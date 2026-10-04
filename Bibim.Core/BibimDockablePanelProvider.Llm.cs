// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.UI;
using Microsoft.Web.WebView2.Wpf;

namespace Bibim.Core
{
    // Part of BibimDockablePanelProvider (split by concern in v1.2.0; members moved verbatim).
    // LLM services and chat flow: provider construction, history window, planner routing, direct chat, tool service.
    public partial class BibimDockablePanelProvider
    {
        /// <summary>
        /// Ensure LlmOrchestrationService is initialized with a valid API key.
        /// Lazy-init so we don't fail at panel creation time if config isn't ready.
        /// </summary>
        private LlmOrchestrationService EnsureLlmService()
        {
            if (_llmService != null) return _llmService;

            var (provider, apiKey, modelId) = ConfigService.GetActiveCredentials();
            var compiler = GetCompiler();

            if (provider == "local")
            {
                // Local self-hosted: gating is server URL, not API key (key optional).
                var cfg = ConfigService.GetRagConfig();
                if (string.IsNullOrWhiteSpace(cfg?.LocalServerUrl))
                    throw new InvalidOperationException(
                        "Local LLM server URL not configured. " +
                        $"Open Settings → Local LLM and enter your server URL for model '{modelId}'.");
                _llmService = new LlmOrchestrationService(
                    modelId, apiKey, compiler, cfg.LocalServerUrl, cfg.LocalModelName);
            }
            else
            {
                if (string.IsNullOrEmpty(apiKey))
                    throw new InvalidOperationException(
                        $"API key for provider '{provider}' not configured. " +
                        $"Open Settings and add the matching key for model '{modelId}'.");
                _llmService = LlmOrchestrationService.CreateRemote(modelId, apiKey, compiler,
                    ConfigService.GetAnthropicEndpoint(),
                    ConfigService.GetRagConfig()?.PromptCacheTtl ?? "1h");
            }

            // Wire streaming events to bridge
            _llmService.OnStreamingDelta += (delta) =>
            {
                if (Volatile.Read(ref _suppressStreamingDeltaCount) == 0)
                    _bridge.PostMessage("streaming_delta", delta);
            };

            _llmService.OnStatusUpdate += (status) =>
            {
                if (status != null)
                    _bridge.PostMessage("progress", new[] {
                        new { label = status, status = "active" }
                    });
                else
                    _bridge.PostMessage("progress", new object[0]); // Clear progress UI
            };

            _llmService.OnTokenUsage += (info) =>
            {
                TokenTracker.Track("chat", _llmService.ProviderName, info.Model,
                    info.InputTokens, info.OutputTokens, info.RequestId,
                    info.CachedInputTokens, info.CacheCreationInputTokens);
            };

            Logger.Log("BibimDockablePanel",
                $"LlmOrchestrationService initialized — provider={provider} model={modelId}");
            return _llmService;
        }

        /// <summary>
        /// Lightweight LLM instance for the Task Planner.
        /// No streaming delta events — only token usage is tracked.
        /// </summary>
        private LlmOrchestrationService EnsurePlannerService()
        {
            if (_plannerLlmService != null) return _plannerLlmService;

            var (provider, apiKey, modelId) = ConfigService.GetActiveCredentials();
            var compiler = GetCompiler();

            // Optional planner-model routing: classification/question generation is a
            // Haiku-class job — running it on the user's premium codegen model wastes
            // ~2.5k input tokens per turn at premium rates. Same-provider overrides
            // reuse the already-resolved key (works with the env-var key
            // too); cross-provider needs its own key; "local" targets are excluded
            // (different construction path below).
            string plannerOverride = ConfigService.GetRagConfig()?.PlannerModel;
            if (!string.IsNullOrWhiteSpace(plannerOverride) &&
                !string.Equals(plannerOverride, modelId, StringComparison.OrdinalIgnoreCase))
            {
                // Guard rails: (a) only KNOWN model ids — a typo would otherwise 404
                // every planner call with nothing in the UI naming the bad config key;
                // (b) never route to "local" here (different construction path).
                bool knownModel = ConfigService.AvailableModels.Any(m =>
                    string.Equals(m.Id, plannerOverride, StringComparison.OrdinalIgnoreCase));
                string ovProvider = LlmProviderFactory.ResolveProviderForModel(plannerOverride);
                if (!knownModel || ovProvider == "local" || string.IsNullOrEmpty(ovProvider))
                {
                    Logger.Log("TaskPlanner",
                        $"planner_model '{plannerOverride}' ignored (unknown model or unsupported provider '{ovProvider}')");
                }
                else if (string.Equals(ovProvider, provider, StringComparison.OrdinalIgnoreCase))
                {
                    modelId = plannerOverride;
                    Logger.Log("TaskPlanner", $"planner model override → {modelId} (same provider)");
                }
                else
                {
                    string ovKey = ConfigService.GetApiKeyForProvider(ovProvider);
                    if (!string.IsNullOrWhiteSpace(ovKey))
                    {
                        provider = ovProvider; apiKey = ovKey; modelId = plannerOverride;
                        Logger.Log("TaskPlanner", $"planner model override → {modelId} ({provider})");
                    }
                    else
                    {
                        Logger.Log("TaskPlanner", $"planner_model '{plannerOverride}' ignored — no key for provider '{ovProvider}'");
                    }
                }
            }

            if (provider == "local")
            {
                var cfg = ConfigService.GetRagConfig();
                if (string.IsNullOrWhiteSpace(cfg?.LocalServerUrl))
                    throw new InvalidOperationException(
                        "Local LLM server URL not configured. " +
                        $"Open Settings → Local LLM and enter your server URL for model '{modelId}'.");
                _plannerLlmService = new LlmOrchestrationService(
                    modelId, apiKey, compiler, cfg.LocalServerUrl, cfg.LocalModelName);
            }
            else
            {
                if (string.IsNullOrEmpty(apiKey))
                    throw new InvalidOperationException(
                        $"API key for provider '{provider}' not configured. " +
                        $"Open Settings and add the matching key for model '{modelId}'.");
                _plannerLlmService = LlmOrchestrationService.CreateRemote(modelId, apiKey, compiler,
                    ConfigService.GetAnthropicEndpoint(),
                    ConfigService.GetRagConfig()?.PromptCacheTtl ?? "1h");
            }
            _plannerLlmService.OnTokenUsage += (info) =>
            {
                TokenTracker.Track("task_planner", _plannerLlmService.ProviderName, info.Model,
                    info.InputTokens, info.OutputTokens, info.RequestId,
                    info.CachedInputTokens, info.CacheCreationInputTokens);
            };

            return _plannerLlmService;
        }

        /// <summary>
        /// Returns a windowed slice of _chatHistory for LLM calls.
        /// Keeps the most recent ChatHistoryMaxTurns messages and prepends a
        /// single synthetic "[Earlier session context]" turn that summarises any
        /// turns that were dropped — so the model still has a bird's-eye view
        /// of long sessions without paying the per-turn token cost.
        /// The full history is preserved in _chatHistory for session persistence.
        /// </summary>
        private List<ChatMessage> GetHistoryWindow()
        {
            lock (_chatHistoryLock)
            {
                // Stepped window (HistoryWindowPolicy): the start index only moves every
                // few turns, so consecutive requests share a byte-identical prefix and
                // the conversation stays prompt-cached instead of re-billed each turn.
                int start = HistoryWindowPolicy.StartIndex(
                    _chatHistory.Count, ChatHistoryMaxTurns, BibimConstants.ChatHistoryWindowStep);
                if (start <= 0)
                    return _chatHistory.ToList();

                var kept = _chatHistory.Skip(start).ToList();
                string cacheKey = $"{_activeSessionId}:{start}";
                string summary;
                if (string.Equals(_historySummaryCacheKey, cacheKey, StringComparison.Ordinal))
                {
                    summary = _historySummaryCacheText;
                }
                else
                {
                    summary = HistorySummariser.Summarise(_chatHistory.Take(start).ToList(), _sessionContext);
                    _historySummaryCacheKey = cacheKey;
                    _historySummaryCacheText = summary;
                }

                if (string.IsNullOrEmpty(summary))
                    return kept;

                var window = new List<ChatMessage>(kept.Count + 1)
                {
                    new ChatMessage { Text = summary, IsUser = true }
                };
                window.AddRange(kept);
                return window;
            }
        }

        /// <summary>
        /// Chat using LlmOrchestrationService with real SDK streaming.
        /// Sends streaming_delta events for real-time UI updates,
        /// then streaming_end with final text + token usage.
        /// Also persists messages to the local session.
        /// 
        /// Integrates:
        ///   - CodeGenSystemPrompt.Build(revitVersion) for Revit-aware prompts
        ///   - @context tag resolution via RevitContextProvider
        ///   - RAG document injection via CodeGenSystemPrompt.AppendRagContext
        /// </summary>
        private async Task ChatWithLlmServiceAsync(string userText, AttachedDocument attachment = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Cancel any previous streaming
            var ct = ReplaceCts();

            try
            {
                EnsureActiveSession();
                // @tags resolve against the user's instruction only — never inside the document.
                string resolvedText = ResolveContextTags(userText);
                TaskPlanResponse plan = null;

                // Heuristic gate: greetings / acks / very short non-actionable messages
                // never need the ~2,500-token planner system prompt. Skip the LLM call
                // entirely and route straight to chat. Only safe when there is no
                // active task that the user might be answering.
                var activeTask = GetActiveTask();
                bool hasActiveTask = activeTask != null && !IsTaskTerminal(activeTask);
                bool skipPlanner = attachment == null && PlannerGate.ShouldSkipPlanner(userText, hasActiveTask);
                string plannerHaltMessage = null;

                if (skipPlanner)
                {
                    Logger.Log("TaskPlanner",
                        $"Gate skipped LLM call (msg=\"{userText?.Substring(0, Math.Min(userText?.Length ?? 0, 24))}...\")");
                }
                else
                {
                    // The planner round-trip (seconds on premium models) used to run
                    // with an empty progress banner — the first "silent gap" users hit.
                    _bridge?.PostMessage("progress", new[] {
                        new { label = UiText("Analyzing your request...", "요청 분석 중..."), status = "active" }
                    });
                    try
                    {
                        plan = await PlanUserIntentAsync(userText, resolvedText, ct, attachment);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception planEx)
                    {
                        // Safety gate: a WRITE-intent request must not silently fall
                        // back to ungated direct chat (a chat answer can read as "done"
                        // without the preview→confirm flow). Read questions still
                        // degrade to chat. The halt itself happens AFTER the shared
                        // history-persist lines below, so gated messages are stored
                        // exactly like every other user message.
                        plannerHaltMessage = BuildPlannerFailureHalt(userText, planEx);
                        if (plannerHaltMessage == null)
                            Logger.Log("TaskPlanner", $"Falling back to direct chat: {planEx.Message}");
                    }
                    finally
                    {
                        // Always clear the planning banner — including on cancel
                        // (the rethrown OCE above), which otherwise left the spinner
                        // and the disabled input stuck forever (frontend only clears
                        // steps on a backend progress:[] message). Follow-up stages
                        // post their own first status immediately, so there is no
                        // "looks idle" hole after a successful plan.
                        _bridge?.PostMessage("progress", new object[0]);
                    }
                }

                // In-memory history keeps the document so follow-up chat can refer to it; the
                // saved session gets only "📎 name" + the instruction.
                string historyText = attachment != null ? attachment.ComposeForLlm(resolvedText) : resolvedText;
                lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = historyText, IsUser = true });
                _sessionManager?.AddMessage(_activeSessionId, "user", "text",
                    attachment != null ? attachment.DisplayMarker(userText) : userText);
                SendSessionList();

                if (plannerHaltMessage != null)
                {
                    SendAssistantMessage(plannerHaltMessage, "text", messageType: "error");
                    return;
                }

                if (plan == null || string.Equals(plan.Mode, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    if (plan != null && !string.IsNullOrWhiteSpace(plan.AssistantMessage))
                    {
                        SendAssistantMessage(plan.AssistantMessage, "text",
                            plan.PlannerInputTokens, plan.PlannerOutputTokens);
                    }
                    else
                    {
                        await SendDirectChatResponseAsync(userText, resolvedText, sw, ct);
                    }

                    return;
                }

                await HandlePlannedTaskAsync(plan, userText, ct, attachment);
            }
            catch (OperationCanceledException)
            {
                _bridge.PostMessage("streaming_end", new
                {
                    text = UiText("Request cancelled.", "요청이 취소되었습니다."),
                    type = "system",
                    inputTokens = 0,
                    outputTokens = 0,
                    elapsedMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("ChatWithLlmService", ex);
                _bridge.PostMessage("streaming_end", new
                {
                    text = LlmErrorPresenter.ToUserMessage(ex),
                    type = "error",
                    inputTokens = 0,
                    outputTokens = 0,
                    elapsedMs = sw.ElapsedMilliseconds
                });
            }
        }

        private async Task SendDirectChatResponseAsync(
            string userText, string resolvedText,
            System.Diagnostics.Stopwatch sw,
            CancellationToken ct)
        {
            // Post a status immediately — the planning banner was just cleared and
            // the RAG/system-prompt build below can take seconds; without this the
            // panel looks idle (input re-enables) mid-flow.
            _bridge?.PostMessage("progress", new[] {
                new { label = UiText("Preparing response...", "응답 준비 중..."), status = "active" }
            });
            var llm = EnsureLlmService();
            string systemPrompt = await BuildSystemPromptWithRagAsync(resolvedText, false, ct);
            // 16k: thinking-by-default models spend part of the ceiling on reasoning.
            var response = await llm.SendMessageAsync(GetHistoryWindow(), systemPrompt, ct,
                maxTokens: 16000,
                options: new LlmRequestOptions { Effort = ConfigService.GetRagConfig()?.EffortChat });

            if (response.Success)
            {
                SendAssistantMessage(response.Text, "text",
                    response.ProcessedInputTokens, response.OutputTokens);
                return;
            }

            string chatErrMsg = response.IsContextLengthExceeded
                ? UiText("The conversation is too long. Please start a new session and try again.",
                         "대화가 너무 길어졌습니다. 새 세션을 시작한 뒤 다시 시도해 주세요.")
                : response.ErrorMessage;

            // Use SendAssistantMessage so the error is saved to _chatHistory and _sessionManager
            SendAssistantMessage(chatErrMsg, "text", messageType: "error", elapsedMs: sw.ElapsedMilliseconds);
        }

        private async Task HandlePlannedTaskAsync(TaskPlanResponse plan, string userText, CancellationToken ct,
            AttachedDocument attachment = null)
        {
            if (string.Equals(plan.TaskRelation, "ask", StringComparison.OrdinalIgnoreCase))
            {
                // F-5: Instead of blocking the user with a meta-question ("update or new?"),
                // treat ambiguous intent as a new task and notify inline.
                // The user can always say "이전 작업에 추가해줘" to redirect.
                string noticeText = UiText(
                    "Starting as a new task — say \"add to the previous task\" if you meant to continue it.",
                    "새 작업으로 진행합니다 — 이전 작업에 이어붙이려면 \"이전 작업에 추가해줘\"라고 말씀해주세요.");
                _bridge.PostMessage("system_message", noticeText);
                plan.TaskRelation = "new";
                // Fall through to new-task handling below.
            }

            var activeTask = GetActiveTask();
            bool updateExisting =
                activeTask != null &&
                !IsTaskTerminal(activeTask) &&
                string.Equals(plan.TaskRelation, "update", StringComparison.OrdinalIgnoreCase);

            var task = updateExisting ? activeTask : CreateTask(plan, userText);
            if (updateExisting)
                AppendTaskUserInput(task, userText);
            ApplyPlanToTask(task, plan, userText);
            if (attachment != null)
                task.Attachment = attachment;   // a new attachment replaces the task's previous one

            if (IsBuiltInCurrentContextSummaryTaskV2(task))
            {
                ApplyCurrentContextSummaryDefaults(task);
            }

            ApplyParameterCreationGuard(task);

            // Spec §4-2 rule 3: before anything runs, show what the document asks for —
            // target count, rules, current → expected (deterministic, from the parsed table)
            // followed by the planner's own summary.
            bool hasQuestions = task.Questions != null && task.Questions.Count > 0;
            // Planner tokens are reported once: on the question/review message when one
            // follows, otherwise on the digest.
            bool followUpCarriesTokens = hasQuestions || IsBroadReadReviewTask(task);
            if (attachment != null)
            {
                string digest = attachment.BuildPlanDigest();
                if (!string.IsNullOrWhiteSpace(plan.Summary))
                    digest += "\n\n" + plan.Summary.Trim();
                if (!hasQuestions && task.Kind == TaskKinds.Write)
                    digest += "\n\n" + UiText(
                        "Press [Review Task] on the task card to generate the code and run the preview.",
                        "작업 카드의 [작업 확인]을 누르면 코드 생성과 미리 검증을 시작합니다.");
                SendAssistantMessage(digest, "text",
                    followUpCarriesTokens ? 0 : plan.PlannerInputTokens,
                    followUpCarriesTokens ? 0 : plan.PlannerOutputTokens);
            }

            if (task.Questions != null && task.Questions.Count > 0)
            {
                task.Stage = TaskStages.NeedsDetails;
                UpsertTask(task, autoOpen: true);
                string questionText = BuildTaskQuestionsMessage(task);
                SendAssistantMessage(questionText, "question",
                    plan.PlannerInputTokens, plan.PlannerOutputTokens, messageType: "question");
                return;
            }

            if (IsBroadReadReviewTask(task))
            {
                task.Stage = TaskStages.Review;
                UpsertTask(task, autoOpen: true);
                string reviewText = BuildReadTaskReviewMessage(task);
                SendAssistantMessage(reviewText, "question",
                    plan.PlannerInputTokens, plan.PlannerOutputTokens, messageType: "question");
                return;
            }

            if (task.Kind == TaskKinds.Read && plan.ShouldAutoRun)
            {
                task.Stage = TaskStages.Working;
                UpsertTask(task, autoOpen: true);
                await GenerateCodeFromTaskAsync(task, runAfterGeneration: true);
                return;
            }

            task.Stage = TaskStages.Review;
            UpsertTask(task, autoOpen: true);
        }

        /// <summary>
        /// Build system prompt using CodeGenSystemPrompt with Revit version + RAG context.
        /// Design doc §2.2 — system prompt includes Revit version and verified API docs.
        /// </summary>
        private string BuildSystemPrompt(bool isCodeGeneration)
        {
            string revitVersion = ConfigService.GetEffectiveRevitVersion();
            return CodeGenSystemPrompt.Build(revitVersion, isCodeGeneration);
        }

        /// <summary>
        /// Build system prompt for plain chat (non-code-gen).
        /// </summary>
        private Task<string> BuildSystemPromptWithRagAsync(
            string queryContext, bool isCodeGeneration, CancellationToken ct)
        {
            string revitVersion = ConfigService.GetEffectiveRevitVersion();
            return Task.FromResult(CodeGenSystemPrompt.Build(revitVersion, isCodeGeneration));
        }

        /// <summary>
        /// Resolve @context tags in user message text.
        /// Replaces @view, @selection, @levels, etc. with live Revit data.
        /// Design doc §1 — @Context Picker integration.
        /// </summary>
        private string ResolveContextTags(string userText)
        {
            if (string.IsNullOrEmpty(userText) || !userText.Contains("@"))
                return userText;

            // Initialize context provider if needed
            EnsureContextProvider();
            if (_contextProvider == null)
                return userText; // No Revit context available (e.g., no active document)

            // Match @tag patterns: @view, @selection, @levels, @worksets, @phases, @family:xxx, @parameters:xxx
            return Regex.Replace(userText, @"@(\w+)(?::([^\s]+))?", match =>
            {
                string fullTag = match.Value;
                try
                {
                    string resolved = _contextProvider.ResolveContextTag(fullTag);
                    if (!string.IsNullOrEmpty(resolved) && resolved != fullTag)
                    {
                        Logger.Log("ContextResolve", $"Resolved {fullTag} → {resolved.Length} chars");
                        return $"\n[Context: {fullTag}]\n{resolved}\n";
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("ContextResolve", $"Failed to resolve {fullTag}: {ex.Message}");
                }
                return fullTag; // Return original if resolution fails
            });
        }

        /// <summary>
        /// Ensure RevitContextProvider is initialized with UIApplication.
        /// Gets UIApplication from BibimApp's execution context.
        /// </summary>
        private void EnsureContextProvider()
        {
            if (_contextProvider != null) return;

            _contextProvider = ServiceContainer.GetService<RevitContextProvider>();
            if (_contextProvider == null)
            {
                // Create a new one — UIApplication will be set when available
                _contextProvider = new RevitContextProvider();
                Logger.Log("BibimDockablePanel", "RevitContextProvider created (UIApplication pending)");
            }
        }

        // Fetch the process-wide RoslynCompilerService. BibimApp.OnStartup registers
        // it eagerly; we fall back to `new` only when the panel is constructed before
        // the container is initialized (test harness / linked-source builds), since
        // RoslynCompilerService's ctor scans AppDomain assemblies (~100-300ms) and we
        // do not want that cost on every chat turn / rerun.
        private static RoslynCompilerService GetCompiler()
        {
            return ServiceContainer.GetService<RoslynCompilerService>()
                ?? new RoslynCompilerService();
        }

        // Whitelist of hosts that the auto-update handler is allowed to fetch from.
        // VersionChecker reads release metadata from api.github.com; the asset URL
        // GitHub returns lives at objects.githubusercontent.com; release HTML pages
        // (the fallback when no matching asset is found) live at github.com.
        // Anything else is rejected — even if a future server-side bug or compromise
        // points us at a different domain, the installer download stays off it.
        private static readonly string[] _trustedReleaseAssetHosts =
        {
            "github.com",
            "objects.githubusercontent.com"
        };

        private static bool IsTrustedReleaseAssetUrl(string url, out string reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                reason = "empty";
                return false;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                reason = "not an absolute URI";
                return false;
            }
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"scheme '{uri.Scheme}' is not https";
                return false;
            }
            string host = uri.Host;
            foreach (var allowed in _trustedReleaseAssetHosts)
            {
                if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            reason = $"host '{host}' not in trusted list";
            return false;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? string.Empty;
            return s.Substring(0, max) + "…";
        }

        /// <summary>
        /// Create a BibimToolService wired to the current Revit context and Roslyn services.
        /// The mainThreadInvoker dispatches RevitContext calls to the WPF/Revit main thread.
        /// </summary>
        private BibimToolService CreateToolService()
        {
            EnsureContextProvider();
            return new BibimToolService(
                _contextProvider,
                GetCompiler(),
                _analyzerService,
                async func =>
                {
                    using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)))
                    {
                        try
                        {
                            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                                func,
                                System.Windows.Threading.DispatcherPriority.Normal,
                                cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            Logger.Log("Dispatcher", "Timeout (>10s): Revit main thread was busy.");
                            return "[Tool Error] Dispatcher timeout: Revit main thread was busy for over 10 seconds.";
                        }
                    }
                },
                () => _codeLibrary?.GetAll());
        }

        /// <summary>True when the Code Library has snippets worth offering to the model.</summary>
        private bool HasCodeLibrarySnippets()
        {
            try { return (_codeLibrary?.GetAll()?.Count ?? 0) > 0; }
            catch { return false; }
        }

        /// <summary>
        /// Extract a lightweight spec payload from a structured assistant response.
        /// This enables the SpecCard flow without requiring a separate backend step.
        /// </summary>
        private object TryExtractSpec(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
                return null;

            var lines = responseText
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            if (lines.Count == 0)
                return null;

            var stepPrefixes = new[] { "-", "*", "•", "1.", "2.", "3.", "4.", "5." };
            var steps = lines
                .Where(line => stepPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
                .Select(line => line.TrimStart('-', '*', '•', ' ').Trim())
                .Select(line =>
                {
                    int dotIndex = line.IndexOf('.');
                    if (dotIndex > 0 && dotIndex < 3 && line.Take(dotIndex).All(char.IsDigit))
                        return line.Substring(dotIndex + 1).Trim();
                    return line;
                })
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Take(8)
                .ToList();

            bool hasSpecCue =
                responseText.IndexOf("spec", StringComparison.OrdinalIgnoreCase) >= 0 ||
                responseText.IndexOf("plan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                responseText.IndexOf("requirements", StringComparison.OrdinalIgnoreCase) >= 0 ||
                responseText.IndexOf("사양", StringComparison.OrdinalIgnoreCase) >= 0 ||
                responseText.IndexOf("계획", StringComparison.OrdinalIgnoreCase) >= 0 ||
                responseText.IndexOf("요구사항", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!hasSpecCue && steps.Count < 2)
                return null;

            string title = lines[0].TrimStart('#', '-', '*', '•', ' ').Trim();
            if (title.Length > 80)
                title = title.Substring(0, 80).Trim();

            string description = lines
                .Skip(1)
                .FirstOrDefault(line => !stepPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
                ?? "Review this proposed plan before generating code.";

            if (description.Length > 240)
                description = description.Substring(0, 240).Trim();

            return new
            {
                title,
                description,
                steps = steps.ToArray(),
                status = "pending"
            };
        }
    }
}
