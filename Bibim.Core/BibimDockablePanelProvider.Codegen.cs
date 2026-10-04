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
    // Code generation: task/spec codegen loops, runtime self-correction judge, preview and verification wording.
    public partial class BibimDockablePanelProvider
    {
        private async Task GenerateCodeFromTaskAsync(TaskState task, bool runAfterGeneration, string additionalContext = null)
        {
            // Same idle-hole bridge as SendDirectChatResponseAsync: cover the gap
            // between the planning banner clearing and the orchestrator's first
            // OnStatusUpdate (RAG/system-prompt build can take seconds).
            _bridge?.PostMessage("progress", new[] {
                new { label = UiText("Preparing code generation...", "코드 생성 준비 중..."), status = "active" }
            });
            var ct = ReplaceCts();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool suppressStreaming = task != null;
            string debugDirectory = null;

            try
            {
                if (suppressStreaming)
                    Interlocked.Increment(ref _suppressStreamingDeltaCount);

                if (task == null)
                    throw new InvalidOperationException("No active task to generate.");

                var llm = EnsureLlmService();
                string taskPrompt = BuildTaskExecutionPrompt(task);
                if (!string.IsNullOrWhiteSpace(additionalContext))
                {
                    string trimmedContext = additionalContext.Length > BibimConstants.AdditionalContextMaxChars
                        ? additionalContext.Substring(0, BibimConstants.AdditionalContextMaxChars) +
                          "\n[...additional context truncated]"
                        : additionalContext;
                    taskPrompt += $"\n\n[Additional context]\n{trimmedContext}";
                }
                string revitVersion = ConfigService.GetEffectiveRevitVersion();

                // Only include the ~700-token FileOutputRules block when the task
                // actually produces files. Most tasks (parameter edits, geometry
                // moves, view tweaks) don't, so this saves a large chunk per call.
                bool isFileOutput =
                    task?.Category == TaskCategories.Export ||
                    CodeGenSystemPrompt.LooksLikeFileOutputTask(task?.Title) ||
                    CodeGenSystemPrompt.LooksLikeFileOutputTask(task?.Summary) ||
                    CodeGenSystemPrompt.LooksLikeFileOutputTask(task?.SourceUserMessage) ||
                    CodeGenSystemPrompt.LooksLikeFileOutputTask(taskPrompt);

                // READ tasks may answer straight from the typed read tools (no code).
                bool isReadTask = task.Kind == TaskKinds.Read;
                bool offerLibrary = HasCodeLibrarySnippets();
                string systemPrompt = CodeGenSystemPrompt.Build(revitVersion, true, isFileOutput,
                        allowDirectReadAnswer: isReadTask)
                    + CodeGenSystemPrompt.AppendToolUseInstructions(includeReadTools: isReadTask,
                        includeCodeLibrary: offerLibrary);
                debugDirectory = CodegenDebugRecorder.CreateRunDirectory(task.TaskId, Guid.NewGuid().ToString("N").Substring(0, 8), "task_codegen");
                CodegenDebugRecorder.WriteJson(debugDirectory, "task_snapshot.json", CreateTaskDebugSnapshot(task));
                CodegenDebugRecorder.WriteText(debugDirectory, "task_prompt.txt", taskPrompt);
                CodegenDebugRecorder.WriteText(debugDirectory, "system_prompt.txt", systemPrompt);

                _analyzerService.SetRevitVersion(revitVersion);

                // Document bodies in the chat history are collapsed here: the task prompt
                // below carries the task's document once (no duplicate 200k-char payloads).
                var generationHistory = new List<ChatMessage>(
                    GetHistoryWindow().Select(m => AttachedDocument.ContainsDocument(m.Text)
                        ? new ChatMessage { Text = AttachedDocument.Redact(m.Text), IsUser = m.IsUser }
                        : m))
                {
                    new ChatMessage { Text = taskPrompt, IsUser = true }
                };

                // Build a context hint so we only ship Revit-context tools
                // (get_view_info, get_selected_elements, etc.) when the task text
                // actually mentions matching keywords.
                string toolHint = string.Join(" ",
                    task?.Title ?? "",
                    task?.Summary ?? "",
                    task?.SourceUserMessage ?? "",
                    taskPrompt ?? "");

                // ── Runtime self-correction (안 A+) ──
                // For WRITE tasks that will preview, wire a validator that runs the
                // dry-run inside the codegen loop. The dry-run ExecutionResult is
                // captured here (closure) so the preview step below can REUSE it
                // instead of running dry-run a second time (critical for large tasks
                // whose dry-run takes minutes). The validator stays out of
                // LlmOrchestrationService — it only returns a DryRunOutcome.
                var scCfg = ConfigService.GetRagConfig();
                bool scEnabled = scCfg?.SelfCorrectionEnabled ?? true;
                int scMaxRetries = scCfg?.SelfCorrectionMaxRetries ?? 1;
                int scScaleGuard = scCfg?.SelfCorrectionScaleGuard ?? 500;
                ExecutionResult capturedDryRun = null;

                Func<CompilationResult, CancellationToken, Task<DryRunOutcome>> runtimeValidator = null;
                if (scEnabled && task.Kind == TaskKinds.Write && runAfterGeneration)
                {
                    runtimeValidator = async (compResult, vct) =>
                    {
                        var exec = await ExecuteCompilationAsync(compResult, isDryRun: true, task);
                        capturedDryRun = exec;   // reused as the preview below
                        return JudgeRuntimeResult(exec, scScaleGuard, task, compResult?.OriginalSource);
                    };
                }

                var agentResult = await llm.GenerateWithToolsAsync(
                    generationHistory, systemPrompt,
                    BibimToolService.GetToolDefinitions(toolHint, includeReadTools: isReadTask,
                        includeCodeLibrary: offerLibrary),
                    CreateToolService().ExecuteAsync,
                    maxTurns: 15, debugDirectory, ct,
                    dryRunValidator: runtimeValidator,
                    maxRuntimeRetries: scEnabled ? scMaxRetries : 0,
                    effort: ConfigService.GetRagConfig()?.EffortCodegen);

                _lastCodeGenResult = agentResult;
                if (!string.IsNullOrEmpty(task?.TaskId))
                    lock (_lastCodeGenResultLock) _codeGenByTask[task.TaskId] = agentResult;

                if (!agentResult.Success)
                {
                    task.Stage = TaskStages.Review;
                    string failMsg = agentResult.IsContextLengthExceeded
                        ? UiText("The conversation is too long. Please start a new session and try again.",
                                 "대화가 너무 길어졌습니다. 새 세션을 시작한 뒤 다시 시도해 주세요.")
                        : agentResult.ErrorMessage ?? UiText("Code generation failed.", "코드 생성에 실패했습니다.");
                    task.ResultSummary = failMsg;
                    UpsertTask(task, autoOpen: true);
                    SendAssistantMessage(failMsg, "text",
                        agentResult.TotalProcessedInputTokens, agentResult.TotalOutputTokens,
                        messageType: "error");
                    return;
                }

                if (!agentResult.IsCodeResponse)
                {
                    task.Stage = TaskStages.Completed;
                    task.ResultSummary = agentResult.RawResponse;
                    UpsertTask(task, autoOpen: false);
                    SendAssistantMessage(agentResult.RawResponse, "text",
                        agentResult.TotalProcessedInputTokens, agentResult.TotalOutputTokens);
                    return;
                }

                string codeToCompile = agentResult.GeneratedCode;
                var compileResult = agentResult.CompilationResult;
                var validationConfig = ConfigService.GetRagConfig();
                bool validationEnabled = validationConfig?.ValidationGateEnabled ?? true;
                bool verifyStageEnabled = validationConfig?.VerifyStageEnabled ?? false;
                var analyzerReport = _analyzerService.Analyze(codeToCompile);

                task.GeneratedCode = codeToCompile;
                task.Stage = TaskStages.Working;
                UpsertTask(task, autoOpen: runAfterGeneration);

                // Save to code library
                try
                {
                    var snippet = new CodeSnippet
                    {
                        Title = task.Title ?? "",
                        Summary = task.Summary ?? "",
                        Code = codeToCompile,
                        RevitVersion = ConfigService.GetEffectiveRevitVersion(),
                        TaskKind = task.Kind ?? TaskKinds.Write,
                        SourceSessionId = _activeSessionId
                    };
                    _pendingLibrarySnippetId = snippet.Id;
                    _codeLibrary?.Save(snippet);
                    _bridge?.PostMessage("code_library_updated", new object());
                }
                catch (Exception libEx)
                {
                    Logger.Log("CodeLibrary", $"Save failed: {libEx.Message}");
                    _pendingLibrarySnippetId = null;
                }

                if (task.Kind == TaskKinds.Read)
                {
                    var execResult = await ExecuteCompiledCodeAsync(isDryRun: true, agentResult);
                    var report = InspectGeneratedCode(codeToCompile, execResult);
                    BindTaskToExecutedDocument(task, execResult);

                    // Record execution result in session log
                    if (_sessionContext != null)
                    {
                        _sessionContext.RecordExecution(new ExecutionLogEntry
                        {
                            TaskId = task.TaskId,
                            TaskTitle = task.Title,
                            Success = execResult.Success,
                            Output = execResult.Success ? execResult.Output : null,
                            ErrorMessage = execResult.ErrorMessage,
                            IsDryRun = true
                        });
                        SaveSessionContext();
                    }

                    task.ExecutionSuccess = execResult.Success;
                    task.Review = BuildTaskReviewSummary(report, execResult);
                    task.Stage = execResult.Success ? TaskStages.Completed : TaskStages.Review;
                    task.ResultSummary = execResult.Success
                        ? execResult.Output
                        : $"{UiText("Execution failed", "실행 실패")}: {execResult.ErrorMessage}";
                    UpsertTask(task, autoOpen: true);
                    SendAssistantMessage(task.ResultSummary, "text",
                        _lastCodeGenResult?.TotalProcessedInputTokens ?? 0,
                        _lastCodeGenResult?.TotalOutputTokens ?? 0,
                        codeToCompile,
                        messageType: execResult.Success ? "normal" : "error");
                    return;
                }

                if (runAfterGeneration)
                {
                    // Reuse the dry-run the self-correction validator already ran on
                    // the FINAL code (avoids a duplicate dry-run, which matters when a
                    // single dry-run takes minutes). Falls back to a fresh dry-run when
                    // self-correction was disabled / not wired for this task.
                    var previewResult = capturedDryRun ?? await ExecuteCompiledCodeAsync(isDryRun: true, agentResult);
                    var report = InspectGeneratedCode(codeToCompile, previewResult);
                    BindTaskToExecutedDocument(task, previewResult);

                    // Record preview execution result in session log
                    if (_sessionContext != null)
                    {
                        _sessionContext.RecordExecution(new ExecutionLogEntry
                        {
                            TaskId = task.TaskId,
                            TaskTitle = task.Title,
                            Success = previewResult.Success,
                            Output = previewResult.Success ? previewResult.Output : null,
                            ErrorMessage = previewResult.ErrorMessage,
                            IsDryRun = true
                        });
                        SaveSessionContext();
                    }

                    task.Review = BuildTaskReviewSummary(report, previewResult);
                    task.Stage = TaskStages.PreviewReady;

                    // Some operations leave a dry-run artifact behind: file exports drop a
                    // "_BIBIM_TEST" file, and view/element duplication isn't always rolled
                    // back by the commit+group-rollback dry-run (Revit views in particular).
                    // Warn the user so the leftover doesn't read as a real result.
                    bool leavesArtifact =
                        task?.Category == TaskCategories.Export ||
                        CodeGenSystemPrompt.LooksLikeFileOutputTask(task?.Title) ||
                        CodeGenSystemPrompt.LooksLikeFileOutputTask(task?.Summary) ||
                        CodeGenSystemPrompt.LooksLikeFileOutputTask(codeToCompile) ||
                        ContainsAny(BuildTaskSearchText(task), "복제", "duplicate", "Duplicate", ".Duplicate(");

                    task.ResultSummary = previewResult.Success
                        ? BuildPreviewCompletedText(previewResult, task)
                          + (leavesArtifact
                            ? UiText("\n※ The preview may leave a temporary artifact (a \"_BIBIM_TEST\" file, or a duplicated view/element) — it is safe to delete. Pressing Apply produces the final result.",
                                "\n※ 미리 검증 과정에서 임시 잔재(\"_BIBIM_TEST\" 파일 또는 복제된 뷰/요소)가 남을 수 있습니다 — 삭제하셔도 무방합니다. [실제 적용]을 누르면 최종 결과가 생성됩니다.")
                            : "")
                        : $"{UiText("Preview failed", "미리 검증 실패")}: {previewResult.ErrorMessage}";
                    UpsertTask(task, autoOpen: true);

                    if (!previewResult.Success)
                    {
                        SendAssistantMessage(task.ResultSummary, "text",
                            agentResult.TotalProcessedInputTokens,
                            agentResult.TotalOutputTokens,
                            codeToCompile,
                            messageType: "error",
                            elapsedMs: sw.ElapsedMilliseconds);
                    }
                    else
                    {
                        SendAssistantMessage(task.ResultSummary, "text",
                            agentResult.TotalProcessedInputTokens,
                            agentResult.TotalOutputTokens,
                            codeToCompile,
                            messageType: "system",
                            elapsedMs: sw.ElapsedMilliseconds);

                        // PreviewReady is the one SUCCESS moment that genuinely needs
                        // the user back at the panel (press Apply) — the global gate
                        // in SendAssistantMessage only notifies for question/error.
                        try { WindowsNotificationService.NotifyActionRequired(); }
                        catch (Exception nex) { Logger.Log("Notification", $"Skipped: {nex.Message}"); }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                string cancelledText = UiText("Code generation cancelled.", "코드 생성이 취소되었습니다.");
                SendAssistantMessage(cancelledText, "text", messageType: "system");
            }
            catch (Exception ex)
            {
                Logger.LogError("GenerateCodeFromTask", ex);
                string errorText = LlmErrorPresenter.ToUserMessage(ex);
                SendAssistantMessage(errorText, "text", messageType: "error");
            }
            finally
            {
                if (suppressStreaming)
                    Interlocked.Decrement(ref _suppressStreamingDeltaCount);
                _bridge.PostMessage("progress", new object[0]);
            }
        }

        /// <summary>
        /// Decide whether a planner failure must HALT the turn instead of degrading to
        /// direct chat, and with what user message. Returns null when falling back to
        /// chat is safe (read intent / smalltalk — chat cannot touch the model).
        /// Context-length failures get "start a new session" guidance instead of
        /// "try again": retrying the same oversized history can never succeed.
        /// </summary>
        private string BuildPlannerFailureHalt(string userText, Exception planEx)
        {
            if (!PlannerGate.ContainsWriteIntent(userText)) return null;

            Logger.Log("TaskPlanner",
                $"Planner failed on write-intent message — halting instead of direct chat: {planEx.Message}");

            if (LlmOrchestrationService.IsContextLengthError(planEx.Message))
                return UiText(
                    "The conversation is too long for planning to continue. Please start a new session and try again.",
                    "대화가 너무 길어져 계획 단계를 진행할 수 없습니다. 새 세션을 시작한 뒤 다시 시도해 주세요.");

            // Surface the (already sanitized) failure reason — a revoked key must
            // read "AI 인증에 실패했습니다 (401)", not a blind "try again" loop.
            string reason = planEx.Message ?? string.Empty;
            int nl = reason.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) reason = reason.Substring(0, nl);
            if (reason.Length > 120) reason = reason.Substring(0, 120) + "…";

            return UiText(
                $"The planning step failed, so I stopped instead of running this model-changing request unplanned.\nReason: {reason}\nResolve the issue (or simply retry) and send the request again.",
                $"요청을 계획하는 단계에서 문제가 발생해, 안전을 위해 실행하지 않고 중단했습니다.\n사유: {reason}\n문제 해결 후(또는 잠시 후) 다시 시도해 주세요.");
        }

        /// <summary>
        /// Honest preview-completed summary: reports the MEASURED affected-element
        /// count instead of a generic "verified" claim. The generic wording used to
        /// fire even for runs whose only effect is invisible to DocumentChanged
        /// (ribbon/UI attempts, exports) — a false-positive "검증 완료" that eroded
        /// trust in real field use.
        /// </summary>
        /// <summary>Output text + a localized [Verification] block (no-op when none).</summary>
        private string AppendVerification(string output, CommitVerification verification)
        {
            if (verification == null || verification.Lines.Count == 0) return output;
            string header = UiText("[Verification]", "[검증]");
            string body = verification.ToText();
            return string.IsNullOrWhiteSpace(output)
                ? $"{header}\n{body}"
                : $"{output}\n\n{header}\n{body}";
        }

        private string BuildPreviewCompletedText(ExecutionResult exec, TaskState task = null)
        {
            string text = BuildPreviewCompletedTextCore(exec);
            // Attached-document tasks: AFFECTED ELEMENTS vs the document's listed ids.
            var scope = AttachmentScope.Compare(task?.Attachment?.TargetElementIds(), exec?.AffectedElementIds,
                exec?.AffectedElementCount ?? -1);
            return scope == null ? text : text + "\n" + AttachmentScope.Describe(scope);
        }

        private const int LargeCreationWarningThreshold = 200;

        private string BuildPreviewCompletedTextCore(ExecutionResult exec)
        {
            int n = exec?.AffectedElementCount ?? 0;
            // Brake on runaway creation: on 2026-09-22 a preview of 1,218 new dimensions went
            // straight to Apply and buried the view. The count was on the card, unremarked.
            int added = exec?.AddedCount ?? 0;
            if (added >= LargeCreationWarningThreshold)
                return UiText(
                    $"⚠ Preview completed — this would CREATE {added} new element(s) ({n} affected in total). " +
                    "Check that this scale is intended before pressing Apply; if not, narrow the selection or the condition and ask again.",
                    $"⚠ 미리 검증 완료 — 새 요소 {added}개가 생성됩니다 (변경 예정 요소 총 {n}개). " +
                    "의도한 규모인지 확인한 뒤 [실제 적용]을 누르세요. 아니라면 선택 범위나 조건을 좁혀 다시 요청하세요.");
            if (n > 0)
                return UiText(
                    $"Preview completed — {n} element(s) would be changed. Review the result, then press Apply.",
                    $"미리 검증 완료 — 변경 예정 요소 {n}개. 결과 확인 후 [실제 적용]을 누르세요.");
            return UiText(
                "Preview completed — no model elements would change (normal for file-output or view/selection tasks). Review the result before proceeding.",
                "미리 검증 완료 — 변경되는 모델 요소 없음(파일 출력·뷰 작업이면 정상). 결과를 확인한 뒤 진행하세요.");
        }

        /// <summary>
        /// Judge a dry-run ExecutionResult for runtime self-correction (안 A+) — Tier 1.
        /// Triggers regeneration on a runtime exception OR a 0-element result (a WRITE
        /// task that changed nothing — filter matched nothing, or every write was
        /// rejected as read-only). Large tasks (affected > scaleGuard) are skipped
        /// because a repeated dry-run would cost minutes. Bounded to maxRetries=1
        /// upstream, so a genuinely-empty result wastes at most one retry.
        /// (Tier 2 multi-step semantic judging is added in Phase 4.)
        /// </summary>
        private DryRunOutcome JudgeRuntimeResult(ExecutionResult exec, int scaleGuard,
            TaskState task = null, string generatedSource = null)
        {
            if (exec == null)
                return new DryRunOutcome { Ran = false, ShouldRegenerate = false };

            // Scale guard — a dry-run on a huge element set can take minutes; don't
            // pay that twice. Let the user judge the preview instead. (Checked first
            // so a large successful task never trips the 0-element branch.)
            if (exec.AffectedElementCount > scaleGuard)
            {
                Logger.Log("SelfCorrection",
                    $"scale guard hit (affected={exec.AffectedElementCount} > {scaleGuard}); skipping self-correction");
                return new DryRunOutcome { Ran = true, ShouldRegenerate = false };
            }

            // Runtime exception — the clearest failure signal.
            if (!exec.Success)
            {
                return new DryRunOutcome
                {
                    Ran = true,
                    ShouldRegenerate = true,
                    FeedbackText = BuildRuntimeFeedback(exec, "a runtime exception occurred")
                };
            }

            // 0 elements affected — a WRITE task that changed nothing. Usually a bad
            // filter (wrong name / category) or every write rejected (read-only /
            // type-level parameter). May genuinely be 0; bounded retry absorbs that.
            if (exec.AffectedElementCount == 0)
            {
                // EXCEPTION: exports, view/selection changes, and other UI-side work
                // legitimately produce zero DocumentChanged deltas — their success
                // signal is a file on disk or a UI state, not a model change. Without
                // this check the judge regenerates CORRECT code (a full dry-run + a
                // full LLM round wasted) purely because the proxy metric reads 0.
                if (IsZeroDeltaByDesign(task, generatedSource))
                {
                    Logger.Log("SelfCorrection",
                        "0 elements affected but the task is zero-delta-by-design (export/view/selection) — passing");
                    return new DryRunOutcome { Ran = true, ShouldRegenerate = false };
                }

                return new DryRunOutcome
                {
                    Ran = true,
                    ShouldRegenerate = true,
                    FeedbackText = BuildRuntimeFeedback(exec,
                        "0 elements were affected — the filter likely matched nothing, " +
                        "or every write was rejected (e.g. a read-only / type-level parameter)")
                };
            }

            return new DryRunOutcome { Ran = true, ShouldRegenerate = false };
        }

        // View/selection APIs whose legitimate effect leaves zero DocumentChanged
        // deltas. Deliberately does NOT include file-write APIs (StreamWriter,
        // File.WriteAll, .Export) — generated code often mixes an incidental report
        // file or view switch into a model-edit task, and matching those would pass
        // genuinely-failed writes. Kept in sync conceptually with
        // CodeGenSystemPrompt.LooksLikeFileOutputTask (task-text side of the check).
        private static readonly string[] _viewSelectionMarkers =
        {
            "ActiveView =", "RequestViewChange", "Selection.SetElementIds", "ShowElements("
        };

        /// <summary>
        /// True when a WRITE-classified task is expected to leave zero DocumentChanged
        /// deltas even on success: pure file exports (the planner classifies all exports
        /// as write) and pure view/selection operations. Two guards keep this narrow:
        /// (1) task text must NOT also carry write intent — "파라미터 변경하고 엑셀로
        /// 저장" keeps its 0-element retry because the model edit is the primary job;
        /// (2) file-write APIs in the source are ignored (incidental report files must
        /// not mask a failed write). Conservative: unknown → false (normal retry).
        /// Known tradeoff: a PURE export whose filter matched nothing passes without a
        /// retry — indistinguishable from a correct export by delta count alone; the
        /// preview wording surfaces the 0-change fact to the user instead.
        /// </summary>
        private bool IsZeroDeltaByDesign(TaskState task, string generatedSource)
        {
            // v1.2.0: the planner emits a structured category — trust it over the
            // keyword heuristics below, which stay only for tasks without one.
            if (TaskCategories.IsKnown(task?.Category))
                return TaskCategories.IsZeroDeltaByDesign(task.Category);

            string taskText = BuildTaskSearchText(task);
            if (string.IsNullOrWhiteSpace(taskText)) return false;

            // Mixed edit+output tasks keep the 0-element retry: 0 deltas there means
            // the PRIMARY (write) part failed, which is exactly what Tier-1 catches.
            // The PLANNER'S OWN question wording is stripped first — field log
            // 2026-07-13: "폴더 경로를 입력해 주세요" made an export task read as
            // write-intent ("입력") and re-triggered the 0-element regeneration this
            // exception exists to prevent. Answers (A:) are kept; prompts (Q:) go.
            string intentText = System.Text.RegularExpressions.Regex.Replace(
                taskText, @"Q:\s*.*?(?=A:|$)", " ",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (PlannerGate.ContainsWriteIntent(intentText)) return false;

            if (CodeGenSystemPrompt.LooksLikeFileOutputTask(taskText))
                return true;

            if (string.IsNullOrEmpty(generatedSource)) return false;
            foreach (var m in _viewSelectionMarkers)
            {
                if (generatedSource.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Build the runtime-validation feedback handed back to the model when a
        /// dry-run reveals a problem. Deliberately minimal — the actual error / log
        /// IS the teacher (that's the whole point of self-correction); we don't pile
        /// on case-specific Revit lore here. <paramref name="reason"/> names the
        /// detected problem so the model knows what to fix.
        /// </summary>
        private string BuildRuntimeFeedback(ExecutionResult exec, string reason)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[RUNTIME VALIDATION] Your code compiled and ran as a dry-run preview (changes rolled back).");
            sb.AppendLine("Result:");
            sb.AppendLine($"- Status: {(exec.Success ? "Success" : "FAILED")}");
            if (!string.IsNullOrWhiteSpace(exec.ErrorMessage))
                sb.AppendLine($"- Runtime error: {Truncate(exec.ErrorMessage, 600)}");
            sb.AppendLine($"- Affected elements: {exec.AffectedElementCount}");
            if (exec.HasExecutionLogs)
            {
                sb.AppendLine("- Execution log:");
                sb.AppendLine(Truncate(string.Join("\n", exec.ExecutionLogs), 2000));
            }
            sb.AppendLine();
            sb.AppendLine($"This looks wrong: {reason}.");
            sb.AppendLine("Diagnose the ROOT CAUSE from the error/log above and regenerate the code. " +
                "Use tools (get_element_parameters, get_project_levels, search_revit_api) to verify " +
                "assumptions instead of guessing. If you are CERTAIN the result is actually correct " +
                "(e.g. there genuinely are 0 matching elements), return the same code unchanged.");
            return sb.ToString();
        }

        /// <summary>
        /// Generate code from the confirmed spec using the agent loop.
        /// Stores the result for later execute (dryrun/commit).
        ///
        /// Integrates:
        ///   - CodeGenSystemPrompt.Build(revitVersion, isCodeGeneration: true)
        ///   - GenerateWithToolsAsync (Claude Tool Use loop with auto-compile)
        ///   - RoslynAnalyzerService (BIBIM001-005) for post-compile analysis
        /// </summary>
        private async Task GenerateCodeFromSpecAsync()
        {
            // Same idle-hole bridge as GenerateCodeFromTaskAsync — RAG/system-prompt
            // build runs for seconds before the orchestrator posts its first status.
            _bridge?.PostMessage("progress", new[] {
                new { label = UiText("Preparing code generation...", "코드 생성 준비 중..."), status = "active" }
            });
            var ct = ReplaceCts();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _suppressStreamingDeltaCount);

            try
            {
                var llm = EnsureLlmService();

                string revitVersion2 = ConfigService.GetEffectiveRevitVersion();

                // Inspect the last few user messages for file-output keywords
                // (no task object here — spec flow drives codegen straight from chat history).
                bool specIsFileOutput = false;
                lock (_chatHistoryLock)
                {
                    int scan = Math.Min(_chatHistory.Count, 6);
                    for (int i = _chatHistory.Count - scan; i < _chatHistory.Count; i++)
                    {
                        if (i < 0) continue;
                        if (_chatHistory[i].IsUser &&
                            CodeGenSystemPrompt.LooksLikeFileOutputTask(
                                AttachedDocument.InstructionOf(_chatHistory[i].Text)))
                        {
                            specIsFileOutput = true;
                            break;
                        }
                    }
                }

                string systemPrompt = CodeGenSystemPrompt.Build(revitVersion2, true, specIsFileOutput)
                    + CodeGenSystemPrompt.AppendToolUseInstructions();

                _analyzerService.SetRevitVersion(revitVersion2);

                // Use windowed history to cap token cost across the multi-turn tool loop
                var specHistory = new List<ChatMessage>(GetHistoryWindow());

                string specDebugDir = CodegenDebugRecorder.CreateRunDirectory(
                    "spec", Guid.NewGuid().ToString("N").Substring(0, 8), "spec_codegen");
                CodegenDebugRecorder.WriteText(specDebugDir, "system_prompt.txt", systemPrompt);

                // Use the most recent user message(s) as the tool-hint so we only
                // ship Revit-context tools that the spec actually needs.
                string specToolHint = string.Join(" ",
                    specHistory.Where(m => m.IsUser).Select(m => m.Text ?? "").Reverse().Take(3));

                var agentResult = await llm.GenerateWithToolsAsync(
                    specHistory, systemPrompt,
                    BibimToolService.GetToolDefinitions(specToolHint),
                    CreateToolService().ExecuteAsync,
                    maxTurns: 15, specDebugDir, ct,
                    effort: ConfigService.GetRagConfig()?.EffortCodegen);

                _lastCodeGenResult = agentResult;

                if (!agentResult.Success)
                {
                    string specFailMsg = agentResult.IsContextLengthExceeded
                        ? UiText("The conversation is too long. Please start a new session and try again.",
                                 "대화가 너무 길어졌습니다. 새 세션을 시작한 뒤 다시 시도해 주세요.")
                        : agentResult.ErrorMessage ?? UiText("Code generation failed.", "코드 생성에 실패했습니다.");
                    _bridge.PostMessage("streaming_end", new
                    {
                        text = specFailMsg,
                        type = "error",
                        inputTokens = agentResult.TotalProcessedInputTokens,
                        outputTokens = agentResult.TotalOutputTokens,
                        elapsedMs = sw.ElapsedMilliseconds
                    });
                    return;
                }

                if (!agentResult.IsCodeResponse)
                {
                    lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = agentResult.RawResponse, IsUser = false });
                    _bridge.PostMessage("streaming_end", new
                    {
                        text = agentResult.RawResponse,
                        type = "normal",
                        inputTokens = agentResult.TotalProcessedInputTokens,
                        outputTokens = agentResult.TotalOutputTokens,
                        elapsedMs = sw.ElapsedMilliseconds
                    });
                    return;
                }

                // Add assistant's code response to history so subsequent turns have context
                lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = agentResult.RawResponse, IsUser = false });

                string codeToCompile = agentResult.GeneratedCode;
                var compileResult = agentResult.CompilationResult;
                var validationConfig = ConfigService.GetRagConfig();
                bool validationEnabled = validationConfig?.ValidationGateEnabled ?? true;
                bool verifyStageEnabled = validationConfig?.VerifyStageEnabled ?? false;
                var analyzerReport = _analyzerService.Analyze(codeToCompile);

                PostStreamingEndMessage(
                    UiText("Code generation completed. Review the results before applying changes.",
                        "코드 생성이 완료되었습니다. 검토 결과를 확인한 뒤 실제 적용 여부를 결정하세요."),
                    "system",
                    inputTokens: agentResult.TotalProcessedInputTokens,
                    outputTokens: agentResult.TotalOutputTokens,
                    elapsedMs: sw.ElapsedMilliseconds);

                // Run API inspection with analyzer integration
                var inspector = new ApiInspectorService(
                    validationEnabled || verifyStageEnabled ? _analyzerService : null,
                    ConfigService.GetEffectiveRevitVersion());
                var report = inspector.Inspect(codeToCompile);
                _bridge.PostMessage("api_report", new
                {
                    apiUsages = report.ApiUsages.Select(u => new
                    {
                        apiName = u.ApiName,
                        fullExpression = u.FullExpression,
                        status = u.Status.ToString().ToLower(),
                        note = u.Note,
                        line = u.Line
                    }),
                    safeCount = report.SafeCount,
                    versionSpecificCount = report.VersionSpecificCount,
                    deprecatedCount = report.DeprecatedCount,
                    analyzerDiagnostics = analyzerReport.Diagnostics.Select(d => new
                    {
                        id = d.Id,
                        message = d.Message,
                        severity = d.Severity.ToString().ToLower(),
                        line = d.Line
                    })
                });
            }
            catch (OperationCanceledException)
            {
                _bridge.PostMessage("streaming_end", new
                {
                    text = UiText("Code generation cancelled.", "코드 생성이 취소되었습니다."),
                    type = "system",
                    inputTokens = 0,
                    outputTokens = 0,
                    elapsedMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("GenerateCodeFromSpec", ex);
                _bridge.PostMessage("streaming_end", new
                {
                    text = LlmErrorPresenter.ToUserMessage(ex),
                    type = "error",
                    inputTokens = 0,
                    outputTokens = 0,
                    elapsedMs = sw.ElapsedMilliseconds
                });
            }
            finally
            {
                Interlocked.Decrement(ref _suppressStreamingDeltaCount);
            }
        }
    }
}
