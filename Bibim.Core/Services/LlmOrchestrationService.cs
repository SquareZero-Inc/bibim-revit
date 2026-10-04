// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// LLM Orchestration Service — provider-agnostic.
    ///
    /// Owns the agent tool-use loop, Roslyn compile/retry logic, and token tracking.
    /// Delegates HTTP/SSE/format details to an ILlmProvider (Anthropic / OpenAI / Local).
    /// Per-model request rules (thinking, append-only history, fallbacks) come from
    /// <see cref="ModelCatalog"/>.
    ///
    /// Canonical message format inside the loop is Anthropic-shaped: each provider
    /// adapter converts to/from its native shape transparently.
    /// </summary>
    public class LlmOrchestrationService
    {
        // Shared HttpClient — never per-request, never per-provider.
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private readonly ILlmProvider _provider;
        private readonly RoslynCompilerService _compiler;

        public string ProviderName => _provider.ProviderName;
        public string ModelId => _provider.ModelId;

        public event Action<string> OnStreamingDelta;
        public event Action<string> OnStatusUpdate;
        public event Action<TokenUsageInfo> OnTokenUsage;

        /// <summary>
        /// Construct with a pre-built provider. Caller is responsible for
        /// resolving the active provider + key (typically via ConfigService.GetActiveCredentials()
        /// + LlmProviderFactory.Create()).
        /// </summary>
        public LlmOrchestrationService(ILlmProvider provider, RoslynCompilerService compiler)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        }

        /// <summary>
        /// Construct from a model id + matching api key. Picks the right provider via factory.
        /// Compatibility helper for callers that previously took (apiKey, compiler).
        /// </summary>
        public LlmOrchestrationService(string modelId, string apiKey, RoslynCompilerService compiler)
            : this(LlmProviderFactory.Create(modelId, apiKey, _httpClient), compiler)
        {
        }

        /// <summary>
        /// Hosted providers (Anthropic / OpenAI) with the shared HttpClient, an optional
        /// Anthropic endpoint override (self-hosted gateway; null by default) and the prefix cache TTL.
        /// </summary>
        public static LlmOrchestrationService CreateRemote(
            string modelId, string apiKey, RoslynCompilerService compiler,
            string anthropicEndpoint = null, string prefixCacheTtl = "1h")
        {
            return new LlmOrchestrationService(
                LlmProviderFactory.Create(modelId, apiKey, _httpClient, anthropicEndpoint, null, prefixCacheTtl),
                compiler);
        }

        /// <summary>
        /// Construct for the self-hosted local provider. Requires the user-configured
        /// server URL and (optional) server-side model name override. <paramref name="apiKey"/>
        /// may be null/empty for unauthenticated localhost setups (Ollama / LM Studio default).
        /// </summary>
        public LlmOrchestrationService(
            string modelId,
            string apiKey,
            RoslynCompilerService compiler,
            string baseUrl,
            string serverModelName)
            : this(LlmProviderFactory.Create(modelId, apiKey, _httpClient, baseUrl, serverModelName), compiler)
        {
        }

        /// <summary>
        /// Convenience: construct using whichever provider/model/key is currently active in config.
        /// Throws if no key is configured for the active provider (except local — where the
        /// gating is server URL, not key).
        /// </summary>
        public static LlmOrchestrationService CreateFromActiveConfig(RoslynCompilerService compiler)
        {
            var (provider, apiKey, modelId) = ConfigService.GetActiveCredentials();

            if (provider == "local")
            {
                var cfg = ConfigService.GetRagConfig();
                if (string.IsNullOrWhiteSpace(cfg?.LocalServerUrl))
                    throw new InvalidOperationException(
                        "Local LLM server URL not configured. Open Settings → Local LLM.");
                return new LlmOrchestrationService(
                    modelId, apiKey, compiler, cfg.LocalServerUrl, cfg.LocalModelName);
            }

            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException(
                    $"No API key configured for active provider '{provider}'. " +
                    "Open Settings and add the matching key.");
            return new LlmOrchestrationService(modelId, apiKey, compiler);
        }

        /// <summary>
        /// Send a streaming chat message. Delegates SSE handling to the provider.
        /// </summary>
        public async Task<LlmResponse> SendMessageAsync(
            List<ChatMessage> history,
            string systemPrompt,
            CancellationToken ct = default,
            int maxTokens = 16000,
            LlmRequestOptions options = null)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            var response = new LlmResponse { RequestId = requestId };

            try
            {
                OnStatusUpdate?.Invoke(L("Generating response...", "응답 생성 중..."));

                JArray messages = BuildMessagesArray(history);
                var stream = await _provider.SendStreamingAsync(
                    messages, systemPrompt, OnStreamingDelta, ct,
                    ClampMaxTokens(maxTokens), options);

                response.Text = stream.FullText;
                response.InputTokens = stream.InputTokens;
                response.OutputTokens = stream.OutputTokens;
                response.CachedInputTokens = stream.CachedInputTokens;
                response.CacheCreationInputTokens = stream.CacheCreationInputTokens;
                response.Success = true;
                if (stream.StopReason == "refusal")
                {
                    response.Success = false;
                    response.IsRefusal = true;
                    response.ErrorMessage = LlmErrorPresenter.RefusalMessage();
                }

                OnTokenUsage?.Invoke(new TokenUsageInfo
                {
                    RequestId = requestId,
                    Model = _provider.ModelId,
                    InputTokens = stream.InputTokens,
                    OutputTokens = stream.OutputTokens,
                    CachedInputTokens = stream.CachedInputTokens,
                    CacheCreationInputTokens = stream.CacheCreationInputTokens,
                    ElapsedMs = sw.ElapsedMilliseconds
                });

                Logger.Log("LlmOrchestration",
                    $"rid={requestId} provider={_provider.ProviderName} model={_provider.ModelId} " +
                    $"in={stream.InputTokens} out={stream.OutputTokens} " +
                    $"cache_read={stream.CachedInputTokens} cache_create={stream.CacheCreationInputTokens} " +
                    $"ms={sw.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException)
            {
                response.Success = false;
                response.ErrorMessage = "Request cancelled.";
                throw;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                // User-facing text is sanitized+localized; raw detail goes to the log.
                response.ErrorMessage = LlmErrorPresenter.ToUserMessage(ex);
                Logger.LogError("LlmOrchestration.SendMessage", ex);
            }
            finally
            {
                // Always clear progress UI — otherwise an error path leaves the UI stuck "loading".
                OnStatusUpdate?.Invoke(null);
            }

            return response;
        }

        /// <summary>
        /// Non-streaming single-turn call without tools (lightweight requests, e.g. Task Planner).
        /// </summary>
        /// <param name="options">Effort / JSON schema / JSON mode for this call (the
        /// planner passes its response schema here).</param>
        public async Task<LlmResponse> SendMessageNonStreamingAsync(
            List<ChatMessage> history,
            string systemPrompt,
            int maxTokens = 4096,
            CancellationToken ct = default,
            LlmRequestOptions options = null)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            var response = new LlmResponse { RequestId = requestId };

            try
            {
                JArray messages = BuildMessagesArray(history);
                var raw = await _provider.CreateMessageAsync(messages, systemPrompt, null, ct,
                    ClampMaxTokens(maxTokens), options);

                string text = ExtractTextFromContent(raw["content"] as JArray);
                int inTok = raw["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                int outTok = raw["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                int cachedTok = raw["usage"]?["cache_read_input_tokens"]?.Value<int>() ?? 0;
                int cacheCreateTok = raw["usage"]?["cache_creation_input_tokens"]?.Value<int>() ?? 0;

                response.Text = text;
                response.InputTokens = inTok;
                response.OutputTokens = outTok;
                response.CachedInputTokens = cachedTok;
                response.CacheCreationInputTokens = cacheCreateTok;
                response.StopReason = raw["stop_reason"]?.ToString();
                response.Success = true;
                if (response.StopReason == "refusal")
                {
                    response.Success = false;
                    response.IsRefusal = true;
                    response.ErrorMessage = LlmErrorPresenter.RefusalMessage();
                }

                OnTokenUsage?.Invoke(new TokenUsageInfo
                {
                    RequestId = requestId,
                    Model = _provider.ModelId,
                    InputTokens = inTok,
                    OutputTokens = outTok,
                    CachedInputTokens = cachedTok,
                    CacheCreationInputTokens = cacheCreateTok,
                    ElapsedMs = sw.ElapsedMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                response.Success = false;
                response.ErrorMessage = "Request cancelled.";
                throw;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                response.ErrorMessage = LlmErrorPresenter.ToUserMessage(ex);
                Logger.LogError("LlmOrchestration.NonStreaming", ex);
            }

            return response;
        }

        /// <summary>
        /// Agent tool-use loop with Roslyn-driven self-correction.
        /// Provider-agnostic — works with Anthropic / OpenAI / Local via ILlmProvider.
        /// </summary>
        public async Task<CodeGenerationResult> GenerateWithToolsAsync(
            List<ChatMessage> history,
            string systemPrompt,
            JArray toolDefinitions,
            Func<string, string, CancellationToken, Task<string>> toolExecutor,
            int maxTurns = 10,
            string debugDirectory = null,
            CancellationToken ct = default,
            // ── Runtime self-correction (안 A+) ──
            // When supplied, after a successful compile the loop runs this validator
            // (a dry-run preview) and, if it asks to regenerate, feeds the result back
            // and continues — exactly mirroring the existing compile-error feedback loop.
            // null + maxRuntimeRetries=0 ⇒ disabled, behaviour identical to before.
            Func<CompilationResult, CancellationToken, Task<DryRunOutcome>> dryRunValidator = null,
            int maxRuntimeRetries = 0,
            string effort = null)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var modelInfo = ModelCatalog.Describe(_provider.ModelId);
            // Thinking models spend part of max_tokens on reasoning; Anthropic turns stream
            // on the wire, so a large ceiling carries no HTTP-timeout risk.
            int turnMaxTokens = ClampMaxTokens(32000);

            // Live progress: the banner shows what the model is doing inside a turn
            // (reasoning summary / prose / "writing code (N lines)"), throttled so the
            // bridge is not flooded with one message per token.
            var narrator = new ProgressNarrator();
            var progressClock = Stopwatch.StartNew();
            long lastProgressMs = -1000;
            string turnLabel = null;
            var requestOptions = new LlmRequestOptions
            {
                Effort = effort,
                OnProgress = (kind, delta) =>
                {
                    string snippet = narrator.Feed(kind, delta);
                    if (snippet == null || turnLabel == null) return;
                    long now = progressClock.ElapsedMilliseconds;
                    if (now - lastProgressMs < 350) return;
                    lastProgressMs = now;
                    OnStatusUpdate?.Invoke(turnLabel + " · " + snippet);
                }
            };
            var result = new CodeGenerationResult
            {
                RequestId = requestId,
                DebugArtifactDirectory = debugDirectory
            };

            JArray messages = BuildMessagesArray(history);
            int runtimeRetries = 0;
            int compileFailures = 0;
            // Last successfully-compiled candidate. When a retry round ([RUNTIME
            // VALIDATION] / [COMPILE_ERROR]) is answered with PROSE instead of code
            // ("the 0-element result is correct because..."), the working code must
            // not be thrown away — field log 2026-07-13: an export task ended
            // Completed with no Apply button because the explanation was accepted
            // as a terminal non-code answer.
            string lastGoodCode = null;
            CompilationResult lastGoodCompile = null;

            try
            {
                for (int turn = 0; turn < maxTurns; turn++)
                {
                    ct.ThrowIfCancellationRequested();

                    turnLabel = (turn == 0 ? L("Generating code...", "코드 생성 중...") : L("Thinking...", "다음 단계 생각 중...")) + $" ({turn + 1}/{maxTurns})";
                    narrator.Reset();
                    OnStatusUpdate?.Invoke(turnLabel);
                    Logger.Log("LlmOrchestration",
                        $"rid={requestId} provider={_provider.ProviderName} model={_provider.ModelId} tool-turn={turn}");

                    JObject response;
                    try
                    {
                        // turnMaxTokens (32k, clamped per model) is a HARD LIMIT on
                        // thinking + text + tool input together — providers bill emitted
                        // tokens, not the ceiling, so headroom costs nothing. Thinking-by-
                        // default models (Sonnet 5 / Opus 5.x / Fable) truncated at the old
                        // 8192. Anthropic turns stream, so the ceiling has no HTTP-timeout
                        // cost. The tool_use coercion below still covers a truncated turn.
                        response = await _provider.CreateMessageAsync(
                            messages, systemPrompt, toolDefinitions, ct, turnMaxTokens, requestOptions);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                        result.ErrorMessage = LlmErrorPresenter.ToUserMessage(ex);
                        Logger.LogError("LlmOrchestration.GenerateWithToolsAsync", ex);
                        return result;
                    }

                    int inTok = response["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                    int outTok = response["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                    int cachedTok = response["usage"]?["cache_read_input_tokens"]?.Value<int>() ?? 0;
                    int cacheCreateTok = response["usage"]?["cache_creation_input_tokens"]?.Value<int>() ?? 0;
                    result.TotalInputTokens += inTok;
                    result.TotalOutputTokens += outTok;
                    result.TotalCachedInputTokens += cachedTok;
                    result.TotalCacheCreationInputTokens += cacheCreateTok;
                    OnTokenUsage?.Invoke(new TokenUsageInfo
                    {
                        RequestId = requestId,
                        Model = _provider.ModelId,
                        InputTokens = inTok,
                        OutputTokens = outTok,
                        CachedInputTokens = cachedTok,
                        CacheCreationInputTokens = cacheCreateTok,
                        ElapsedMs = 0
                    });

                    CodegenDebugRecorder.WriteJson(debugDirectory,
                        $"tool_turn_{turn:00}_response.json", response);

                    string stopReason = response["stop_reason"]?.ToString();
                    var content = response["content"] as JArray ?? new JArray();

                    // ── BIBIM-007 — Defensive tool_use coercion ──
                    // Providers may emit a complete tool_use block then truncate
                    // trailing text — both Anthropic and OpenAI then reject the
                    // next turn unless the tool_use is paired with a tool_result.
                    // If we honored the truncation signal naively, we'd push the
                    // assistant content (with tool_use) followed by a plain
                    // "continue" user message, which fails 400 on the next call.
                    //
                    // Whenever content contains any tool_use block, force the
                    // tool_use branch regardless of the reported stop_reason —
                    // executing the tool produces the required pairing and the
                    // model's next response can pick up from the tool_result.
                    bool hasToolUse = false;
                    foreach (var block in content)
                    {
                        if (block is JObject blockObj &&
                            blockObj["type"]?.ToString() == "tool_use")
                        {
                            hasToolUse = true;
                            break;
                        }
                    }
                    if (hasToolUse && stopReason != "tool_use")
                    {
                        Logger.Log("LlmOrchestration",
                            $"rid={requestId} turn={turn} provider reported stop_reason={stopReason} " +
                            "but content has tool_use blocks; coercing to tool_use to preserve pairing");
                        stopReason = "tool_use";
                    }

                    // ── refusal: a safety classifier (or the model) declined and any
                    // server-side fallback declined too. Never treat as code/clarification.
                    if (stopReason == "refusal")
                    {
                        Logger.Log("LlmOrchestration",
                            $"rid={requestId} turn={turn} stop_reason=refusal details={response["stop_details"]?.ToString(Newtonsoft.Json.Formatting.None)}");
                        result.Success = false;
                        result.IsRefusal = true;
                        result.ErrorMessage = LlmErrorPresenter.RefusalMessage();
                        OnStatusUpdate?.Invoke(null);
                        return result;
                    }

                    // ── end_turn / max_tokens: model finished or was truncated ──
                    if (stopReason == "end_turn" || stopReason == "max_tokens")
                    {
                        if (stopReason == "max_tokens")
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} turn={turn} response truncated by max_tokens");

                        string finalText = ExtractTextFromContent(content);
                        string code = ExtractCSharpCode(finalText);

                        result.RawResponse = finalText;

                        if (string.IsNullOrWhiteSpace(code))
                        {
                            // max_tokens truncation mid-generation — request continuation
                            if (stopReason == "max_tokens" && turn < maxTurns - 1)
                            {
                                messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                                messages.Add(new JObject
                                {
                                    ["role"] = "user",
                                    ["content"] = "[CONTINUATION_REQUIRED] Your response was cut off. " +
                                        "Continue EXACTLY where you left off. Do NOT repeat code already generated."
                                });
                                continue;
                            }

                            // Non-code response. If a retry round already produced
                            // working code, the model is explaining ("result is
                            // correct as-is") rather than clarifying — deliver the
                            // last good code so the preview→Apply flow proceeds.
                            if (lastGoodCompile != null && (runtimeRetries > 0 || compileFailures > 0))
                            {
                                Logger.Log("LlmOrchestration",
                                    $"rid={requestId} non-code reply after retry — falling back to last compiled code");
                                result.GeneratedCode = lastGoodCode;
                                result.CompilationResult = lastGoodCompile;
                                result.IsCodeResponse = true;
                                result.Success = true;
                                OnStatusUpdate?.Invoke(null);
                                return result;
                            }

                            // Non-code response (e.g. clarification)
                            result.Success = true;
                            result.IsCodeResponse = false;
                            OnStatusUpdate?.Invoke(null);
                            return result;
                        }

                        result.GeneratedCode = code;
                        result.IsCodeResponse = true;
                        result.CompileAttempts = turn + 1;

                        OnStatusUpdate?.Invoke(L("Compiling...", "코드 컴파일 검사 중...") + $" ({turn + 1}/{maxTurns})");
                        var compileResult = _compiler.Compile(code);
                        result.CompilationResult = compileResult;

                        if (compileResult.Success)
                        {
                            lastGoodCode = code;
                            lastGoodCompile = compileResult;
                            // ── Runtime self-correction (안 A+) ──
                            // Compile OK ≠ runtime OK. If a validator is wired, run a
                            // dry-run preview; if it reports the result is wrong (runtime
                            // exception / 0 elements / missing step), feed it back and
                            // regenerate — same mechanism as the compile-error loop above.
                            // Bounded by maxRuntimeRetries so it can never loop forever,
                            // and still inside the outer maxTurns budget.
                            // Validator runs on EVERY successful compile (so the captured
                            // dry-run always matches the FINAL code, letting the caller
                            // reuse it as the preview instead of running dry-run twice).
                            // Only the *regeneration* is bounded by maxRuntimeRetries.
                            if (dryRunValidator != null)
                            {
                                // NOTE: both labels are matched by frontend LoadingModal.
                                // REVIT_EXECUTING_LABELS (Revit is executing — Stop must not
                                // abort). Keep in sync when rewording.
                                OnStatusUpdate?.Invoke(L("Validating (preview)...", "미리 검증 실행 중..."));
                                DryRunOutcome outcome = null;
                                try
                                {
                                    outcome = await dryRunValidator(compileResult, ct);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception vex)
                                {
                                    // Validator failure must never block code delivery —
                                    // fall through and return the compiled code as-is.
                                    Logger.LogError("LlmOrchestration.dryRunValidator", vex);
                                }

                                if (outcome != null && outcome.ShouldRegenerate
                                    && runtimeRetries < maxRuntimeRetries)
                                {
                                    runtimeRetries++;
                                    Logger.Log("LlmOrchestration",
                                        $"rid={requestId} runtime validation → regenerate " +
                                        $"(retry {runtimeRetries}/{maxRuntimeRetries})");
                                    CodegenDebugRecorder.WriteText(debugDirectory,
                                        $"tool_turn_{turn:00}_runtime_validation.txt",
                                        outcome.FeedbackText ?? "(no feedback text)");

                                    // Prune prior retry rounds (compile OR runtime) the same
                                    // way the compile path does — without this, runtime-retry
                                    // pairs re-sent the full failed code every following turn.
                                    // Append-only models (preserved thinking) must never have
                                    // earlier turns removed: their thinking blocks would be
                                    // invalidated. The cached re-read is cheap on those models.
                                    if (!modelInfo.AppendOnlyHistory)
                                        PrunePriorCompileAttempts(messages);

                                    messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                                    messages.Add(new JObject
                                    {
                                        ["role"] = "user",
                                        ["content"] = outcome.FeedbackText ?? "The preview result looks wrong. Please review and regenerate."
                                    });
                                    continue;
                                }
                            }

                            result.Success = true;
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} tool-loop done turns={turn + 1} compile=OK" +
                                (runtimeRetries > 0 ? $" runtime-retries={runtimeRetries}" : ""));
                            OnStatusUpdate?.Invoke(null);
                            return result;
                        }

                        if (turn < maxTurns - 1)
                        {
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} turn={turn} compile failed, feeding error back");

                            // Prune prior failed-attempt turns so we keep only the latest
                            // failed code + latest feedback. Older attempts are summarised
                            // into a single short marker so the model still knows it's
                            // already retried, without re-sending the full failed code each turn.
                            //
                            // isFirstFailure must track COMPILE failures, not pruned pairs:
                            // the prune also removes [RUNTIME VALIDATION] rounds, and the
                            // first real compile failure still needs the full Rules block
                            // (and must not be mislabeled "retry attempt #2").
                            compileFailures++;
                            if (!modelInfo.AppendOnlyHistory)
                                PrunePriorCompileAttempts(messages);
                            bool isFirstFailure = compileFailures == 1;

                            messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                            messages.Add(new JObject
                            {
                                ["role"] = "user",
                                ["content"] = BuildCompileErrorFeedback(compileResult, isFirstFailure, compileFailures - 1)
                            });
                            continue;
                        }

                        result.Success = false;
                        result.ErrorMessage = $"Final compilation failed:\n{compileResult.ErrorSummary}";
                        Logger.Log("LlmOrchestration",
                            $"rid={requestId} tool-loop exhausted turns={turn + 1} compile=FAIL");
                        OnStatusUpdate?.Invoke(null);
                        return result;
                    }

                    // ── tool_use: execute each tool, feed results back ──
                    if (stopReason == "tool_use")
                    {
                        messages.Add(new JObject
                        {
                            ["role"] = "assistant",
                            ["content"] = content
                        });

                        var toolResults = new JArray();
                        foreach (JObject block in content)
                        {
                            if (block["type"]?.ToString() != "tool_use") continue;

                            string toolId = block["id"]?.ToString();
                            string toolName = block["name"]?.ToString();
                            string toolInput = block["input"]?.ToString(Newtonsoft.Json.Formatting.None) ?? "{}";

                            if (string.IsNullOrEmpty(toolId) || string.IsNullOrEmpty(toolName))
                            {
                                Logger.Log("LlmOrchestration",
                                    $"rid={requestId} malformed tool_use block: id={toolId ?? "null"}, name={toolName ?? "null"} — skipping");
                                continue;
                            }

                            OnStatusUpdate?.Invoke(GetToolStatusLabel(toolName) + $" ({turn + 1}/{maxTurns})");
                            Logger.Log("LlmOrchestration", $"rid={requestId} tool={toolName}");

                            string toolOutput;
                            try
                            {
                                toolOutput = await toolExecutor(toolName, toolInput, ct);
                            }
                            catch (Exception ex)
                            {
                                toolOutput = $"[Tool Error] {ex.Message}";
                                Logger.LogError($"LlmOrchestration.tool.{toolName}", ex);
                            }

                            CodegenDebugRecorder.WriteText(debugDirectory,
                                $"tool_turn_{turn:00}_{toolName}_result.txt", toolOutput);

                            toolResults.Add(new JObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = toolId,
                                ["content"] = toolOutput
                            });
                        }

                        if (toolResults.Count == 0)
                        {
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} turn={turn} stop_reason=tool_use but no valid tool blocks found");
                            result.Success = false;
                            result.ErrorMessage = "Provider returned tool_use stop reason but no valid tool blocks were found.";
                            OnStatusUpdate?.Invoke(null);
                            return result;
                        }

                        messages.Add(new JObject
                        {
                            ["role"] = "user",
                            ["content"] = toolResults
                        });

                        continue;
                    }

                    // Unexpected stop reason
                    result.Success = false;
                    result.ErrorMessage = $"Unexpected stop_reason: {stopReason}";
                    OnStatusUpdate?.Invoke(null);
                    return result;
                }

                result.Success = false;
                result.ErrorMessage = $"Tool loop exceeded max_turns ({maxTurns}).";
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "Cancelled.";
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                result.ErrorMessage = LlmErrorPresenter.ToUserMessage(ex);
                Logger.LogError("LlmOrchestration.GenerateWithToolsAsync", ex);
            }
            finally
            {
                // Always clear progress UI on exit (success / error / cancel).
                OnStatusUpdate?.Invoke(null);
            }

            return result;
        }

        // ───────────────────────────── helpers ─────────────────────────────

        private static JArray BuildMessagesArray(IEnumerable<ChatMessage> history)
        {
            var arr = new JArray();
            if (history == null) return arr;
            foreach (var m in history)
            {
                arr.Add(new JObject
                {
                    ["role"] = m.IsUser ? "user" : "assistant",
                    ["content"] = m.Text ?? string.Empty
                });
            }
            return arr;
        }

        private static string ExtractTextFromContent(JArray content)
        {
            var sb = new StringBuilder();
            if (content == null) return string.Empty;
            foreach (JObject block in content)
            {
                if (block["type"]?.ToString() == "text")
                    sb.Append(block["text"]?.ToString());
            }
            return sb.ToString();
        }

        private static string ExtractCSharpCode(string responseText)
        {
            if (string.IsNullOrEmpty(responseText)) return null;

            string[] markers = { "```csharp", "```cs", "```C#" };
            foreach (var marker in markers)
            {
                int start = responseText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start = responseText.IndexOf('\n', start);
                if (start < 0) continue;
                start++;
                int end = responseText.IndexOf("```", start, StringComparison.Ordinal);
                if (end < 0) end = responseText.Length;
                return responseText.Substring(start, end - start).Trim();
            }
            return null;
        }

        private string BuildCompileErrorFeedback(CompilationResult result, bool includeRules = true, int priorAttempts = 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[COMPILE_ERROR] The generated C# code failed Roslyn compilation. Fix the errors below and regenerate ONLY the corrected code.");
            if (priorAttempts > 0)
                sb.AppendLine($"(This is retry attempt #{priorAttempts + 1} — earlier attempts were pruned to save context.)");
            sb.AppendLine();
            sb.AppendLine(result.ErrorSummary);

            if (includeRules)
            {
                // Rules only need to be sent once — on the FIRST compile failure.
                // On subsequent retries the model already has them in context.
                sb.AppendLine();
                sb.AppendLine("Rules:");
                sb.AppendLine("- Fix ONLY the compilation errors listed above");
                sb.AppendLine("- Do NOT change the logic or add new features");
                sb.AppendLine("- Return ONLY a ```csharp``` block containing statements for the body of Execute(UIApplication uiApp)");
                sb.AppendLine("- Do NOT include using directives, namespace, class, method signature, or explanation");
                sb.AppendLine("- NEVER re-declare app, doc, or uidoc — they already exist in scope");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Removes prior failed compile attempts from the messages array so we don't
        /// re-send several rounds of failed code on each retry. Each "attempt" is the
        /// pair: assistant turn carrying the failed code + the user feedback that
        /// follows it. Returns the number of attempts pruned (0 if this is the first
        /// failure). The very first user message (the original task prompt) and any
        /// tool_use / tool_result turns are preserved untouched.
        /// </summary>
        private static int PrunePriorCompileAttempts(JArray messages)
        {
            int pruned = 0;
            // Walk from the end backwards. A pair we want to drop is:
            //   user: "[COMPILE_ERROR] ..."  (the feedback we previously appended)
            //   assistant: <text content with the failed ```csharp``` block>
            // We only drop assistant turns that look like plain text (no tool_use blocks).
            for (int i = messages.Count - 1; i >= 1; i--)
            {
                var userMsg = messages[i] as JObject;
                if (userMsg == null) break;
                if (userMsg["role"]?.ToString() != "user") break;

                string userContent = userMsg["content"]?.Type == JTokenType.String
                    ? userMsg["content"].ToString()
                    : string.Empty;
                bool isRetryFeedback =
                    userContent != null &&
                    (userContent.StartsWith("[COMPILE_ERROR]", StringComparison.Ordinal) ||
                     userContent.StartsWith("[RUNTIME VALIDATION]", StringComparison.Ordinal));
                if (!isRetryFeedback)
                    break;

                var assistantMsg = messages[i - 1] as JObject;
                if (assistantMsg == null) break;
                if (assistantMsg["role"]?.ToString() != "assistant") break;
                if (!IsPlainTextAssistantContent(assistantMsg["content"])) break;

                messages.RemoveAt(i);          // user feedback
                messages.RemoveAt(i - 1);      // assistant failed-code reply
                pruned++;
                i--; // step over the now-removed pair
            }
            return pruned;
        }

        private static bool IsPlainTextAssistantContent(JToken content)
        {
            if (content == null) return false;
            if (content.Type == JTokenType.String) return true;
            if (content is JArray arr)
            {
                foreach (JObject block in arr)
                {
                    string t = block["type"]?.ToString();
                    if (t != "text") return false; // tool_use → keep, never prune
                }
                return true;
            }
            return false;
        }


        /// <summary>Language-aware literal for progress-banner status strings.
        /// These bypass the UI i18n table (they originate here in C#), so localize
        /// at the source — English status text on a Korean panel reads as broken.</summary>
        private static string L(string en, string kr) => AppLanguage.Pick(en, kr);

        /// <summary>Friendly progress label per tool — never leak internal tool names
        /// like "search_revit_api" to the banner.</summary>
        private static string GetToolStatusLabel(string toolName)
        {
            switch (toolName)
            {
                case "search_revit_api": return L("Searching Revit API docs...", "Revit API 자료 검색 중...");
                case "run_roslyn_check": return L("Checking the code...", "코드 검사 중...");
                case "search_code_library": return L("Searching the Code Library...", "코드 보관함 검색 중...");
                case "count_elements":
                case "list_elements": return L("Querying model elements...", "모델 요소 조회 중...");
                default:                 return L("Reading model context...", "모델 정보 확인 중...");
            }
        }

        // Public: the planner-failure gate in BibimDockablePanelProvider reuses this
        // to route context-length failures to "start a new session" guidance.
        public static bool IsContextLengthError(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("prompt is too long", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("context_length_exceeded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("maximum context length", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Respect the model's output ceiling (local servers are small).</summary>
        private int ClampMaxTokens(int requested)
        {
            int ceiling = ModelCatalog.Describe(_provider.ModelId).MaxOutputTokens;
            return ceiling > 0 ? Math.Min(requested, ceiling) : requested;
        }

        private static HttpClient CreateHttpClient()
        {
            // Non-streaming calls (planner, OpenAI tool turns) are bounded here; long
            // Anthropic tool turns stream and are guarded by the SSE idle timeout instead.
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            return client;
        }
    }

    // ───────────────────────────── result types ─────────────────────────────

    public class LlmResponse
    {
        public string RequestId { get; set; }
        public bool Success { get; set; }
        public string Text { get; set; }
        public string ErrorMessage { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public int CacheCreationInputTokens { get; set; }

        /// <summary>
        /// Total input the provider actually processed: fresh + cache-read +
        /// cache-creation. InputTokens alone excludes the cache line items and
        /// structurally UNDERCOUNTS real usage — use this for anything shown to
        /// the user or logged for cost accounting.
        /// </summary>
        public int ProcessedInputTokens => InputTokens + CachedInputTokens + CacheCreationInputTokens;

        public bool IsContextLengthExceeded { get; set; }

        /// <summary>Provider stop reason for non-streaming calls.</summary>
        public string StopReason { get; set; }

        /// <summary>The model / safety classifier declined (stop_reason "refusal").</summary>
        public bool IsRefusal { get; set; }
    }

    public class CodeGenerationResult
    {
        public string RequestId { get; set; }
        public bool Success { get; set; }
        public bool IsCodeResponse { get; set; }
        public string RawResponse { get; set; }
        public string GeneratedCode { get; set; }
        public string ErrorMessage { get; set; }
        public CompilationResult CompilationResult { get; set; }
        public int CompileAttempts { get; set; }
        public int TotalInputTokens { get; set; }
        public int TotalOutputTokens { get; set; }
        public int TotalCachedInputTokens { get; set; }
        public int TotalCacheCreationInputTokens { get; set; }

        /// <summary>See LlmResponse.ProcessedInputTokens — same rule, loop totals.</summary>
        public int TotalProcessedInputTokens => TotalInputTokens + TotalCachedInputTokens + TotalCacheCreationInputTokens;
        public string DebugArtifactDirectory { get; set; }
        public bool IsContextLengthExceeded { get; set; }

        /// <summary>The model / safety classifier declined (stop_reason "refusal").</summary>
        public bool IsRefusal { get; set; }
    }

    public class TokenUsageInfo
    {
        public string RequestId { get; set; }
        public string Model { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public int CacheCreationInputTokens { get; set; }
        public long ElapsedMs { get; set; }
    }

    /// <summary>
    /// Result of a runtime self-correction validation pass (안 A+).
    /// Produced by the dryRunValidator callback that GenerateWithToolsAsync runs
    /// after a successful compile. The orchestrator only reads ShouldRegenerate +
    /// FeedbackText; the actual dry-run ExecutionResult stays inside the caller's
    /// closure (BibimDockablePanelProvider) so this layer never depends on the
    /// Revit execution types.
    /// </summary>
    public sealed class DryRunOutcome
    {
        /// <summary>True ⇒ feed FeedbackText back to the model and regenerate.</summary>
        public bool ShouldRegenerate { get; set; }

        /// <summary>The runtime-validation message handed to the model on regenerate.</summary>
        public string FeedbackText { get; set; }

        /// <summary>True if a dry-run actually executed (false if skipped by a guard).</summary>
        public bool Ran { get; set; }
    }
}
