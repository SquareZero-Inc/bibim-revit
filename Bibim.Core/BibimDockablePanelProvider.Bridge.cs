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
    // WebView2 bridge: handler registration and all JS -> C# message handlers, status pushes to the UI.
    public partial class BibimDockablePanelProvider
    {
        // Registers an async bridge handler with standard Task.Run + try/catch boilerplate.
        // swallowCancellation: if true, OperationCanceledException is logged at Info level (not Error).
        private void RegisterAsyncHandler(string type,
            Func<Newtonsoft.Json.Linq.JObject, Task> handler,
            bool swallowCancellation = false)
        {
            _bridge.On(type, (payloadJson) =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var payload = Newtonsoft.Json.Linq.JObject.Parse(payloadJson ?? "{}");
                        await handler(payload);
                    }
                    catch (OperationCanceledException) when (swallowCancellation)
                    {
                        Logger.Log("BridgeHandler", $"{type} cancelled");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"BridgeHandler.{type}", ex);
                    }
                });
            });
        }

        // Registers a synchronous bridge handler with standard try/catch boilerplate.
        private void RegisterSyncHandler(string type,
            Action<Newtonsoft.Json.Linq.JObject> handler)
        {
            _bridge.On(type, (payloadJson) =>
            {
                try
                {
                    var payload = Newtonsoft.Json.Linq.JObject.Parse(payloadJson ?? "{}");
                    handler(payload);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"BridgeHandler.{type}", ex);
                }
            });
        }

        private void RegisterBridgeHandlers()
        {
            _bridge.On("user_message", (payloadJson) =>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var payload = Newtonsoft.Json.Linq.JObject.Parse(payloadJson);
                        string userText = payload["text"]?.ToString() ?? "";

                        // Attached document (.md/.txt/.csv): body stays in memory and goes to
                        // the LLM only — the log line records name/size, never the content.
                        var attachment = AttachedDocument.FromPayload(
                            payload["attachment"] as Newtonsoft.Json.Linq.JObject);
                        if (attachment != null && string.IsNullOrWhiteSpace(userText))
                            userText = UiText("Review the attached document and do what it requires.",
                                              "첨부 문서를 검토하고 필요한 작업을 수행해 줘");
                        Logger.Log("BridgeHandler", attachment == null
                            ? $"user_message processing: {payloadJson}"
                            : $"user_message processing: text=\"{Truncate(userText, 200)}\" attachment={attachment.Describe()}");

                        if (string.IsNullOrWhiteSpace(userText))
                        {
                            PostStreamingEndMessage(
                                UiText("Empty message.", "빈 메시지입니다."));
                            return;
                        }

                        // Pre-flight credential check. Local provider uses LocalServerUrl
                        // as the gate (API key is optional — Ollama / LM Studio default
                        // is unauthenticated). All other providers require an API key.
                        var preCheckCreds = ConfigService.GetActiveCredentials();
                        bool credsMissing;
                        string credsMissingMessage;
                        if (preCheckCreds.Provider == "local")
                        {
                            credsMissing = string.IsNullOrWhiteSpace(
                                ConfigService.GetRagConfig()?.LocalServerUrl);
                            credsMissingMessage = UiText(
                                "Local LLM server URL is not configured. Open **Settings** (gear icon) and enter your server URL under Local LLM.",
                                "Local LLM 서버 URL이 설정되어 있지 않습니다. **설정** (톱니바퀴 아이콘) → Local LLM 섹션에서 서버 URL을 입력해 주세요.");
                        }
                        else
                        {
                            credsMissing = string.IsNullOrEmpty(preCheckCreds.ApiKey);
                            credsMissingMessage = UiText(
                                "API key is not configured for the selected model. Please go to **Settings** (gear icon) and enter the matching API key.",
                                "선택한 모델용 API 키가 설정되어 있지 않습니다. **설정** (톱니바퀴 아이콘)에서 해당 키를 입력해 주세요.");
                        }
                        if (credsMissing)
                        {
                            PostStreamingEndMessage(credsMissingMessage);
                            return;
                        }

                        await ChatWithLlmServiceAsync(userText, attachment);
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Log("BridgeHandler", "Streaming cancelled by user");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("BridgeHandler.user_message", ex);
                        try
                        {
                            _bridge.PostMessage("streaming_end", new
                            {
                                text = LlmErrorPresenter.ToUserMessage(ex),
                                type = "error",
                                inputTokens = 0,
                                outputTokens = 0,
                                elapsedMs = 0
                            });
                        }
                        catch (Exception postEx)
                        {
                            Logger.LogError("BridgeHandler.PostError", postEx);
                        }
                    }
                });
            });

            _bridge.On("cancel", (_) =>
            {
                _streamingCts?.Cancel();
                Logger.Log("BridgeHandler", "Cancel requested");
            });

            // --- spec_action: confirm / revise / reject spec ---
            RegisterAsyncHandler("spec_action", async payload =>
            {
                string action = payload["action"]?.ToString() ?? "";
                string feedback = payload["feedback"]?.ToString() ?? "";
                Logger.Log("BridgeHandler", $"spec_action: {action}");

                if (action == "confirm")
                {
                    await GenerateCodeFromSpecAsync();
                }
                else if (action == "revise")
                {
                    string revisePrompt = string.IsNullOrEmpty(feedback)
                        ? "Please revise the spec."
                        : $"Please revise the spec based on this feedback: {feedback}";
                    await ChatWithLlmServiceAsync(revisePrompt);
                }
                else if (action == "reject")
                {
                    _bridge.PostMessage("system_message",
                        UiText("Spec rejected.", "스펙이 취소되었습니다."));
                }
            });

            // --- question_answers: structured Q&A from Question Card UI ---
            RegisterAsyncHandler("question_answers", async payload =>
            {
                var answers = payload["answers"] as Newtonsoft.Json.Linq.JArray;
                Logger.Log("BridgeHandler", $"question_answers: {answers?.Count ?? 0} answers");

                var task = GetActiveTask();
                if (task == null || answers == null) return;

                foreach (var answerObj in answers)
                {
                    string qId = answerObj["id"]?.ToString();
                    string answer = answerObj["answer"]?.ToString();
                    bool skipped = answerObj["skipped"]?.ToObject<bool>() ?? false;
                    var question = task.Questions?.FirstOrDefault(q => q.Id == qId);
                    if (question != null) { question.Answer = answer; question.Skipped = skipped; }
                }

                var qaSummary = new System.Text.StringBuilder();
                foreach (var q in task.Questions ?? new List<QuestionItem>())
                {
                    string answerText = q.Skipped
                        ? UiText("(skipped)", "(건너뜀)")
                        : (q.Answer ?? UiText("(no answer)", "(답변 없음)"));
                    qaSummary.AppendLine($"Q: {q.Text}");
                    qaSummary.AppendLine($"A: {answerText}");
                }

                string qaText = qaSummary.ToString().Trim();
                AppendTaskUserInput(task, qaText);

                bool allSkipped = task.Questions != null
                    && task.Questions.Count > 0
                    && task.Questions.All(q => q.Skipped);
                if (allSkipped)
                {
                    _bridge.PostMessage("system_message",
                        UiText(
                            "All questions were skipped. BIBIM will proceed with default assumptions, but the result may not match your intent.",
                            "모든 질문을 건너뛰었습니다. BIBIM이 기본값으로 진행하지만, 원하는 결과와 다를 수 있습니다."));
                }

                // Store Q&A in internal history for planner context (sliding window),
                // but do NOT echo it back to the chat panel as a user bubble.
                // Displaying "Q: ... A: ..." as if the user typed it is confusing —
                // the user answered via card UI, not by typing. The task panel's
                // summary already shows the collected answers.
                lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = qaText, IsUser = true });
                _sessionManager?.AddMessage(_activeSessionId, "user", "text", qaText);

                task.Questions = new List<QuestionItem>();
                SendSessionList();

                // F-2: After Q&A the task has all details — skip the manual "작업 확인"
                // gate for WRITE tasks (preview is non-destructive; the real safety gate
                // is "실제 적용"). Broad-read review tasks still pause for scope confirmation.
                if (task.Kind == TaskKinds.Write && !IsBuiltInCurrentContextSummaryTaskV2(task))
                {
                    task.Stage = TaskStages.Working;
                    UpsertTask(task, autoOpen: true);
                    await GenerateCodeFromTaskAsync(task, runAfterGeneration: true);
                }
                else
                {
                    task.Stage = TaskStages.Review;
                    UpsertTask(task, autoOpen: true);
                }

            });

            RegisterAsyncHandler("task_action", async payload =>
            {
                string action = payload["action"]?.ToString() ?? "";
                Logger.Log("BridgeHandler", $"task_action: {action}");

                var task = GetActiveTask();
                if (task == null) return;

                if (action == "confirm")
                {

                    ApplyParameterCreationGuard(task);

                    if (task.Questions != null && task.Questions.Count > 0)
                    {
                        task.Stage = TaskStages.NeedsDetails;
                        UpsertTask(task, autoOpen: true);
                        string questionText = BuildTaskQuestionsMessage(task);
                        _bridge.PostMessage("system_message",
                            UiText("I still need a few details before I can continue this task.",
                                "이 작업을 계속하려면 아직 몇 가지 정보가 더 필요합니다."));
                        SendAssistantMessage(questionText, "question", messageType: "question");
                        return;
                    }

                    task.Stage = TaskStages.Working;
                    UpsertTask(task, autoOpen: true);
                    if (IsBuiltInCurrentContextSummaryTaskV2(task))
                    {
                        ApplyCurrentContextSummaryDefaults(task);
                        await RunBuiltInCurrentContextSummaryTaskAsync(task);
                    }
                    else
                    {
                        await GenerateCodeFromTaskAsync(task, runAfterGeneration: true);
                    }
                }
                else if (action == "cancel")
                {
                    task.Stage = TaskStages.Cancelled;
                    task.ResultSummary = UiText("Task cancelled.", "작업이 취소되었습니다.");
                    UpsertTask(task, autoOpen: false);
                    _sessionContext.ActiveTaskId = null;
                    SaveSessionContext();
                    SendTaskState();
                    SendTaskList();
                }
            });

            // --- execute: dryrun / commit ---
            RegisterAsyncHandler("execute", async payload =>
            {
                string mode = payload["mode"]?.ToString() ?? "dryrun";
                bool isDryRun = mode == "dryrun";
                Logger.Log("BridgeHandler", $"execute: mode={mode}");


                // Resolve the task the USER pressed Apply on (frontend sends the
                // card's taskId) and that task's own code. The active-task pointer
                // follows the latest generation, so resolving by it let an
                // interleaved task swap the assembly under Apply. Taskless callers
                // (spec flow) send no taskId and use the shared slot directly —
                // never a stale per-task entry from a finished card.
                string requestedTaskId = payload["taskId"]?.ToString();
                TaskState task;
                CodeGenerationResult codeGen;
                if (!string.IsNullOrEmpty(requestedTaskId))
                {
                    task = _sessionContext?.Tasks?.FirstOrDefault(
                               t => t.TaskId == requestedTaskId) ?? GetActiveTask();
                    codeGen = ResolveCodeGenForTask(task);
                }
                else
                {
                    task = GetActiveTask();
                    codeGen = _lastCodeGenResult;
                }
                // Run again after a session reload / restart: the compiled assembly lives in
                // memory only, the code is saved on the task, so recompile it.
                if (codeGen?.CompilationResult?.Assembly == null && task != null &&
                    !string.IsNullOrEmpty(requestedTaskId) && !string.IsNullOrWhiteSpace(task.GeneratedCode))
                {
                    var recompiled = GetCompiler().Compile(task.GeneratedCode);
                    if (recompiled.Success)
                    {
                        codeGen = new CodeGenerationResult
                        {
                            Success = true, IsCodeResponse = true, GeneratedCode = task.GeneratedCode,
                            CompilationResult = recompiled, CompileAttempts = 1
                        };
                        lock (_lastCodeGenResultLock) _codeGenByTask[task.TaskId] = codeGen;
                    }
                    else
                    {
                        Logger.Log("BridgeHandler", $"rerun recompile failed: {recompiled.ErrorSummary}");
                    }
                }
                if (codeGen?.CompilationResult?.Assembly == null)
                {
                    _bridge.PostMessage("system_message",
                        UiText("No compiled code available. Generate code first.", "실행할 컴파일 결과가 없습니다. 먼저 코드를 생성하세요."));
                    return;
                }

                // NOTE: these labels are matched by frontend LoadingModal.
                // REVIT_EXECUTING_LABELS (Stop must not abort while Revit executes).
                _bridge.PostMessage("progress", new[] {
                    new { label = isDryRun
                        ? UiText("Running preview...", "미리보기를 실행하는 중...")
                        : UiText("Applying changes...", "변경 사항 적용 중..."), status = "active" }
                });

                var execResult = await ExecuteCompiledCodeAsync(isDryRun, codeGen);
                BindTaskToExecutedDocument(task, execResult);

                if (_sessionContext != null)
                {
                    _sessionContext.RecordExecution(new ExecutionLogEntry
                    {
                        TaskId = task?.TaskId,
                        TaskTitle = task?.Title,
                        Success = execResult.Success,
                        Output = execResult.Success ? execResult.Output : null,
                        ErrorMessage = execResult.ErrorMessage,
                        IsDryRun = isDryRun
                    });
                    SaveSessionContext();
                }

                var report = !string.IsNullOrWhiteSpace(codeGen.GeneratedCode)
                    ? InspectGeneratedCode(codeGen.GeneratedCode, execResult)
                    : null;

                // Post-commit verification (M7): report MEASURED facts — element delta vs.
                // the preview's prediction, reported files on disk — instead of trusting
                // the generated code's own "done" string. Preview count is read BEFORE the
                // task's review is overwritten with the commit result below.
                int previewAffected = task?.Review != null && task.Review.PreviewSuccess
                    ? task.Review.AffectedElementCount
                    : -1;
                CommitVerification verification = null;
                if (!isDryRun && execResult.Success)
                {
                    verification = CommitVerifier.Verify(
                        task?.Category, previewAffected,
                        execResult.AffectedElementCount, execResult.AddedCount,
                        execResult.ModifiedCount, execResult.DeletedCount,
                        execResult.Output, execResult.ExecutionLogs, null);

                    // Attached-document tasks: prove the change stayed inside the listed ids.
                    var scope = AttachmentScope.Compare(
                        task?.Attachment?.TargetElementIds(), execResult.AffectedElementIds,
                        execResult.AffectedElementCount);
                    if (scope != null)
                    {
                        verification.Lines.Add(AttachmentScope.Describe(scope));
                        if (scope.ChangedOutside > 0) verification.HasWarning = true;
                    }
                }

                if (!isDryRun)
                {
                    AuditLogService.Append(new AuditEntry
                    {
                        Event = "commit",
                        AppVersion = BibimApp.AppVersion,
                        TaskId = task?.TaskId,
                        Title = task?.Title,
                        Category = task?.Category,
                        Kind = task?.Kind,
                        Document = execResult.DocumentTitle,
                        DocumentPath = execResult.DocumentPath,
                        Model = _llmService?.ModelId ?? ConfigService.GetActiveCredentials().ModelId,
                        Success = execResult.Success,
                        Error = execResult.Success ? null : execResult.ErrorMessage,
                        Risk = AuditLogService.ClassifyRisk(task?.Category, execResult.AffectedElementCount, execResult.DeletedCount),
                        Affected = execResult.AffectedElementCount,
                        Added = execResult.AddedCount,
                        Modified = execResult.ModifiedCount,
                        Deleted = execResult.DeletedCount,
                        PreviewAffected = previewAffected,
                        ElementIds = execResult.AffectedElementIds,
                        CodeSha256 = AuditLogService.Sha256(codeGen.GeneratedCode),
                        Output = execResult.Output,
                        Verification = verification?.ToText(),
                        RevitWarnings = execResult.RevitWarnings
                    });
                }

                if (task != null && isDryRun && task.Kind == TaskKinds.Read)
                {
                    // READ task run again: the dry run IS the result, so show it and finish.
                    task.Stage = TaskStages.Completed;
                    task.Review = BuildTaskReviewSummary(report, execResult);
                    string readOutput = execResult.Success
                        ? (execResult.Output ?? "")
                        : $"{UiText("Execution failed", "실행 실패")}: {execResult.ErrorMessage}";
                    if (execResult.Success && execResult.HasExecutionLogs)
                        readOutput += "\n\n[Log]\n" + string.Join("\n", execResult.ExecutionLogs.Select(l => $"• {l}"));
                    task.ExecutionSuccess = execResult.Success;
                    task.ResultSummary = readOutput;
                    UpsertTask(task, autoOpen: true);
                    if (execResult.Success)
                        SendAssistantMessage(readOutput, "text");
                }
                else if (task != null && isDryRun)
                {
                    task.Stage = TaskStages.PreviewReady;
                    task.Review = BuildTaskReviewSummary(report, execResult);
                    task.ResultSummary = execResult.Success
                        ? BuildPreviewCompletedText(execResult, task)
                        : UiText("Preview failed. Review the result and adjust the task.",
                            "미리 검증이 실패했습니다. 결과를 확인하고 작업을 보완하세요.");
                    UpsertTask(task, autoOpen: true);
                }
                else if (task != null && !isDryRun)
                {
                    task.ExecutionSuccess = execResult.Success;
                    task.Stage = execResult.Success ? TaskStages.Completed : TaskStages.PreviewReady;
                    task.WasApplied = execResult.Success;
                    task.ResultSummary = execResult.Success
                        ? AppendVerification(execResult.Output, verification)
                        : $"{UiText("Execution failed", "실행 실패")}: {execResult.ErrorMessage}";
                    task.Review = BuildTaskReviewSummary(report, execResult);
                    UpsertTask(task, autoOpen: !execResult.Success || (verification?.HasWarning ?? false));
                }

                if (!isDryRun || !execResult.Success)
                {
                    var action = (!isDryRun && execResult.Success)
                        ? TrackAppliedAction(task)
                        : null;

                    // Append ctx.Log() entries to output when present
                    string baseOutput = execResult.Success
                        ? AppendVerification(execResult.Output, verification)
                        : $"{UiText("Execution failed", "실행 실패")}: {execResult.ErrorMessage}";
                    string messageText = baseOutput;
                    if (execResult.HasExecutionLogs)
                    {
                        string logBlock = string.Join("\n", execResult.ExecutionLogs.Select(l => $"• {l}"));
                        messageText = $"{baseOutput}\n\n[Log]\n{logBlock}";
                    }

                    PostStreamingEndMessage(
                        messageText,
                        execResult.Success ? "normal" : "error",
                        inputTokens: 0, outputTokens: 0, elapsedMs: 0,
                        actionId: action?.ActionId,
                        taskId: action?.TaskId,
                        canUndo: action?.CanUndo ?? false,
                        feedbackEnabled: false,
                        feedbackState: action?.FeedbackState);

                    // F-4: Suppress the active feedback bubble on success — the 👍/👎
                    // icons are already attached to the message via feedbackEnabled on
                    // the message itself. Active prompting on every success interrupts
                    // flow; reserve it for failures where the user needs a recovery path.
                    if (action != null && !execResult.Success)
                        _bridge.PostMessage("feedback_request", new { actionId = action.ActionId, taskId = action.TaskId });

                    // If Revit warnings fired during commit, prompt user to re-generate
                    if (!isDryRun && execResult.Success && execResult.HasRevitWarnings)
                    {
                        string warningList = string.Join("\n", execResult.RevitWarnings.Select(w => $"- {w}"));
                        string warningPrompt = AppLanguage.IsEnglish
                            ? $"Execution succeeded, but the following Revit warnings occurred:\n{warningList}\n\nWould you like to regenerate the code to address these?"
                            : $"실행 자체는 문제 없지만, 다음 Revit 경고가 발생했습니다:\n{warningList}\n\n이 부분까지 대응해서 새로 진행할까요?";
                        _lastRevitWarnings = execResult.RevitWarnings;
                        _bridge.PostMessage("revit_warning", new
                        {
                            message = warningPrompt,
                            warnings = execResult.RevitWarnings,
                            taskId = task?.TaskId
                        });
                    }

                    _sessionManager?.AddMessage(_activeSessionId, "assistant",
                        execResult.Success ? "text" : "error", messageText);
                    SendSessionList();
                }

                _bridge.PostMessage("progress", new object[0]);
            });

            // warning_response: user replied to revit_warning prompt (yes / no / add)
            RegisterAsyncHandler("warning_response", async payload =>
            {
                string choice = payload["choice"]?.ToString(); // "yes" | "no" | "add"
                string extraText = payload["text"]?.ToString() ?? "";
                string taskId = payload["taskId"]?.ToString();

                if (string.Equals(choice, "no", StringComparison.OrdinalIgnoreCase))
                    return;


                // Retrieve task for context
                var task = (!string.IsNullOrWhiteSpace(taskId)
                               ? GetTasks().FirstOrDefault(t => t.TaskId == taskId)
                               : null)
                           ?? GetTasks().LastOrDefault();
                if (task == null)
                {
                    _bridge.PostMessage("system_message",
                        UiText("No active task to regenerate.", "재생성할 활성 태스크가 없습니다."));
                    return;
                }

                string warningContext = AppLanguage.IsEnglish
                    ? "The previous code executed but triggered Revit warnings. Please regenerate to address them."
                    : "이전 코드 실행 시 Revit 경고가 발생했습니다. 이를 해결하도록 코드를 다시 생성해주세요.";
                if (_lastRevitWarnings?.Count > 0)
                    warningContext += "\n\nRevit warnings:\n" + string.Join("\n", _lastRevitWarnings.Select(w => $"- {w}"));
                if (!string.IsNullOrWhiteSpace(extraText))
                    warningContext += AppLanguage.IsEnglish
                        ? $"\n\nAdditional requirement: {extraText}"
                        : $"\n\n추가 요청: {extraText}";

                _bridge.PostMessage("progress", new[]
                {
                    new { label = UiText("Regenerating code...", "코드 재생성 중..."), status = "active" }
                });

                await GenerateCodeFromTaskAsync(task, runAfterGeneration: true, additionalContext: warningContext);
            });

            RegisterAsyncHandler("undo_last_apply", async payload =>
            {
                string actionId = payload["actionId"]?.ToString();

                if (_lastAppliedAction == null || !_lastAppliedAction.CanUndo)
                {
                    _bridge.PostMessage("system_message",
                        UiText("There is no recent apply to undo.", "되돌릴 최근 적용 결과가 없습니다."));
                    return;
                }


                if (!string.IsNullOrWhiteSpace(actionId) &&
                    !string.Equals(actionId, _lastAppliedAction.ActionId, StringComparison.Ordinal))
                {
                    _bridge.PostMessage("system_message",
                        UiText("Only the latest applied change can be undone.",
                            "가장 최근에 실제 적용한 변경만 되돌릴 수 있습니다."));
                    return;
                }

                _bridge.PostMessage("progress", new[] {
                    new { label = UiText("Reverting the last applied change...", "마지막 적용 내용을 되돌리는 중..."), status = "active" }
                });

                long undoDocumentSequence = DocumentChangeTracker.GetCurrentSequence(
                    _lastAppliedAction.TargetDocumentTitle,
                    _lastAppliedAction.TargetDocumentPath);
                if (undoDocumentSequence > _lastAppliedAction.DocumentChangeSequence)
                {
                    _lastAppliedAction.CanUndo = false;
                    SendMessageActionState(_lastAppliedAction);
                    _lastAppliedAction = null;
                    _bridge.PostMessage("system_message",
                        UiText(
                            "Undo is no longer available because the document changed after apply.",
                            "적용 이후 문서가 변경되어 되돌리기를 더 이상 사용할 수 없습니다."));
                    _bridge.PostMessage("progress", new object[0]);
                    return;
                }

                var action = _lastAppliedAction;
                var undoResult = await UndoLastApplyAsync(action);
                AuditLogService.Append(new AuditEntry
                {
                    Event = "undo",
                    AppVersion = BibimApp.AppVersion,
                    TaskId = action.TaskId,
                    Title = FindTask(action.TaskId)?.Title,
                    Category = FindTask(action.TaskId)?.Category,
                    Document = action.TargetDocumentTitle,
                    DocumentPath = action.TargetDocumentPath,
                    Success = undoResult.Success,
                    Error = undoResult.Success ? null : undoResult.ErrorMessage,
                    Risk = "low"
                });
                if (!undoResult.Success)
                {
                    PostStreamingEndMessage($"{UiText("Undo failed", "되돌리기 실패")}: {undoResult.ErrorMessage}", "error");
                    _bridge.PostMessage("progress", new object[0]);
                    return;
                }

                action.CanUndo = false;
                SendMessageActionState(action);
                _lastAppliedAction = null;

                var task = FindTask(action.TaskId);
                if (task != null)
                {
                    task.Stage = TaskStages.PreviewReady;
                    task.WasApplied = false;
                    task.ResultSummary = UiText(
                        "The last applied change was reverted. Review the result and apply again if needed.",
                        "마지막 실제 적용 결과를 되돌렸습니다. 필요하면 결과를 다시 확인한 뒤 재적용하세요.");
                    UpsertTask(task, autoOpen: true);
                }

                string undoMsg = UiText("The last applied change was reverted.", "마지막 실제 적용 내용을 되돌렸습니다.");
                PostStreamingEndMessage(undoMsg, "system");
                _sessionManager?.AddMessage(_activeSessionId, "assistant", "system", undoMsg);
                SendSessionList();
                _bridge.PostMessage("progress", new object[0]);
            });

            RegisterAsyncHandler("task_feedback", payload =>
            {
                string actionId = payload["actionId"]?.ToString() ?? "";
                string vote = payload["vote"]?.ToString() ?? "";
                string taskId = payload["taskId"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(actionId) ||
                    (vote != "up" && vote != "down") ||
                    !_messageActions.TryGetValue(actionId, out var action)) return Task.CompletedTask;

                action.FeedbackState = vote;

                if (vote == "up")
                {
                    _bridge.PostMessage("feedback_update", new { actionId, step = "up_confirmed" });
                }
                else
                {
                    if (!string.IsNullOrEmpty(action.LibrarySnippetId))
                        _codeLibrary?.Delete(action.LibrarySnippetId);
                    _bridge.PostMessage("feedback_update", new { actionId, step = "down_detail" });
                }
                return Task.CompletedTask;
            });

            RegisterAsyncHandler("task_feedback_detail", payload =>
            {
                string actionId = payload["actionId"]?.ToString() ?? "";
                string taskId = payload["taskId"]?.ToString() ?? "";
                string detail = payload["detail"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(actionId) || string.IsNullOrWhiteSpace(detail)) return Task.CompletedTask;
                _bridge.PostMessage("feedback_update", new { actionId, step = "regen_offer", detail });
                return Task.CompletedTask;
            });

            RegisterAsyncHandler("regenerate_with_feedback", async payload =>
            {
                string actionId = payload["actionId"]?.ToString() ?? "";
                string taskId = payload["taskId"]?.ToString() ?? "";
                string detail = payload["detail"]?.ToString() ?? "";


                string resolvedTaskId = taskId;
                if (string.IsNullOrWhiteSpace(resolvedTaskId) &&
                    _messageActions.TryGetValue(actionId, out var fbAction))
                    resolvedTaskId = fbAction.TaskId;

                var task = FindTask(resolvedTaskId);
                if (task == null)
                {
                    _bridge.PostMessage("system_message",
                        UiText("Task not found. Please start a new session.", "작업을 찾을 수 없습니다. 새 세션을 시작해 주세요."));
                    return;
                }

                string feedbackNote = UiText(
                    $"[User feedback on previous execution: {detail}]\nPlease regenerate addressing this feedback.",
                    $"[이전 실행에 대한 피드백: {detail}]\n피드백을 반영하여 재생성해 주세요.");
                task.SourceUserMessage = string.IsNullOrEmpty(task.SourceUserMessage)
                    ? feedbackNote
                    : $"{task.SourceUserMessage}\n\n{feedbackNote}";

                task.Stage = TaskStages.Working;
                UpsertTask(task, autoOpen: true);

                await GenerateCodeFromTaskAsync(task, runAfterGeneration: true);
            });

            // --- load_session: restore a previous session ---
            RegisterSyncHandler("load_session", payload =>
            {
                string sessionId = payload["sessionId"]?.ToString() ?? "";
                Logger.Log("BridgeHandler", $"load_session: {sessionId}");

                if (_sessionManager == null || string.IsNullOrEmpty(sessionId))
                {
                    _bridge.PostMessage("system_message", UiText("Session not found.", "세션을 찾을 수 없습니다."));
                    return;
                }

                var session = _sessionManager.LoadSession(sessionId);
                if (session == null)
                {
                    _bridge.PostMessage("system_message", UiText("Session not found.", "세션을 찾을 수 없습니다."));
                    return;
                }

                _activeSessionId = sessionId;
                lock (_chatHistoryLock) _chatHistory.Clear();
                _lastCodeGenResult = null;
                lock (_lastCodeGenResultLock) _codeGenByTask.Clear();
                _sessionContext = null;
                ResetMessageActions();

                TokenTracker.ResetSession();
                SendActiveSession();
                EnsureActiveSession(false);

                foreach (var msg in session.Messages)
                {
                    lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = msg.Content, IsUser = msg.Role == "user" });

                    if (msg.Role != "user")
                        TokenTracker.RestoreSessionUsage(msg.InputTokens, msg.OutputTokens, incrementCallCount: true);

                    if (!string.IsNullOrWhiteSpace(msg.CSharpCode))
                        RestoreLastCodeGenerationResult(msg.Content, msg.CSharpCode, msg.InputTokens, msg.OutputTokens);

                    string msgType;
                    switch ((msg.ContentType ?? "").ToLowerInvariant())
                    {
                        case "code": case "question": case "system": case "error":
                            msgType = msg.ContentType.ToLowerInvariant(); break;
                        default:
                            msgType = !string.IsNullOrWhiteSpace(msg.CSharpCode) ? "code" : "normal"; break;
                    }

                    _bridge.PostMessage("streaming_end", new
                    {
                        text = msg.Content,
                        type = msgType,
                        isUser = msg.Role == "user",
                        csharpCode = msg.CSharpCode,
                        inputTokens = msg.InputTokens,
                        outputTokens = msg.OutputTokens,
                        elapsedMs = 0,
                        createdAt = msg.CreatedAt != default ? msg.CreatedAt.ToString("o") : null
                    });
                }

                var restoredTask = GetActiveTask();
                if (_lastCodeGenResult == null && !string.IsNullOrWhiteSpace(restoredTask?.GeneratedCode))
                {
                    RestoreLastCodeGenerationResult(restoredTask.ResultSummary ?? restoredTask.Title, restoredTask.GeneratedCode, 0, 0);
                    // Seed ONLY here — the slot provably holds THIS task's code. An
                    // unconditional seed would bind whatever the last replayed code
                    // message was (possibly another task's) as A's authoritative entry.
                    if (_lastCodeGenResult != null && !string.IsNullOrEmpty(restoredTask.TaskId))
                        lock (_lastCodeGenResultLock) _codeGenByTask[restoredTask.TaskId] = _lastCodeGenResult;
                }


                SendTaskList();
                SendTaskState();
                Logger.Log("BridgeHandler", $"Session loaded: {session.Messages.Count} messages");
            });

            // --- new_session: create a fresh session ---
            _bridge.On("new_session", (_) =>
            {
                try
                {
                    Logger.Log("BridgeHandler", "new_session");
                    lock (_chatHistoryLock) _chatHistory.Clear();
                    _lastCodeGenResult = null;
                lock (_lastCodeGenResultLock) _codeGenByTask.Clear();
                    _sessionContext = null;
                    ResetMessageActions();
                    TokenTracker.ResetSession();

                    if (_sessionManager != null)
                    {
                        var session = _sessionManager.CreateSession();
                        _activeSessionId = session.SessionId;
        
                        SendActiveSession();
                        EnsureActiveSession(false);
                    }

                    SendSessionList();
    
                    SendTaskList();
                    SendTaskState();
                }
                catch (Exception ex)
                {
                    Logger.LogError("BridgeHandler.new_session", ex);
                }
            });

            // --- delete_session: remove a session from local storage ---
            RegisterSyncHandler("delete_session", payload =>
            {
                string sessionId = payload["sessionId"]?.ToString() ?? "";
                Logger.Log("BridgeHandler", $"delete_session: {sessionId}");
                if (_sessionManager == null || string.IsNullOrEmpty(sessionId)) return;

                _sessionManager.DeleteSession(sessionId);

                bool wasActive = _activeSessionId == sessionId;
                if (wasActive)
                {
                    _activeSessionId = null;
                    lock (_chatHistoryLock) _chatHistory.Clear();
                    _sessionContext = null;
                    _lastCodeGenResult = null;
                lock (_lastCodeGenResultLock) _codeGenByTask.Clear();
                    TokenTracker.ResetSession();
                }

                SendSessionList();
                if (wasActive) { SendTaskList(); SendTaskState(); }
                _bridge.PostMessage("session_deleted", new { sessionId, wasActive });
            });

            // --- rename_session: update a session's title ---
            RegisterSyncHandler("rename_session", payload =>
            {
                string sessionId = payload["sessionId"]?.ToString() ?? "";
                string title = payload["title"]?.ToString() ?? "";
                Logger.Log("BridgeHandler", $"rename_session: {sessionId} => {title}");
                if (_sessionManager == null || string.IsNullOrEmpty(sessionId)) return;

                _sessionManager.RenameSession(sessionId, title);
                SendSessionList();
                _bridge.PostMessage("session_renamed", new { sessionId, title });
            });

            RegisterAsyncHandler("rerun_code", async payload =>
            {
                string sourceSessionId = payload["sourceSessionId"]?.ToString() ?? "";
                string sourceTitle = payload["sourceTitle"]?.ToString() ?? "";
                string code = payload["code"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(code) || _sessionManager == null) return;

                try
                {
                    lock (_chatHistoryLock) _chatHistory.Clear();
                    _lastCodeGenResult = null;
                lock (_lastCodeGenResultLock) _codeGenByTask.Clear();
                    _sessionContext = null;
                    ResetMessageActions();
                    TokenTracker.ResetSession();

                    var session = _sessionManager.CreateSession();
                    string dateStr = DateTime.Now.ToString("MM.dd");
                    session.ParentSessionId = string.IsNullOrWhiteSpace(sourceSessionId) ? null : sourceSessionId;
                    session.Title = string.IsNullOrWhiteSpace(sourceTitle)
                        ? UiText($"Rerun ({dateStr})", $"재실행 ({dateStr})")
                        : $"{sourceTitle} - {UiText($"Rerun ({dateStr})", $"재실행 ({dateStr})")}";

                    _activeSessionId = session.SessionId;
                    _sessionManager.SaveSession(session);
    
                    SendActiveSession();
                    EnsureActiveSession(false);
                    SendSessionList();
    

                    var compiler = GetCompiler();
                    var compileResult = compiler.Compile(code);
                    if (!compileResult.Success)
                    {
                        Logger.Log("RerunCode", $"Compile failed: {compileResult.ErrorSummary}");
                        SendAssistantMessage(
                            UiText("The code failed to compile and could not be loaded. Please try regenerating.",
                                   "코드 컴파일에 실패해서 불러오지 못했습니다. 다시 생성해 보세요."),
                            "error", csharpCode: code, messageType: "error");
                        return;
                    }

                    _lastCodeGenResult = new CodeGenerationResult
                    {
                        Success = true, IsCodeResponse = true, GeneratedCode = code,
                        CompilationResult = compileResult, CompileAttempts = 1
                    };

                    SendAssistantMessage(
                        UiText(
                            $"I loaded code from history.\nSource session: {sourceTitle}\n\nReview the preview before applying changes.",
                            $"히스토리에서 코드를 불러왔습니다.\n원본 세션: {sourceTitle}\n\n미리보기를 확인한 뒤 적용하세요."),
                        "text", csharpCode: code, messageType: "system");

                    _bridge.PostMessage("progress", new[] {
                        new { label = UiText("Running preview...", "미리보기를 실행하는 중..."), status = "active" }
                    });

                    var execResult = await ExecuteCompiledCodeAsync(isDryRun: true);
                    var report = InspectGeneratedCode(code, execResult);

                    var task = new TaskState
                    {
                        TaskId = Guid.NewGuid().ToString(),
                        Title = session.Title,
                        Summary = UiText("Re-run a previously generated C# script from history.",
                            "히스토리에서 생성된 C# 스크립트를 다시 실행합니다."),
                        Stage = execResult.Success ? TaskStages.PreviewReady : TaskStages.Review,
                        Kind = TaskKinds.Write, RequiresApply = true, GeneratedCode = code,
                        Review = BuildTaskReviewSummary(report, execResult),
                        ResultSummary = execResult.Success
                            ? UiText("Preview completed. Review the result before applying changes.",
                                "미리보기가 완료되었습니다. 결과를 확인한 뒤 실제 적용하세요.")
                            : $"{UiText("Preview failed", "미리 검증 실패")}: {execResult.ErrorMessage}",
                    };
                    BindTaskToExecutedDocument(task, execResult);
                    // Pair the rerun task with ITS code — otherwise a later taskless
                    // slot overwrite (spec flow) would commit foreign code from R's card.
                    lock (_lastCodeGenResultLock) _codeGenByTask[task.TaskId] = _lastCodeGenResult;
                    UpsertTask(task, autoOpen: true);
                    SendAssistantMessage(task.ResultSummary, "text", csharpCode: code,
                        messageType: execResult.Success ? "system" : "error");
                }
                finally
                {
                    _bridge.PostMessage("progress", new object[0]);
                }
            });

            RegisterSyncHandler("edit_code", payload =>
            {
                string sourceTitle = payload["sourceTitle"]?.ToString() ?? "";
                string code = payload["code"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(code) || _sessionManager == null) return;

                lock (_chatHistoryLock) _chatHistory.Clear();
                _lastCodeGenResult = null;
                lock (_lastCodeGenResultLock) _codeGenByTask.Clear();
                _sessionContext = null;
                ResetMessageActions();
                TokenTracker.ResetSession();

                var session = _sessionManager.CreateSession();
                string dateStr = DateTime.Now.ToString("MM.dd");
                session.Title = string.IsNullOrWhiteSpace(sourceTitle)
                    ? UiText($"Edit ({dateStr})", $"수정 ({dateStr})")
                    : $"{sourceTitle} - {UiText($"Edit ({dateStr})", $"수정 ({dateStr})")}";

                _activeSessionId = session.SessionId;
                _sessionManager.SaveSession(session);

                SendActiveSession();
                EnsureActiveSession(false);
                SendSessionList();


                SendAssistantMessage(
                    UiText(
                        $"I loaded code from the library for editing.\n\n```csharp\n{code}\n```\n\nPlease describe the changes you'd like to make.",
                        $"코드 보관함에서 코드를 불러왔습니다.\n\n```csharp\n{code}\n```\n\n수정사항을 입력해주세요."),
                    "text", csharpCode: code, messageType: "system");
            });

            // --- OSS: auth/subscription bridge handlers removed (login, logout, check_auth, get_subscription, get_credits) ---
            // BYOK: no server-side auth. API key is stored locally in rag_config.json.

            _bridge.On("get_app_info", (_) => SendAppInfo());
            _bridge.On("get_sessions", (_) => SendSessionList());
            _bridge.On("get_task_state", (_) => SendTaskState());
            _bridge.On("get_task_list", (_) => SendTaskList());
            _bridge.On("get_code_library", (_) => SendCodeLibrary());
            _bridge.On("get_api_key_status", (_) => SendApiKeyStatus());
            // Anthropic / Claude — kept the legacy "save_api_key" name for back-compat with the frontend.
            RegisterSyncHandler("save_api_key", payload =>
            {
                string newKey = payload["apiKey"]?.ToString() ?? "";
                try
                {
                    ConfigService.SaveApiKeyForProvider("anthropic", newKey);
                    _llmService = null;
                    _plannerLlmService = null;
                    Logger.Log("BridgeHandler", "Anthropic API key saved");
                    SendApiKeyStatus();
                    _bridge.PostMessage("api_key_save_result", new { success = true, provider = "anthropic" });
                }
                catch (Exception ex)
                {
                    Logger.Log("BridgeHandler", $"save_api_key (anthropic) failed: {ex.Message}");
                    _bridge.PostMessage("api_key_save_result", new { success = false, provider = "anthropic", error = ex.Message });
                }
            });
            RegisterSyncHandler("save_openai_api_key", payload =>
            {
                string newKey = payload["apiKey"]?.ToString() ?? "";
                try
                {
                    ConfigService.SaveApiKeyForProvider("openai", newKey);
                    _llmService = null;
                    _plannerLlmService = null;
                    Logger.Log("BridgeHandler", "OpenAI API key saved");
                    SendApiKeyStatus();
                    _bridge.PostMessage("api_key_save_result", new { success = true, provider = "openai" });
                }
                catch (Exception ex)
                {
                    Logger.Log("BridgeHandler", $"save_openai_api_key failed: {ex.Message}");
                    _bridge.PostMessage("api_key_save_result", new { success = false, provider = "openai", error = ex.Message });
                }
            });

            RegisterSyncHandler("save_model", payload =>
            {
                string modelId = payload["modelId"]?.ToString() ?? "";
                try
                {
                    ConfigService.SaveModel(modelId);
                    // Reset LLM service so it picks up new model
                    _llmService = null;
                    _plannerLlmService = null;
                    Logger.Log("BridgeHandler", $"Model saved: {modelId}");
                    SendApiKeyStatus();
                    _bridge.PostMessage("model_save_result", new { success = true });
                }
                catch (Exception ex)
                {
                    Logger.Log("BridgeHandler", $"save_model failed: {ex.Message}");
                    _bridge.PostMessage("model_save_result", new { success = false, error = ex.Message });
                }
            });

            // Save the self-hosted local LLM server config (URL + model name + optional API key).
            // Empty serverUrl clears the config. Reset cached LLM service so subsequent calls
            // pick up the new endpoint immediately.
            RegisterSyncHandler("save_local_llm_config", payload =>
            {
                string serverUrl = payload["serverUrl"]?.ToString() ?? "";
                string modelName = payload["modelName"]?.ToString() ?? "";
                string apiKey    = payload["apiKey"]?.ToString() ?? "";
                try
                {
                    ConfigService.SaveLocalServerConfig(serverUrl, modelName, apiKey);
                    _llmService = null;
                    _plannerLlmService = null;
                    Logger.Log("BridgeHandler",
                        $"Local LLM config saved — url={(string.IsNullOrEmpty(serverUrl) ? "(cleared)" : serverUrl)} model={(string.IsNullOrEmpty(modelName) ? "(none)" : modelName)} keyConfigured={!string.IsNullOrEmpty(apiKey)}");
                    SendApiKeyStatus();
                    _bridge.PostMessage("api_key_save_result", new { success = true, provider = "local" });
                }
                catch (Exception ex)
                {
                    Logger.Log("BridgeHandler", $"save_local_llm_config failed: {ex.Message}");
                    _bridge.PostMessage("api_key_save_result", new { success = false, provider = "local", error = ex.Message });
                }
            });

            // Connection probe for the Local LLM section (red/blue indicator).
            // Performs a GET /models against the supplied URL with optional bearer auth,
            // returns parsed model count + sample model id, or the upstream error string.
            RegisterAsyncHandler("test_local_connection", async payload =>
            {
                string serverUrl = payload["serverUrl"]?.ToString() ?? "";
                string apiKey    = payload["apiKey"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(serverUrl))
                {
                    _bridge.PostMessage("local_connection_test_result", new
                    {
                        success = false,
                        error = "Server URL is empty."
                    });
                    return;
                }

                try
                {
                    // Construct a throwaway provider just for the /models call.
                    // modelId is required by the ctor but unused on /models.
                    var probe = new LocalProvider(
                        apiKey: apiKey,
                        modelId: "probe/dummy",
                        httpClient: _downloadHttpClient,
                        baseUrl: serverUrl,
                        serverModelName: null);

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    {
                        var modelsJson = await probe.ListModelsAsync(cts.Token);
                        var data = modelsJson?["data"] as Newtonsoft.Json.Linq.JArray;
                        int count = data?.Count ?? 0;

                        // Return the full id list (capped at 50 entries to bound the
                        // payload — most local servers expose 1-10 models, but some
                        // proxies in front of model hubs expose hundreds). The frontend
                        // uses this list to auto-populate the model selector so the
                        // user doesn't have to know the exact server-side model name.
                        var modelIds = new List<string>();
                        if (data != null)
                        {
                            foreach (var entry in data)
                            {
                                if (modelIds.Count >= 50) break;
                                string id = entry?["id"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(id)) modelIds.Add(id);
                            }
                        }

                        _bridge.PostMessage("local_connection_test_result", new
                        {
                            success = true,
                            modelCount = count,
                            firstModel = modelIds.Count > 0 ? modelIds[0] : "",
                            models = modelIds   // NEW: full list for the auto-populate dropdown
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    _bridge.PostMessage("local_connection_test_result", new
                    {
                        success = false,
                        error = "Connection timed out after 8 seconds."
                    });
                }
                catch (Exception ex)
                {
                    Logger.Log("BridgeHandler", $"test_local_connection failed: {ex.Message}");
                    _bridge.PostMessage("local_connection_test_result", new
                    {
                        success = false,
                        error = ex.Message
                    });
                }
            });
            RegisterSyncHandler("get_code_snippet", payload =>
            {
                var snippet = _codeLibrary?.GetById(payload["id"]?.ToString());
                if (snippet != null)
                    _bridge.PostMessage("code_snippet", new
                    {
                        id = snippet.Id,
                        title = snippet.Title,
                        summary = snippet.Summary,
                        code = snippet.Code,
                        revitVersion = snippet.RevitVersion,
                        taskKind = snippet.TaskKind,
                        sourceSessionId = snippet.SourceSessionId,
                        createdAt = snippet.CreatedAt.ToString("o")
                    });
            });

            RegisterSyncHandler("delete_code_snippet", payload =>
            {
                _codeLibrary?.Delete(payload["id"]?.ToString());
                SendCodeLibrary();
            });

            RegisterSyncHandler("rename_snippet", payload =>
            {
                _codeLibrary?.RenameSnippet(payload["id"]?.ToString(), payload["title"]?.ToString() ?? "");
                SendCodeLibrary();
            });

            RegisterSyncHandler("move_snippet", payload =>
            {
                var folderToken = payload["folderId"];
                string folderId = (folderToken == null || folderToken.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                    ? null : folderToken.ToString();
                _codeLibrary?.MoveSnippet(payload["snippetId"]?.ToString(), folderId);
                SendCodeLibrary();
            });

            RegisterSyncHandler("create_folder", payload =>
            {
                _codeLibrary?.CreateFolder(payload["name"]?.ToString() ?? "New Folder", payload["parentId"]?.ToString());
                SendCodeLibrary();
            });

            RegisterSyncHandler("rename_folder", payload =>
            {
                _codeLibrary?.RenameFolder(payload["id"]?.ToString(), payload["name"]?.ToString() ?? "");
                SendCodeLibrary();
            });

            RegisterSyncHandler("delete_folder", payload =>
            {
                _codeLibrary?.DeleteFolder(payload["id"]?.ToString());
                SendCodeLibrary();
            });

            RegisterAsyncHandler("check_update", async _ =>
            {
                var result = await VersionChecker.CheckForUpdatesAsync();
                BibimApp.LastVersionCheckResult = result.UpdateRequired ? result : null;
                SendUpdateInfo(result);
            });

            RegisterAsyncHandler("download_update", async payload =>
            {
                string url = payload["url"]?.ToString() ?? "";
                string version = payload["version"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(url)) return;

                // Defense-in-depth: even though `url` originates from VersionChecker's
                // GitHub Releases API call, by the time it reaches this handler it has
                // round-tripped through the WebView; treat as untrusted. Reject anything
                // that isn't https on a known release-asset host. Failing closed here
                // is the safer default — the user can always download manually via
                // open_url if the URL is legitimate but on a new host.
                if (!IsTrustedReleaseAssetUrl(url, out string urlRejectReason))
                {
                    Logger.Log("BridgeHandler",
                        $"download_update: rejected URL ({urlRejectReason}): {Truncate(url, 200)}");
                    _bridge?.PostMessage("download_progress",
                        new { status = "error", error = "untrusted_url" });
                    return;
                }

                string downloadsFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                System.IO.Directory.CreateDirectory(downloadsFolder);

                string fileName = string.IsNullOrWhiteSpace(version)
                    ? "BIBIM_AI_Setup.exe"
                    : $"BIBIM_AI_v{version}_Setup.exe";
                string filePath = System.IO.Path.Combine(downloadsFolder, fileName);

                _bridge?.PostMessage("download_progress", new { status = "downloading" });

                // Hard cap the entire download — body streaming bypasses the
                // HttpClient.Timeout default (which only covers headers in async paths),
                // so without a token a stuck connection can keep the UI in "downloading"
                // forever. 10 min is generous for a ~50MB installer over a slow link.
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10)))
                {
                    try
                    {
                        using (var response = await _downloadHttpClient.GetAsync(
                                   url,
                                   System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                                   cts.Token))
                        {
                            response.EnsureSuccessStatusCode();
                            using (var stream = await response.Content.ReadAsStreamAsync())
                            using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create))
                            {
                                await stream.CopyToAsync(fs, 81920, cts.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.Log("BridgeHandler", "download_update: timeout (10 min) reached");
                        _bridge?.PostMessage("download_progress",
                            new { status = "error", error = "timeout" });
                        try { if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath); } catch { }
                        return;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("BridgeHandler", $"download_update: {ex.GetType().Name}: {ex.Message}");
                        _bridge?.PostMessage("download_progress",
                            new { status = "error", error = "network" });
                        try { if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath); } catch { }
                        return;
                    }
                }

                _bridge?.PostMessage("download_complete", new { folderPath = downloadsFolder, fileName });
            });

            RegisterSyncHandler("open_folder", payload =>
            {
                string path = payload["path"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", path);
            });

            RegisterSyncHandler("open_url", payload =>
            {
                string url = payload["url"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(url)
                    && (url.StartsWith("https://") || url.StartsWith("http://")))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
            });
        }

        private void SendApiKeyStatus()
        {
            try
            {
                string anthropicMasked = ConfigService.GetMaskedKeyForProvider("anthropic");
                string openAiMasked    = ConfigService.GetMaskedKeyForProvider("openai");
                string localMasked     = ConfigService.GetMaskedKeyForProvider("local");
                string activeModel = ConfigService.GetRagConfig()?.ClaudeModel ?? ConfigService.DefaultModelId;
                string activeProvider = ConfigService.GetActiveProviderName();

                var cfg = ConfigService.GetRagConfig();
                string localServerUrl = cfg?.LocalServerUrl ?? "";
                string localModelName = cfg?.LocalModelName ?? "";
                // Local "configured" is gated by server URL, NOT key (key is optional).
                bool localConfigured = !string.IsNullOrWhiteSpace(localServerUrl);

                // Aggregate active state — for local, key absence is allowed when URL is set.
                bool activeConfigured = activeProvider == "local"
                    ? localConfigured
                    : !string.IsNullOrEmpty(ConfigService.GetActiveCredentials().ApiKey);

                _bridge?.PostMessage("api_key_status", new
                {
                    configured = activeConfigured,
                    activeProvider,
                    activeModel,
                    // Per-provider status for the UI's gated model selector.
                    anthropic = new { configured = !string.IsNullOrEmpty(anthropicMasked), maskedKey = anthropicMasked },
                    openai    = new { configured = !string.IsNullOrEmpty(openAiMasked),    maskedKey = openAiMasked },
                    local     = new {
                        configured = localConfigured,
                        serverUrl  = localServerUrl,
                        modelName  = localModelName,
                        maskedKey  = localMasked
                    },
                    // Legacy fields kept so older frontend builds keep rendering.
                    maskedKey = anthropicMasked,
                    claudeModel = activeModel,
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendApiKeyStatus", ex);
                _bridge?.PostMessage("api_key_status", new
                {
                    configured = false,
                    activeProvider = "anthropic",
                    activeModel = ConfigService.DefaultModelId,
                    anthropic = new { configured = false, maskedKey = "" },
                    openai    = new { configured = false, maskedKey = "" },
                    maskedKey = "",
                    claudeModel = ConfigService.DefaultModelId,
                });
            }
        }

        private void SendAppInfo()
        {
            try
            {
                var update = BibimApp.LastVersionCheckResult;
                _bridge?.PostMessage("app_info", new
                {
                    version = BibimApp.AppVersion,
                    language = LocalizationService.CurrentLanguage,
                    revitVersion = ConfigService.GetEffectiveRevitVersion(),
                    updateAvailable = update?.UpdateRequired == true,
                    updateMandatory = update?.IsMandatory == true,
                    latestVersion = update?.LatestVersion,
                    downloadUrl = update?.DownloadUrl,
                    releaseNotes = update?.ReleaseNotes,
                    releaseNotesUrl = update?.ReleaseNotesUrl
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendAppInfo", ex);
            }
        }

        private void OnVersionCheckCompleted(VersionCheckResult result)
        {
            SendUpdateInfo(result);
        }

        private void SendUpdateInfo(VersionCheckResult result)
        {
            try
            {
                _bridge?.PostMessage("update_info", new
                {
                    updateAvailable = result?.UpdateRequired == true,
                    isMandatory = result?.IsMandatory == true,
                    currentVersion = result?.CurrentVersion ?? BibimApp.AppVersion,
                    latestVersion = result?.LatestVersion,
                    downloadUrl = result?.DownloadUrl,
                    releaseNotes = result?.ReleaseNotes,
                    releaseNotesUrl = result?.ReleaseNotesUrl
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendUpdateInfo", ex);
            }
        }

        private void SendActiveSession()
        {
            try
            {
                _bridge?.PostMessage("active_session", new { sessionId = _activeSessionId });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendActiveSession", ex);
            }
        }

        /// <summary>
        /// Send the session list to the frontend for the history panel.
        /// </summary>
        private void SendSessionList()
        {
            try
            {
                if (_sessionManager == null || _bridge == null) return;

                var sessions = _sessionManager.GetAllSessions();
                var sessionInfos = sessions.Select(s => new
                {
                    id = s.SessionId,
                    title = s.Title ?? "",
                    createdAt = s.CreatedAt.ToString("o"),
                    messageCount = s.Messages?.Count ?? 0,
                    parentSessionId = s.ParentSessionId
                }).ToArray();

                _bridge.PostMessage("sessions", sessionInfos);
            }
            catch (Exception ex)
            {
                Logger.LogError("SendSessionList", ex);
            }
        }

        private void SendCodeLibrary()
        {
            try
            {
                if (_codeLibrary == null || _bridge == null) return;
                var snippets = _codeLibrary.GetAll();
                var folders = _codeLibrary.GetFolders();
                var snippetItems = snippets.Select(s => new
                {
                    id = s.Id,
                    title = s.Title ?? "",
                    summary = s.Summary ?? "",
                    revitVersion = s.RevitVersion ?? "",
                    taskKind = s.TaskKind ?? "write",
                    createdAt = s.CreatedAt.ToString("o"),
                    folderId = s.FolderId  // null = uncategorized
                }).ToArray();
                var folderItems = folders.Select(f => new
                {
                    id = f.Id,
                    name = f.Name ?? "",
                    parentId = f.ParentId
                }).ToArray();
                _bridge.PostMessage("code_library", new { folders = folderItems, snippets = snippetItems });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendCodeLibrary", ex);
            }
        }

        private void RestoreLastCodeGenerationResult(string responseText, string code,
            int inputTokens, int outputTokens)
        {
            if (string.IsNullOrWhiteSpace(code))
                return;

            try
            {
                var compiler = GetCompiler();
                var compileResult = compiler.Compile(code);
                _lastCodeGenResult = new CodeGenerationResult
                {
                    Success = compileResult.Success,
                    IsCodeResponse = true,
                    GeneratedCode = code,
                    RawResponse = responseText,
                    CompilationResult = compileResult,
                    CompileAttempts = compileResult.Success ? 1 : 0,
                    TotalInputTokens = inputTokens,
                    TotalOutputTokens = outputTokens
                };
            }
            catch (Exception ex)
            {
                Logger.LogError("RestoreLastCodeGenerationResult", ex);
            }
        }
    }
}
