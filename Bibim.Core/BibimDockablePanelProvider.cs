// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
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
    /// <summary>
    /// Dockable panel hosting WebView2 for the React frontend.
    /// Implements IDockablePaneProvider for Revit docking.
    /// See design doc §1 — Architecture Diagram.
    /// 
    /// Chat flow uses LlmOrchestrationService (Anthropic SDK streaming)
    /// instead of raw HTTP calls. Bridge events:
    ///   streaming_delta  → partial text tokens (real-time)
    ///   streaming_end    → final message with token usage
    /// </summary>
    public partial class BibimDockablePanelProvider : IDockablePaneProvider, IDisposable
    {
        private WebView2 _webView;
        private Grid _hostGrid;
        private WebView2Bridge _bridge;
        private bool _initialized;

        // LLM service — lazy-initialized on first message
        private LlmOrchestrationService _llmService;
        // Planner uses a dedicated lightweight instance (non-streaming, no bridge events)
        private LlmOrchestrationService _plannerLlmService;
        private readonly List<ChatMessage> _chatHistory = new List<ChatMessage>();
        private readonly object _chatHistoryLock = new object();
        private CancellationTokenSource _streamingCts;
        private static readonly System.Net.Http.HttpClient _downloadHttpClient = new System.Net.Http.HttpClient();
        // Sliding window: pass at most this many message turns to the LLM (oldest dropped)
        private const int ChatHistoryMaxTurns = BibimConstants.ChatHistoryMaxTurns;
        // Summary of the dropped prefix, cached per (session, window start) so the
        // synthetic first message stays byte-identical while the window start holds
        // (task titles in the summary would otherwise change it every turn).
        private string _historySummaryCacheKey;
        private string _historySummaryCacheText;

        // Session management
        private LocalSessionManager _sessionManager;
        private CodeLibraryService _codeLibrary;
        private string _activeSessionId;
        private SessionContext _sessionContext;

        // Last code generation result — needed for execute (dryrun/commit).
        // Accessed from both UI thread and background task threads.
        // Property wraps lock so all call sites remain unchanged.
        private CodeGenerationResult _lastCodeGenResultValue;
        private readonly object _lastCodeGenResultLock = new object();
        private CodeGenerationResult _lastCodeGenResult
        {
            get { lock (_lastCodeGenResultLock) return _lastCodeGenResultValue; }
            set { lock (_lastCodeGenResultLock) _lastCodeGenResultValue = value; }
        }

        /// <summary>
        /// Per-task codegen results. The shared slot above is a SINGLE slot, so
        /// interleaved tasks could Apply task-B's assembly under task-A's card
        /// (generate A → regenerate B → press Apply on A's card ran B's code).
        /// Execute resolves through this dictionary first; the shared slot stays
        /// as the fallback for taskless flows (spec, rerun, session restore).
        /// Cleared wherever _lastCodeGenResult is nulled (session switches).
        /// </summary>
        private readonly Dictionary<string, CodeGenerationResult> _codeGenByTask
            = new Dictionary<string, CodeGenerationResult>();

        private CodeGenerationResult ResolveCodeGenForTask(TaskState task)
        {
            lock (_lastCodeGenResultLock)
            {
                if (!string.IsNullOrEmpty(task?.TaskId) &&
                    _codeGenByTask.TryGetValue(task.TaskId, out var perTask))
                    return perTask;
            }
            return _lastCodeGenResult;
        }
        private LastAppliedAction _lastAppliedAction;
        private List<string> _lastRevitWarnings;
        private readonly Dictionary<string, LastAppliedAction> _messageActions =
            new Dictionary<string, LastAppliedAction>();

        // Revit context provider — resolves @context tags to live Revit data
        private RevitContextProvider _contextProvider;

        // Roslyn analyzer — BIBIM001-005 custom analyzers
        private readonly RoslynAnalyzerService _analyzerService = new RoslynAnalyzerService();
        private const int PlannerContextWindow = BibimConstants.PlannerContextWindow;
        private int _suppressStreamingDeltaCount;
        private string _pendingLibrarySnippetId;

        private class LastAppliedAction
        {
            public string ActionId { get; set; }
            public string TaskId { get; set; }
            public bool CanUndo { get; set; }
            public string FeedbackState { get; set; }
            public string TargetDocumentTitle { get; set; }
            public string TargetDocumentPath { get; set; }
            public long DocumentChangeSequence { get; set; }
            public string LibrarySnippetId { get; set; }
        }

        /// <summary>
        /// The WPF element Revit will host in the dockable pane.
        /// </summary>
        public FrameworkElement HostElement => _hostGrid;

        /// <summary>
        /// Bridge for C# ↔ WebView2 bidirectional messaging.
        /// </summary>
        public WebView2Bridge Bridge => _bridge;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            _hostGrid = new Grid();

            _webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            _hostGrid.Children.Add(_webView);

            data.FrameworkElement = _hostGrid;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right,
                TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
            };

            // Initialize WebView2 asynchronously after panel is set up
            _ = InitializeWebViewAsync();
        }

        private async Task InitializeWebViewAsync()
        {
            if (_initialized) return;

            try
            {
                // WebView2 user data folder — avoid conflicts with other add-ins
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BIBIM", "WebView2");

                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .CreateAsync(null, userDataFolder);

                await _webView.EnsureCoreWebView2Async(env);

                // Set up the C# ↔ JS bridge
                _bridge = new WebView2Bridge(_webView);
                _bridge.Initialize();
                RegisterBridgeHandlers();

                // Subscribe to background version check completion.
                // PostMessage is dispatcher-safe so firing from background thread is fine.
                BibimApp.VersionCheckCompleted += OnVersionCheckCompleted;
                // If the check already finished before we subscribed, push immediately.
                var pendingUpdate = BibimApp.LastVersionCheckResult;
                if (pendingUpdate != null)
                    SendUpdateInfo(pendingUpdate);

                // Initialize session manager. The frontend requests bootstrap state
                // after its bridge handlers are registered, which is more reliable
                // than sending messages before the page has loaded.
                _sessionManager = new LocalSessionManager();
                _sessionManager.EnsureStorageFolder();
                _codeLibrary = new CodeLibraryService();

                // Navigate to the React frontend
                string assemblyDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? "";
                string indexPath = Path.Combine(assemblyDir, "wwwroot", "index.html");

                Logger.Log("BibimDockablePanel", $"Assembly dir: {assemblyDir}");
                Logger.Log("BibimDockablePanel", $"index.html exists: {File.Exists(indexPath)}");

                if (File.Exists(indexPath))
                {
                    // Use SetVirtualHostNameToFolderMapping for proper local file access
                    string wwwrootDir = Path.Combine(assemblyDir, "wwwroot");
                    _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        "bibim.local", wwwrootDir,
                        Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                    _webView.CoreWebView2.Navigate("https://bibim.local/index.html");
                    Logger.Log("BibimDockablePanel", "Navigating via virtual host: https://bibim.local/index.html");
                }
                else
                {
                    _webView.CoreWebView2.NavigateToString(GetPlaceholderHtml());
                    Logger.Log("BibimDockablePanel", "Using placeholder HTML");
                }

                _initialized = true;
                Logger.Log("BibimDockablePanel", "WebView2 initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError("BibimDockablePanel", ex);

                // Show error in the panel
                var errorBlock = new TextBlock
                {
                    Text = $"WebView2 initialization failed:\n{ex.Message}\n\nPlease ensure WebView2 Runtime is installed.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _hostGrid.Children.Clear();
                _hostGrid.Children.Add(errorBlock);
            }
        }

        private string GetPlaceholderHtml()
        {
            return @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
    background: #0f1117;
    color: #e4e4e7;
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100vh;
    padding: 24px;
  }
  .container { text-align: center; max-width: 400px; }
  h1 { font-size: 20px; font-weight: 600; margin-bottom: 8px; color: #818cf8; }
  p { font-size: 14px; color: #a1a1aa; line-height: 1.6; }
  .status { margin-top: 24px; padding: 12px; background: #1e1e2e; border-radius: 8px; font-size: 12px; color: #6366f1; }
</style>
</head>
<body>
<div class='container'>
  <h1>BIBIM AI v" + BibimApp.AppVersion + @"</h1>
  <p>React frontend is not built yet.<br>Run <code>npm run build</code> in the frontend directory.</p>
  <div class='status'>Phase 1 — Boilerplate Ready</div>
</div>
</body>
</html>";
        }

        public void Dispose()
        {
            try
            {
                BibimApp.VersionCheckCompleted -= OnVersionCheckCompleted;
                _streamingCts?.Cancel();
                _streamingCts?.Dispose();
                _bridge?.Dispose();
                _webView?.Dispose();
            }
            catch { }
        }

        /// <summary>
        /// Atomically replaces _streamingCts and cancels/disposes the old one.
        /// Returns a CancellationToken from the new source.
        /// Using Interlocked.Exchange prevents a race between UI-thread replacement
        /// and any background thread that reads the field directly.
        /// </summary>
        private CancellationToken ReplaceCts()
        {
            var newCts = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref _streamingCts, newCts);
            old?.Cancel();
            old?.Dispose();
            return newCts.Token;
        }

        private string UiText(string english, string korean)
        {
            return AppLanguage.Pick(english, korean);
        }

        private void EnsureActiveSession(bool createIfMissing = true)
        {
            if (_sessionManager == null)
                return;

            if (string.IsNullOrEmpty(_activeSessionId))
            {
                if (!createIfMissing)
                    return;

                var session = _sessionManager.CreateSession();
                _activeSessionId = session.SessionId;

                SendActiveSession();
            }

            if (_sessionContext == null || _sessionContext.SessionId != _activeSessionId)
            {
                _sessionContext = _sessionManager.LoadSessionContext(_activeSessionId)
                    ?? new SessionContext { SessionId = _activeSessionId };
                _sessionContext.Tasks = _sessionContext.Tasks ?? new List<TaskState>();
            }
        }

        private void SaveSessionContext()
        {
            if (_sessionManager == null || _sessionContext == null || string.IsNullOrEmpty(_activeSessionId))
                return;

            _sessionContext.SessionId = _activeSessionId;
            _sessionContext.LastUpdated = DateTime.UtcNow;
            _sessionManager.SaveSessionContext(_sessionContext);
        }

        private List<TaskState> GetTasks()
        {
            EnsureActiveSession(false);
            return _sessionContext?.Tasks ?? new List<TaskState>();
        }

        private TaskState GetActiveTask()
        {
            EnsureActiveSession(false);
            if (_sessionContext == null || string.IsNullOrEmpty(_sessionContext.ActiveTaskId))
                return null;

            return _sessionContext.Tasks?.FirstOrDefault(t => t.TaskId == _sessionContext.ActiveTaskId);
        }

        private TaskState FindTask(string taskId)
        {
            EnsureActiveSession(false);
            if (_sessionContext?.Tasks == null || string.IsNullOrWhiteSpace(taskId))
                return null;

            return _sessionContext.Tasks.FirstOrDefault(t => t.TaskId == taskId);
        }

        private bool IsTaskTerminal(TaskState task)
        {
            return task != null &&
                (task.Stage == TaskStages.Completed || task.Stage == TaskStages.Cancelled);
        }

        private void UpsertTask(TaskState task, bool autoOpen = false)
        {
            if (task == null)
                return;

            EnsureActiveSession();

            var tasks = _sessionContext.Tasks ?? (_sessionContext.Tasks = new List<TaskState>());
            int index = tasks.FindIndex(t => t.TaskId == task.TaskId);
            task.AutoOpen = autoOpen;
            task.UpdatedAt = DateTime.UtcNow;

            if (index >= 0)
                tasks[index] = task;
            else
                tasks.Add(task);

            _sessionContext.ActiveTaskId = task.TaskId;
            SaveSessionContext();
            SendTaskState();
            SendTaskList();
        }

        private void SendTaskState()
        {
            try
            {
                var task = GetActiveTask();
                _bridge?.PostMessage("task_state", ToTaskPayload(task));
                if (task != null && task.AutoOpen)
                {
                    task.AutoOpen = false;
                    SaveSessionContext();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("SendTaskState", ex);
            }
        }

        private void SendTaskList()
        {
            try
            {
                var tasks = GetTasks()
                    .OrderByDescending(t => t.UpdatedAt)
                    .Select(t => new
                    {
                        taskId = t.TaskId,
                        title = t.Title ?? "",
                        summary = t.Summary ?? "",
                        kind = t.Kind ?? TaskKinds.Write,
                        stage = t.Stage ?? TaskStages.NeedsDetails,
                        requiresApply = t.RequiresApply,
                        wasApplied = t.WasApplied,
                        questionCount = t.Questions?.Count ?? 0,
                        updatedAt = t.UpdatedAt.ToString("o"),
                    })
                    .ToArray();

                _bridge?.PostMessage("task_list", tasks);
            }
            catch (Exception ex)
            {
                Logger.LogError("SendTaskList", ex);
            }
        }

        private static bool IsModelChangingCategory(string category) =>
            category == TaskCategories.ModelEdit || category == TaskCategories.Create ||
            category == TaskCategories.Delete || category == TaskCategories.Annotation;

        private object ToTaskPayload(TaskState task)
        {
            if (task == null)
                return null;

            bool hasError = DetectResultError(task);

            return new
            {
                taskId = task.TaskId,
                title = task.Title ?? "",
                summary = task.Summary ?? "",
                kind = task.Kind ?? TaskKinds.Write,
                stage = task.Stage ?? TaskStages.NeedsDetails,
                requiresApply = task.RequiresApply,
                wasApplied = task.WasApplied,
                autoOpen = task.AutoOpen,
                hasError,
                steps = (task.Steps ?? new List<string>()).ToArray(),
                questions = (task.Questions ?? new List<QuestionItem>()).Select(q => new
                {
                    id = q.Id,
                    text = q.Text,
                    selectionType = q.SelectionType ?? "single",
                    options = (q.Options ?? new List<string>()).ToArray(),
                    answer = q.Answer,
                    skipped = q.Skipped,
                }).ToArray(),
                resultSummary = task.ResultSummary ?? "",
                // "Run again": the task's own code can be re-previewed (e.g. after the user
                // selects elements) without regenerating.
                canRerun = !string.IsNullOrWhiteSpace(task.GeneratedCode),
                createdAt = task.CreatedAt.ToString("o"),
                updatedAt = task.UpdatedAt.ToString("o"),
                review = task.Review == null ? null : new
                {
                    safeCount = task.Review.SafeCount,
                    versionSpecificCount = task.Review.VersionSpecificCount,
                    deprecatedCount = task.Review.DeprecatedCount,
                    affectedElementCount = task.Review.AffectedElementCount,
                    previewSuccess = task.Review.PreviewSuccess,
                    previewError = task.Review.PreviewError,
                    executionSummary = task.Review.ExecutionSummary,
                    analyzerDiagnostics = task.Review.AnalyzerDiagnostics?.Select(d => new
                    {
                        id = d.Id,
                        message = d.Message,
                        severity = d.Severity,
                        line = d.Line
                    })
                }
            };
        }

        /// <summary>
        /// Returns true if the completed task ended with an execution failure.
        /// Uses the ExecutionSuccess flag set at runtime (not text-based regex) to
        /// avoid false positives when code output legitimately contains words like "error".
        /// </summary>
        private static bool DetectResultError(TaskState task)
        {
            if (task == null || task.Stage != TaskStages.Completed)
                return false;
            return !task.ExecutionSuccess;
        }

        private TaskState CreateTask(TaskPlanResponse plan, string userText)
        {
            return new TaskState
            {
                TaskId = Guid.NewGuid().ToString(),
                Title = string.IsNullOrWhiteSpace(plan.Title)
                    ? _sessionManager?.GenerateTitle(userText) ?? UiText("Untitled task", "제목 없는 작업")
                    : plan.Title,
                Summary = plan.Summary ?? "",
                Kind = string.Equals(plan.TaskKind, TaskKinds.Read, StringComparison.OrdinalIgnoreCase)
                    ? TaskKinds.Read
                    : TaskKinds.Write,
                RequiresApply = !string.Equals(plan.TaskKind, TaskKinds.Read, StringComparison.OrdinalIgnoreCase),
                Category = NormalizeTaskCategory(plan.TaskCategory),
                Stage = TaskStages.NeedsDetails,
                Steps = plan.Steps?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>(),
                Questions = plan.Questions?.Where(q => !string.IsNullOrWhiteSpace(q.Text)).ToList() ?? new List<QuestionItem>(),
                SourceUserMessage = userText,
                CollectedInputs = new List<string> { userText },
                // Stamp the target document at CREATION from the main-thread cache —
                // the old first-execution binding stamped whatever document happened
                // to be active at preview time (wrong doc in multi-document sessions).
                // BindTaskToExecutedDocument stays as a backfill when this is empty.
                TargetDocumentTitle = BibimApp.CurrentActiveDocTitle,
                TargetDocumentPath = BibimApp.CurrentActiveDocPath,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
        }

        private static bool ContainsAny(string text, params string[] terms)
        {
            if (string.IsNullOrWhiteSpace(text) || terms == null || terms.Length == 0)
                return false;

            return terms.Any(term =>
                !string.IsNullOrWhiteSpace(term) &&
                text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string BuildTaskSearchText(TaskState task)
        {
            if (task == null)
                return string.Empty;

            return string.Join("\n", new[]
            {
                task.Title,
                task.Summary,
                task.SourceUserMessage,
                string.Join(" ", task.CollectedInputs ?? new List<string>()),
                string.Join(" ", task.Steps ?? new List<string>()),
            }).ToLowerInvariant();
        }

        /// <summary>
        /// Build search text scoped to the CURRENT turn only.
        /// Used by guards (e.g. ApplyParameterCreationGuard) to avoid
        /// false positives from stale CollectedInputs accumulated in prior turns.
        /// </summary>
        private string BuildCurrentTurnSearchText(TaskState task)
        {
            if (task == null)
                return string.Empty;

            return string.Join("\n", new[]
            {
                task.Title,
                task.Summary,
                task.SourceUserMessage,
                string.Join(" ", task.Steps ?? new List<string>()),
            }).ToLowerInvariant();
        }

        private void AppendTaskUserInput(TaskState task, string userText)
        {
            if (task == null || string.IsNullOrWhiteSpace(userText))
                return;

            if (task.CollectedInputs == null)
                task.CollectedInputs = new List<string>();

            task.CollectedInputs.Add(userText.Trim());
            if (task.CollectedInputs.Count > 12)
                task.CollectedInputs = task.CollectedInputs.Skip(task.CollectedInputs.Count - 12).ToList();
        }

        private void ApplyPlanToTask(TaskState task, TaskPlanResponse plan, string userText)
        {
            if (task == null || plan == null)
                return;

            task.Title = string.IsNullOrWhiteSpace(plan.Title)
                ? task.Title
                : plan.Title;
            task.Summary = string.IsNullOrWhiteSpace(plan.Summary)
                ? task.Summary
                : plan.Summary;
            task.Kind = string.Equals(plan.TaskKind, TaskKinds.Read, StringComparison.OrdinalIgnoreCase)
                ? TaskKinds.Read
                : TaskKinds.Write;
            task.RequiresApply = task.Kind != TaskKinds.Read;
            task.Category = NormalizeTaskCategory(plan.TaskCategory) ?? task.Category;
            // A model-changing category can never be a READ task: READ tasks only dry-run
            // and complete, so a misclassified "create dimensions" ended as "Completed" with
            // no Apply button and nothing applied (field report 2026-09-22).
            if (task.Kind == TaskKinds.Read && IsModelChangingCategory(task.Category))
            {
                Logger.Log("TaskPlanner", $"kind=read contradicts category={task.Category} — forcing write");
                task.Kind = TaskKinds.Write;
                task.RequiresApply = true;
            }
            task.Steps = plan.Steps?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
            task.Questions = plan.Questions?.Where(q => !string.IsNullOrWhiteSpace(q.Text)).ToList() ?? new List<QuestionItem>();
            task.SourceUserMessage = userText;
            task.ResultSummary = null;
            task.Review = null;
            task.GeneratedCode = null;
            task.WasApplied = false;
        }

        private void ApplyCurrentContextSummaryDefaults(TaskState task)
        {
            if (task == null)
                return;

            task.Kind = TaskKinds.Read;
            task.RequiresApply = false;
            task.WasApplied = false;
            task.Questions = new List<QuestionItem>();
            task.Title = UiText("Current model and view analysis", "현재 모델 및 뷰 분석");
            task.Summary = UiText(
                "I will inspect the open Revit model and the active view, then summarize the available document, view, category, and selection information.",
                "열려 있는 Revit 모델과 활성 뷰를 확인한 뒤, 문서 정보와 뷰 정보, 주요 카테고리, 현재 선택 상태를 요약합니다.");
            task.Steps = new List<string>
            {
                UiText("Document information", "문서 정보"),
                UiText("Active view information", "활성 뷰 정보"),
                UiText("Levels, phases, and worksets", "레벨 / 페이즈 / 워크셋"),
                UiText("Major visible categories in the current view", "현재 뷰의 주요 카테고리"),
                UiText("Current selection state", "현재 선택 상태")
            };
        }

        private bool ApplyParameterCreationGuard(TaskState task)
        {
            if (task == null || task.Kind != TaskKinds.Write)
                return false;

            // Use current-turn-only text to avoid contamination from prior CollectedInputs
            string text = BuildCurrentTurnSearchText(task);

            // Also check CollectedInputs for answers already provided in THIS task
            string fullText = BuildTaskSearchText(task);

            bool isParameterCreation = ContainsAny(text, "파라미터", "parameter") &&
                ContainsAny(text, "추가", "생성", "만들", "추가해", "add", "create", "new");

            if (!isParameterCreation)
                return false;

            // Detect delegation expressions — user wants us to decide
            if (IsDelegationExpression(task.SourceUserMessage))
                return false;

            var questions = new List<QuestionItem>();
            bool hasType = ContainsAny(fullText,
                "yes/no", "yes no", "checkbox", "check box", "체크박스", "bool", "boolean", "예/아니오",
                "텍스트", "text", "숫자", "number", "길이", "length", "각도", "angle");
            bool hasReusePolicy = ContainsAny(fullText,
                "reuse", "overwrite", "existing", "덮어", "재사용", "기존 값", "기존값", "stop with warning");
            bool hasBindingScope = ContainsAny(fullText,
                "카테고리", "category", "categories", "바인딩", "binding",
                "selected categories", "선택한 요소 카테고리", "선택 요소 카테고리");
            bool hasGroup = ContainsAny(fullText,
                "identity data", "parameter group", "group", "id data", "other",
                "파라미터 그룹", "그룹", "매개변수 그룹", "데이터", "data");

            if (!hasBindingScope)
            {
                questions.Add(new QuestionItem
                {
                    Text = UiText(
                        "How should the new parameter be bound?",
                        "새 파라미터를 어떻게 바인딩할까요?"),
                    SelectionType = "single",
                    Options = new List<string>
                    {
                        UiText("Bind to categories of current selection", "현재 선택 요소들의 카테고리에 바인딩"),
                        UiText("Bind to specific categories I choose", "내가 지정한 특정 카테고리에 바인딩"),
                    }
                });
            }

            if (!hasGroup)
            {
                questions.Add(new QuestionItem
                {
                    Text = UiText(
                        "Which parameter group in the Properties palette?",
                        "속성 창에서 어느 파라미터 그룹에 보이게 할까요?"),
                    SelectionType = "single",
                    Options = new List<string>
                    {
                        UiText("Identity Data", "ID 데이터"),
                        UiText("Data", "데이터"),
                        UiText("Other", "기타"),
                    }
                });
            }

            if (!hasType)
            {
                questions.Add(new QuestionItem
                {
                    Text = UiText(
                        "What parameter type should I create?",
                        "어떤 파라미터 형식으로 만들까요?"),
                    SelectionType = "single",
                    Options = new List<string>
                    {
                        UiText("Text", "텍스트"),
                        UiText("Number", "숫자"),
                        UiText("Length", "길이"),
                        UiText("Yes/No", "예/아니오"),
                        UiText("Angle", "각도"),
                    }
                });
            }

            if (!hasReusePolicy)
            {
                questions.Add(new QuestionItem
                {
                    Text = UiText(
                        "If a parameter with the same name already exists?",
                        "같은 이름의 파라미터가 이미 있으면?"),
                    SelectionType = "single",
                    Options = new List<string>
                    {
                        UiText("Reuse it and set values on current selection", "재사용해서 현재 선택 요소에 값 설정"),
                        UiText("Stop with a warning", "경고만 하고 중단"),
                    }
                });
            }

            if (questions.Count == 0)
                return false;

            task.Title = UiText("Add parameter to selected elements", "선택 요소 파라미터 추가");
            task.Summary = UiText(
                "This task needs Revit-specific parameter binding details before code generation can proceed.",
                "이 작업은 코드 생성을 진행하기 전에 Revit 파라미터 바인딩 관련 세부 정보가 더 필요합니다.");
            task.Questions = questions;
            task.RequiresApply = true;
            return true;
        }

        /// <summary>
        /// Detect Korean/English delegation expressions where the user wants
        /// the AI to decide on its own (e.g. "알아서해", "마음대로", "default로").
        /// When detected, guards should skip clarification and use sensible defaults.
        /// </summary>
        private static bool IsDelegationExpression(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "알아서", "마음대로", "아무거나", "상관없어", "니가 결정",
                "네가 결정", "default로", "디폴트로", "기본값으로", "알아서해",
                "알아서 해", "알아서하라고", "알아서 하라고", "그냥 해",
                "just do it", "your choice", "you decide", "don't care",
                "whatever", "any is fine");
        }

        private string BuildTaskQuestionsMessage(TaskState task)
        {
            string intro = UiText(
                "I need a few details before I continue:",
                "계속 진행하려면 아래 정보를 먼저 확인해야 합니다:");
            string questions = string.Join(Environment.NewLine,
                (task.Questions ?? new List<QuestionItem>())
                    .Select((q, index) => $"{index + 1}. {q.Text}"));
            return string.IsNullOrWhiteSpace(questions) ? intro : $"{intro}{Environment.NewLine}{questions}";
        }

        private bool IsBroadReadReviewTask(TaskState task)
        {
            if (task == null || task.Kind != TaskKinds.Read)
                return false;

            if (IsBuiltInCurrentContextSummaryTaskV2(task))
                return true;

            string text = BuildTaskSearchText(task);
            bool broadWords = ContainsAny(text,
                "최대한 많이", "자세히", "상세", "전체", "전부", "모두",
                "as much as possible", "in detail", "detailed", "comprehensive", "everything", "all possible");
            bool readWords = ContainsAny(text,
                "설명", "요약", "분석", "알려", "보여", "정리",
                "describe", "summary", "summarize", "analyze", "report", "show");
            return broadWords && readWords;
        }

        private string BuildReadTaskReviewMessage(TaskState task)
        {
            if (task == null)
                return string.Empty;

            string intro = UiText(
                "I can analyze the current Revit context with the following scope:",
                "아래 범위로 현재 Revit 상태를 분석할게요:");
            string stepLines = string.Join(Environment.NewLine,
                (task.Steps ?? new List<string>()).Select(step => $"- {step}"));
            string outro = UiText(
                "If you want anything else included, tell me before I continue. Otherwise, press Review Task to start the analysis.",
                "추가로 포함할 항목이 있으면 지금 말씀해주세요. 그대로 진행하려면 작업 확인을 눌러 분석을 시작하면 됩니다.");

            return string.IsNullOrWhiteSpace(stepLines)
                ? $"{intro}{Environment.NewLine}{outro}"
                : $"{intro}{Environment.NewLine}{stepLines}{Environment.NewLine}{Environment.NewLine}{outro}";
        }

        private void SendAssistantMessage(string text, string contentType = "text",
            int inputTokens = 0, int outputTokens = 0, string csharpCode = null,
            string messageType = "normal", long elapsedMs = 0)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            string storedContentType = !string.IsNullOrWhiteSpace(csharpCode)
                ? "code"
                : contentType;
            lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = text, IsUser = false });
            _sessionManager?.AddMessage(_activeSessionId, "assistant", storedContentType,
                text, csharpCode, inputTokens, outputTokens);
            SendSessionList();

            PostStreamingEndMessage(text, messageType, csharpCode, inputTokens, outputTokens, elapsedMs);

            // Windows notification ONLY when the user actually needs to look:
            // a question card or an error. Firing on every assistant message
            // (including plain replies and acks) made a multi-turn task pop 4-6
            // "action required" balloons — pure noise that trained users to
            // ignore the one balloon that matters.
            if (messageType == "question" || messageType == "error")
            {
                try { WindowsNotificationService.NotifyActionRequired(); }
                catch (Exception ex) { Logger.Log("Notification", $"Skipped: {ex.Message}"); }
            }
        }

        private void PostStreamingEndMessage(string text, string messageType = "normal",
            string csharpCode = null, int inputTokens = 0, int outputTokens = 0, long elapsedMs = 0,
            bool isUser = false, string actionId = null, string taskId = null, bool canUndo = false,
            bool feedbackEnabled = false, string feedbackState = null, string createdAt = null)
        {
            _bridge?.PostMessage("streaming_end", new
            {
                text,
                type = messageType,
                csharpCode,
                isUser,
                inputTokens,
                outputTokens,
                elapsedMs,
                actionId,
                taskId,
                canUndo,
                feedbackEnabled,
                feedbackState,
                createdAt
            });
        }

        private void SendMessageActionState(LastAppliedAction action)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.ActionId))
                return;

            _bridge?.PostMessage("message_action_state", new
            {
                actionId = action.ActionId,
                canUndo = action.CanUndo,
                feedbackEnabled = true,
                feedbackState = action.FeedbackState
            });
        }

        private LastAppliedAction TrackAppliedAction(TaskState task)
        {
            if (_lastAppliedAction != null &&
                _messageActions.TryGetValue(_lastAppliedAction.ActionId, out var previousAction) &&
                previousAction.CanUndo)
            {
                previousAction.CanUndo = false;
                SendMessageActionState(previousAction);
            }

            var action = new LastAppliedAction
            {
                ActionId = Guid.NewGuid().ToString("N"),
                TaskId = task?.TaskId,
                CanUndo = true,
                FeedbackState = null,
                TargetDocumentTitle = task?.TargetDocumentTitle,
                TargetDocumentPath = task?.TargetDocumentPath,
                DocumentChangeSequence = DocumentChangeTracker.GetCurrentSequence(
                    task?.TargetDocumentTitle,
                    task?.TargetDocumentPath),
                LibrarySnippetId = _pendingLibrarySnippetId,
            };
            _pendingLibrarySnippetId = null;

            _messageActions[action.ActionId] = action;
            _lastAppliedAction = action;
            return action;
        }

        private void ResetMessageActions()
        {
            _lastAppliedAction = null;
            _messageActions.Clear();
        }

        private static void BindTaskToExecutedDocument(TaskState task, ExecutionResult result)
        {
            if (task == null || result == null)
                return;

            if (string.IsNullOrWhiteSpace(task.TargetDocumentTitle) &&
                !string.IsNullOrWhiteSpace(result.DocumentTitle))
            {
                task.TargetDocumentTitle = result.DocumentTitle;
            }

            if (string.IsNullOrWhiteSpace(task.TargetDocumentPath) &&
                !string.IsNullOrWhiteSpace(result.DocumentPath))
            {
                task.TargetDocumentPath = result.DocumentPath;
            }
        }

        /// <summary>
        /// Creates a debug snapshot of the current task state for artifact recording.
        /// </summary>
        private object CreateTaskDebugSnapshot(TaskState task)
        {
            if (task == null)
                return null;

            return new
            {
                task.TaskId,
                task.Title,
                task.Summary,
                task.Kind,
                task.Stage,
                task.RequiresApply,
                task.WasApplied,
                task.SourceUserMessage,
                task.CollectedInputs,
                task.Steps,
                task.Questions,
                task.CreatedAt,
                task.UpdatedAt
            };
        }

        private async Task<ExecutionResult> UndoLastApplyAsync(LastAppliedAction action)
        {
            var request = new ExecutionRequest
            {
                Kind = ExecutionRequestKind.UndoLastApply,
                ExpectedDocumentTitle = action?.TargetDocumentTitle,
                ExpectedDocumentPath = action?.TargetDocumentPath,
                Callback = new TaskCompletionSource<ExecutionResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };

            BibimApp.ExecutionHandler.Enqueue(request);
            BibimApp.ExecutionEvent.Raise();
            return await request.Callback.Task;
        }
    }
}
