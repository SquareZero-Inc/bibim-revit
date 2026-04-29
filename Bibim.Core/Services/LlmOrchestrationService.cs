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
    /// LLM Orchestration Service — provider-agnostic in v1.1.0.
    ///
    /// Owns the agent tool-use loop, Roslyn compile/retry logic, and token tracking.
    /// Delegates HTTP/SSE/format details to an ILlmProvider (Anthropic / OpenAI / Gemini).
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
        /// Convenience: construct using whichever provider/model/key is currently active in config.
        /// Throws if no key is configured for the active provider.
        /// </summary>
        public static LlmOrchestrationService CreateFromActiveConfig(RoslynCompilerService compiler)
        {
            var (provider, apiKey, modelId) = ConfigService.GetActiveCredentials();
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
            int maxTokens = 8192)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            var response = new LlmResponse { RequestId = requestId };

            try
            {
                OnStatusUpdate?.Invoke("Generating response...");

                JArray messages = BuildMessagesArray(history);
                var stream = await _provider.SendStreamingAsync(
                    messages, systemPrompt, OnStreamingDelta, ct, maxTokens);

                response.Text = stream.FullText;
                response.InputTokens = stream.InputTokens;
                response.OutputTokens = stream.OutputTokens;
                response.CachedInputTokens = stream.CachedInputTokens;
                response.CacheCreationInputTokens = stream.CacheCreationInputTokens;
                response.Success = true;

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
                response.ErrorMessage = ex.Message;
                response.IsContextLengthExceeded = IsContextLengthError(ex.Message);
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
        /// <param name="jsonMode">When true, ask the provider for JSON-only output
        /// using its native flag (OpenAI / Gemini). Anthropic ignores this flag
        /// and follows JSON-only instructions in the system prompt.</param>
        public async Task<LlmResponse> SendMessageNonStreamingAsync(
            List<ChatMessage> history,
            string systemPrompt,
            int maxTokens = 1024,
            CancellationToken ct = default,
            bool jsonMode = false)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            var response = new LlmResponse { RequestId = requestId };

            try
            {
                JArray messages = BuildMessagesArray(history);
                var raw = await _provider.SendNonStreamingAsync(messages, systemPrompt, null, ct, maxTokens, jsonMode);

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
                response.Success = true;

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
                response.ErrorMessage = ex.Message;
                response.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                Logger.LogError("LlmOrchestration.NonStreaming", ex);
            }

            return response;
        }

        /// <summary>
        /// Agent tool-use loop with Roslyn-driven self-correction.
        /// Provider-agnostic — works with Anthropic / OpenAI / Gemini via ILlmProvider.
        /// </summary>
        public async Task<CodeGenerationResult> GenerateWithToolsAsync(
            List<ChatMessage> history,
            string systemPrompt,
            JArray toolDefinitions,
            Func<string, string, CancellationToken, Task<string>> toolExecutor,
            int maxTurns = 10,
            string debugDirectory = null,
            CancellationToken ct = default)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var result = new CodeGenerationResult
            {
                RequestId = requestId,
                DebugArtifactDirectory = debugDirectory
            };

            JArray messages = BuildMessagesArray(history);

            try
            {
                for (int turn = 0; turn < maxTurns; turn++)
                {
                    ct.ThrowIfCancellationRequested();

                    OnStatusUpdate?.Invoke(turn == 0 ? "Generating code..." : "Thinking...");
                    Logger.Log("LlmOrchestration",
                        $"rid={requestId} provider={_provider.ProviderName} model={_provider.ModelId} tool-turn={turn}");

                    JObject response;
                    try
                    {
                        // 8192 ceiling. max_tokens is a HARD LIMIT — providers bill by
                        // actual emitted tokens, not by ceiling, so a higher cap costs
                        // nothing when the model emits less. The earlier 4096 cap was
                        // tight for reasoning models (GPT-5.5 / Sonnet 4.6) which often
                        // emit ~3-5k of pre-tool reasoning + tool_use + commentary on
                        // multi-step requests with long history, hitting truncation
                        // mid-response. The Fix B coercion below handles the rare
                        // post-8192 truncation correctly even when it does happen.
                        response = await _provider.SendNonStreamingAsync(
                            messages, systemPrompt, toolDefinitions, ct, 8192);
                    }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"API request failed: {ex.Message}";
                        result.IsContextLengthExceeded = IsContextLengthError(ex.Message);
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

                            // Non-code response (e.g. clarification)
                            result.Success = true;
                            result.IsCodeResponse = false;
                            OnStatusUpdate?.Invoke(null);
                            return result;
                        }

                        result.GeneratedCode = code;
                        result.IsCodeResponse = true;
                        result.CompileAttempts = turn + 1;

                        OnStatusUpdate?.Invoke("Compiling...");
                        var compileResult = _compiler.Compile(code);
                        result.CompilationResult = compileResult;

                        if (compileResult.Success)
                        {
                            result.Success = true;
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} tool-loop done turns={turn + 1} compile=OK");
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
                            int prunedAttempts = PrunePriorCompileAttempts(messages);
                            bool isFirstFailure = prunedAttempts == 0;

                            messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                            messages.Add(new JObject
                            {
                                ["role"] = "user",
                                ["content"] = BuildCompileErrorFeedback(compileResult, isFirstFailure, prunedAttempts)
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

                            OnStatusUpdate?.Invoke($"Using {toolName}...");
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
                                ["name"] = toolName,    // kept for Gemini adapter; stripped by AnthropicProvider
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
                result.ErrorMessage = ex.Message;
                result.IsContextLengthExceeded = IsContextLengthError(ex.Message);
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
                if (userContent == null || !userContent.StartsWith("[COMPILE_ERROR]", StringComparison.Ordinal))
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

        private static bool IsContextLengthError(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("prompt is too long", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("context_length_exceeded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("maximum context length", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
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
        public bool IsContextLengthExceeded { get; set; }
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
        public string DebugArtifactDirectory { get; set; }
        public bool IsContextLengthExceeded { get; set; }
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
}
