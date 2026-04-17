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
    public class BibimDockablePanelProvider : IDockablePaneProvider, IDisposable
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
            return AppLanguage.IsEnglish ? english : korean;
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
                Stage = TaskStages.NeedsDetails,
                Steps = plan.Steps?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>(),
                Questions = plan.Questions?.Where(q => !string.IsNullOrWhiteSpace(q.Text)).ToList() ?? new List<QuestionItem>(),
                SourceUserMessage = userText,
                CollectedInputs = new List<string> { userText },
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

            // Windows notification when user action may be needed
            try { WindowsNotificationService.NotifyActionRequired(); }
            catch (Exception ex) { Logger.Log("Notification", $"Skipped: {ex.Message}"); }
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

        private string BuildTaskPlannerPrompt()
        {
            string outputLanguage = AppLanguage.IsEnglish ? "English" : "Korean";
            return $@"You are the BIBIM task planner for a Revit add-in.
Return JSON only. No markdown. No code fences. No explanations.

Decide whether the latest user message is:
- a direct chat/information request that should be answered immediately (`mode = ""chat""`)
- or an actionable request that should become a task (`mode = ""task""`)

Rules:
1. Greetings, thanks, capability questions, API explanation questions, and troubleshooting explanations are `chat`.
2. Any request to inspect, count, analyze, list, generate, modify, place, rename, delete, export, or execute something in Revit is `task`.
3. For actionable requests, never guess missing details. If anything important is missing, fill `questions` with concrete clarifying questions and set `shouldAutoRun = false`.
4. If there is an active task:
   - use `taskRelation = ""update""` ONLY when the user is clearly refining or answering questions about that SAME task
   - use `taskRelation = ""new""` when the message is a different task or a new request, even if the topic is related
   - use `taskRelation = ""ask""` when it is ambiguous. In that case, put a short user-facing clarification in `assistantMessage`.
   - IMPORTANT: If the user's message describes a completely different action (e.g. active task is ""add parameter"" but user says ""move family""), ALWAYS use `taskRelation = ""new""`.
5. `taskKind = ""read""` means read-only / query / analysis task (e.g. count, list, inspect). If the request is complete, set `shouldAutoRun = true`.
6. `taskKind = ""write""` means any change to the model or generated artifact. If the request is complete, set `shouldAutoRun = false`.
   IMPORTANT: Export operations (PDF, DWG, DXF, CSV, IFC, Excel, image, schedule, etc.) are ALWAYS `taskKind = ""write""` and `shouldAutoRun = false`, even though they do not modify the Revit model. They create file artifacts on disk and must go through the preview → confirm → execute flow.
7. Write all user-facing strings (`title`, `summary`, `questions`, `assistantMessage`) in {outputLanguage}.
8. Keep `steps` high-level, natural-language, and free of API syntax.
9. If the user asks about a recent execution failure (e.g. ""왜 실패했어?"", ""에러 원인"", ""why did it fail""), check the [RECENT EXECUTION LOG] section and answer as `mode = ""chat""` with the failure details in `assistantMessage`.
10. Delegation expressions like ""알아서해"", ""마음대로"", ""아무거나"", ""상관없어"", ""니가 결정해"", ""default로"" mean the user wants you to choose sensible defaults. Treat any pending clarification as answered and proceed.

JSON schema:
{{
  ""mode"": ""chat"" | ""task"",
  ""taskKind"": ""read"" | ""write"",
  ""taskRelation"": ""new"" | ""update"" | ""ask"",
  ""title"": ""short task title"",
  ""summary"": ""one paragraph summary"",
  ""steps"": [""step 1"", ""step 2""],
  ""questions"": [
    {{
      ""text"": ""question text"",
      ""selectionType"": ""single"" | ""multi"",
      ""options"": [""option 1"", ""option 2"", ""option 3""]
    }}
  ],
  ""assistantMessage"": ""direct answer for chat mode OR short clarification for taskRelation=ask"",
  ""shouldAutoRun"": true | false
}}

Question rules:
- Each question MUST have 2-5 concrete options the user can click.
- Use ""single"" when only one answer makes sense, ""multi"" when multiple can apply.
- Options should be short, clear labels (not full sentences).
- The user can also type a custom answer, so options don't need to cover every case.

CATEGORY-SPECIFIC REQUIRED QUESTIONS:
For the following task categories, you MUST ask these questions if the user has NOT already provided the information. Do NOT skip them unless the user explicitly delegates (""알아서해"", ""대충 해"", etc.).

── CROSS-CUTTING: ask for ANY operation that targets ""all"" / ""모든"" or a broad category ──
Scope:
- Target range: current view only / current level / entire model
- Include elements from linked models? (yes / no)
- Phase filter: active phase only / all phases (ask only when the user explicitly mentions demolition, renovation, or multiple phases)

── CROSS-CUTTING: ask for batch operations that may encounter existing elements ──
Conflict / Duplicate Handling:
- If the target element already has a tag / name / value: skip / overwrite / error and stop
- If one item fails in a bulk operation: continue with the rest / roll back all

── PLACEMENT ──
Place / Create Instance:
- Exact placement location (coordinates, level, host face, reference point)
- Family type to place (if not specified)
- Host type: level-based / wall-hosted / face-hosted (if ambiguous)
- Orientation / rotation angle (if applicable)

── MOVE / COPY / ARRAY ──
Move:
- Direction and distance (absolute coordinates vs relative offset)
- Confirm target elements (current selection / by category / by filter)

Copy / Array:
- Linear or radial array
- Specify by count+spacing, count+total length, or spacing only
- Group the copies, or keep as individual instances

Rotate:
- Rotation axis (X / Y / Z or custom axis)
- Rotation angle
- Center of rotation (element center / origin / specific point)

── PARAMETER ──
Parameter Add / Modify:
- Project parameter or Shared parameter
  → If Shared: shared parameter file path and group name
- Instance or Type parameter
- Parameter group (Identity Data, Data, Dimensions, Text, etc.)
- Binding categories (which element categories to attach to)
- If parameter already exists: reuse / overwrite / error

── VIEW & SHEET ──
View Creation (Floor Plan / RCP / Section / Elevation / 3D):
- Which level or reference (for plan views)
- View Template to apply (none / project default / specific template)
- View Range: cut plane height, top / bottom offset (ask only if the user mentions a specific cut height or the task involves mezzanine / split levels)
- View name or naming convention

Sheet Creation:
- Title block family and type (REQUIRED if multiple exist in the project)
- Sheet number and naming convention

Placing Views on Sheets:
- Which sheet (if not specified)
- Viewport position on the sheet (center / specific coordinates)
- View scale (if it differs from the current view scale)

── ANNOTATION / TAG ──
Tag / Annotation:
- Which tag family to use (REQUIRED if multiple families exist for the category)
- Leader line: yes or no
- Placement: auto-center on element / offset position
- Elements already tagged: skip / replace / add duplicate tag

── DELETE ──
Delete:
- Confirm deletion target (to prevent accidental deletion)
- How to handle dependent / hosted elements

── EXPORT ──
Export — PDF:
- Output folder path (REQUIRED — ask if not specified)
- Which sheets / views to export (if not specified)
- Individual file per sheet, or combined into one PDF
- Color or grayscale
- Paper size (if not obvious from the project)

Export — Excel / CSV (parameter schedule):
- Output folder path (REQUIRED — ask if not specified)
- Which categories to include (e.g. Walls, Doors, Windows, all)
- Which parameters to use as columns (if not specified)
- One sheet per category, or all categories in one sheet

Export — DWG / DXF / IFC:
- Output folder path (REQUIRED — ask if not specified)
- Which views / sheets to export (if not specified)
- Export settings / standard (if format-specific options matter)

── SCHEDULE ──
Schedule:
- Purpose: create as a schedule view in the project, or export to file (Excel / CSV)?
  → If exporting to file: also apply the Export — Excel / CSV questions below
- Sort and group fields (if not specified)
- Filter conditions (if any)

── RENAME / RENUMBER ──
Rename / Renumber:
- Scope: selected elements / current view / entire model
- Naming pattern (prefix, suffix, numbering format — e.g. A-##, 101, 101A)
- Conflict handling when the new name already exists: skip / overwrite / error

── WORKSET ──
Workset:
- Which workset to assign new elements to (ask only if workset names appear in [RESOLVED REVIT CONTEXT])

── DISTANCE / PROXIMITY MEASUREMENT ──
Any task that compares, filters, or measures distance between two element types (e.g. ""doors within X mm of walls"", ""columns closer than Y mm to beams""):
- Reference point/line on element A: bounding box face / origin point / specific geometry face / connector
- Reference point/line on element B: same options
  → If the user specifies a sub-geometry reference (e.g. ""the outer edge of the window frame""), ask which specific face or sub-component to use, since family geometry varies per project.
- Distance direction: closest point in any direction / perpendicular distance / horizontal only / vertical only

── UNITS & COORDINATES ──
Units / Coordinates:
- Numeric input unit: millimeters / feet / project units (ask only when the unit is ambiguous from context)
- Coordinate reference for export or shared-coordinate operations: Project Base Point or Survey Point

── GEOMETRY CREATION ──
Form Creation (Loft / Sweep / Blend):
- Solid or Void
- If profile count is large (> 20), confirm split strategy

Grid / Level / Reference Plane Creation:
- Grid spacing and count (X and Y directions separately for 2D grids)
- Origin / center point or reference position for the grid layout
- Grid naming convention (prefix, start number)
- Extent / length of grid lines
- Target level (for level-hosted grids)";
        }

        private string BuildPlannerInput(string userText, string resolvedText, TaskState activeTask)
        {
            string[] recentHistory;
            lock (_chatHistoryLock)
            {
                recentHistory = _chatHistory
                    .Skip(Math.Max(0, _chatHistory.Count - PlannerContextWindow))
                    .Select(m => $"{(m.IsUser ? "USER" : "ASSISTANT")}: {m.Text}")
                    .ToArray();
            }

            string currentTask = activeTask == null || IsTaskTerminal(activeTask)
                ? "None"
                : JsonHelper.Serialize(new
                {
                    activeTask.TaskId,
                    activeTask.Title,
                    activeTask.Summary,
                    activeTask.Kind,
                    activeTask.Stage,
                    activeTask.Steps,
                    activeTask.Questions,
                    // Omit CollectedInputs to prevent stale keywords from
                    // biasing the planner toward "update" on unrelated tasks.
                    activeTask.RequiresApply
                }, indented: true);

            string contextBlock = resolvedText != userText
                ? resolvedText
                : "(none)";

            return $@"[LATEST USER MESSAGE]
{userText}

[RESOLVED REVIT CONTEXT]
{contextBlock}

[ACTIVE TASK]
{currentTask}

[RECENT EXECUTION LOG]
{BuildExecutionLogContext()}

[RECENT CONVERSATION]
{string.Join(Environment.NewLine, recentHistory)}";
        }

        /// <summary>
        /// Build a text summary of recent execution results for inclusion in
        /// planner and codegen prompts. Enables the AI to answer questions like
        /// "왜 실패했어?" without asking the user to re-provide error messages.
        /// </summary>
        private string BuildExecutionLogContext()
        {
            if (_sessionContext?.ExecutionLog == null || _sessionContext.ExecutionLog.Count == 0)
                return "(no recent executions)";

            var sb = new System.Text.StringBuilder();
            foreach (var entry in _sessionContext.ExecutionLog)
            {
                string status = entry.Success ? "SUCCESS" : "FAILED";
                string mode = entry.IsDryRun ? "preview" : "commit";
                sb.AppendLine($"[{entry.Timestamp:HH:mm:ss}] {status} ({mode}) — {entry.TaskTitle ?? "unknown task"}");
                if (!entry.Success && !string.IsNullOrWhiteSpace(entry.ErrorMessage))
                    sb.AppendLine($"  Error: {entry.ErrorMessage}");
                else if (entry.Success && !string.IsNullOrWhiteSpace(entry.Output))
                {
                    // Truncate long output to keep prompt size reasonable
                    string output = entry.Output.Length > 200
                        ? entry.Output.Substring(0, 200) + "..."
                        : entry.Output;
                    sb.AppendLine($"  Output: {output}");
                }
            }
            return sb.ToString().TrimEnd();
        }

        private async Task<TaskPlanResponse> PlanUserIntentAsync(
            string userText, string resolvedText, CancellationToken ct)
        {
            var planner = EnsurePlannerService();

            var planningHistory = new List<ChatMessage>
            {
                new ChatMessage
                {
                    Text = BuildPlannerInput(userText, resolvedText, GetActiveTask()),
                    IsUser = true
                }
            };

            var response = await planner.SendMessageNonStreamingAsync(
                planningHistory, BuildTaskPlannerPrompt(), maxTokens: 1024, ct: ct);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
                throw new InvalidOperationException(response.ErrorMessage ?? "Task planner failed.");

            string json = ExtractJsonObject(response.Text);
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Task planner returned non-JSON content.");

            var plan = JsonHelper.Deserialize<TaskPlanResponse>(json);
            if (plan == null || string.IsNullOrWhiteSpace(plan.Mode))
                throw new InvalidOperationException("Task planner returned an invalid payload.");

            // Backward compatibility: if the LLM returned questions as plain strings
            // instead of structured QuestionItem objects, Newtonsoft may deserialize them
            // as QuestionItem with only partial fields. Also handle raw string arrays
            // by re-parsing the questions array manually.
            try
            {
                var jObj = Newtonsoft.Json.Linq.JObject.Parse(json);
                var questionsToken = jObj["questions"];
                if (questionsToken is Newtonsoft.Json.Linq.JArray questionsArray && questionsArray.Count > 0)
                {
                    var normalized = new List<QuestionItem>();
                    foreach (var item in questionsArray)
                    {
                        if (item.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        {
                            // Plain string question — wrap in QuestionItem with no options
                            normalized.Add(new QuestionItem
                            {
                                Text = item.ToString(),
                                SelectionType = "single",
                                Options = new List<string>()
                            });
                        }
                        else if (item.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                        {
                            var qi = item.ToObject<QuestionItem>();
                            if (qi != null && !string.IsNullOrWhiteSpace(qi.Text))
                            {
                                if (qi.Options == null) qi.Options = new List<string>();
                                if (string.IsNullOrWhiteSpace(qi.SelectionType)) qi.SelectionType = "single";
                                normalized.Add(qi);
                            }
                        }
                    }
                    plan.Questions = normalized;
                }
            }
            catch (Exception parseEx)
            {
                Logger.Log("TaskPlanner", $"Questions normalization fallback: {parseEx.Message}");
            }

            return plan;
        }

        private string ExtractJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var match = Regex.Match(text, "\\{[\\s\\S]*\\}");
            return match.Success ? match.Value : null;
        }

        private string ToCSharpLiteral(string value)
        {
            return Newtonsoft.Json.JsonConvert.ToString(value ?? string.Empty);
        }

        private bool IsBuiltInCurrentContextSummaryTask(TaskState task)
        {
            if (task == null)
                return false;

            string text = BuildTaskSearchText(task);

            bool mentionsCurrentContext =
                text.Contains("현재 뷰") ||
                text.Contains("활성 뷰") ||
                text.Contains("열려있는 모델") ||
                text.Contains("열려 있는 모델") ||
                text.Contains("현재 모델") ||
                text.Contains("open model") ||
                text.Contains("current model") ||
                text.Contains("active view") ||
                text.Contains("current view");

            bool asksForDescription =
                text.Contains("설명") ||
                text.Contains("요약") ||
                text.Contains("분석") ||
                text.Contains("알려") ||
                text.Contains("최대한 많이") ||
                text.Contains("describe") ||
                text.Contains("summary") ||
                text.Contains("analyze") ||
                text.Contains("tell me");

            return mentionsCurrentContext && asksForDescription;
        }

        private bool IsBuiltInCurrentContextSummaryTaskV2(TaskState task)
        {
            if (task == null)
                return false;

            string text = BuildTaskSearchText(task);

            bool mentionsCurrentContext = ContainsAny(text,
                "현재 뷰", "활성 뷰", "열려있는 모델", "열려 있는 모델", "현재 모델",
                "open model", "current model", "active view", "current view");

            bool asksForDescription = ContainsAny(text,
                "설명", "요약", "분석", "알려", "최대한 많이",
                "describe", "summary", "summarize", "analyze", "tell me");

            return mentionsCurrentContext && asksForDescription;
        }

        private string BuildCurrentContextSummaryCode()
        {
            string intro = ToCSharpLiteral(UiText(
                "I analyzed the currently open Revit model and active view.",
                "현재 열려 있는 Revit 모델과 활성 뷰를 분석했습니다."));
            string modelHeader = ToCSharpLiteral(UiText("Model Summary", "모델 요약"));
            string viewHeader = ToCSharpLiteral(UiText("Current View", "현재 뷰"));
            string selectionHeader = ToCSharpLiteral(UiText("Selection", "선택 상태"));
            string docName = ToCSharpLiteral(UiText("Document", "문서명"));
            string docPath = ToCSharpLiteral(UiText("Path", "파일 경로"));
            string familyDoc = ToCSharpLiteral(UiText("Family document", "패밀리 문서"));
            string workshared = ToCSharpLiteral(UiText("Workshared", "워크셋 사용"));
            string levels = ToCSharpLiteral(UiText("Levels", "레벨 수"));
            string phases = ToCSharpLiteral(UiText("Phases", "페이즈 수"));
            string worksets = ToCSharpLiteral(UiText("User worksets", "사용자 워크셋 수"));
            string totalElements = ToCSharpLiteral(UiText("Model elements", "비타입 요소 수"));
            string viewName = ToCSharpLiteral(UiText("Name", "이름"));
            string viewType = ToCSharpLiteral(UiText("Type", "유형"));
            string viewScale = ToCSharpLiteral(UiText("Scale", "축척"));
            string detailLevel = ToCSharpLiteral(UiText("Detail level", "상세 수준"));
            string viewTemplate = ToCSharpLiteral(UiText("View template", "뷰 템플릿"));
            string viewElements = ToCSharpLiteral(UiText("Visible non-type elements", "현재 뷰의 비타입 요소 수"));
            string visibleCategories = ToCSharpLiteral(UiText("Visible categories", "보이는 주요 카테고리"));
            string selectedCount = ToCSharpLiteral(UiText("Selected elements", "선택 요소 수"));
            string selectedCategories = ToCSharpLiteral(UiText("Selected categories", "선택 요소 카테고리"));
            string yesText = ToCSharpLiteral(UiText("Yes", "예"));
            string noText = ToCSharpLiteral(UiText("No", "아니오"));
            string noneText = ToCSharpLiteral(UiText("None", "없음"));

            return $@"using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BibimGenerated
{{
    public static class Program
    {{
        public static object Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx)
        {{
            var uidoc = uiApp?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
                return {ToCSharpLiteral(UiText("No active document.", "활성 문서가 없습니다."))};

            var sb = new StringBuilder();
            sb.AppendLine({intro});
            sb.AppendLine();
            sb.AppendLine(""["" + {modelHeader} + ""]"");
            sb.AppendLine($""- {{{docName}}}: {{doc.Title}}"");
            if (!string.IsNullOrWhiteSpace(doc.PathName))
                sb.AppendLine($""- {{{docPath}}}: {{doc.PathName}}"");
            sb.AppendLine($""- {{{familyDoc}}}: {{(doc.IsFamilyDocument ? {yesText} : {noText})}}"");
            sb.AppendLine($""- {{{workshared}}}: {{(doc.IsWorkshared ? {yesText} : {noText})}}"");
            sb.AppendLine($""- {{{levels}}}: {{new FilteredElementCollector(doc).OfClass(typeof(Level)).GetElementCount()}}"");
            sb.AppendLine($""- {{{phases}}}: {{doc.Phases.Size}}"");
            sb.AppendLine($""- {{{totalElements}}}: {{new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount()}}"");
            if (doc.IsWorkshared)
            {{
                var userWorksetCount = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset)
                    .ToWorksets()
                    .Count;
                sb.AppendLine($""- {{{worksets}}}: {{userWorksetCount}}"");
            }}

            var activeView = doc.ActiveView;
            if (activeView != null)
            {{
                sb.AppendLine();
                sb.AppendLine(""["" + {viewHeader} + ""]"");
                sb.AppendLine($""- {{{viewName}}}: {{activeView.Name}}"");
                sb.AppendLine($""- {{{viewType}}}: {{activeView.ViewType}}"");
                sb.AppendLine($""- {{{viewScale}}}: 1:{{activeView.Scale}}"");
                sb.AppendLine($""- {{{detailLevel}}}: {{activeView.DetailLevel}}"");

                if (activeView.ViewTemplateId != ElementId.InvalidElementId)
                {{
                    var template = doc.GetElement(activeView.ViewTemplateId);
                    if (template != null)
                        sb.AppendLine($""- {{{viewTemplate}}}: {{template.Name}}"");
                }}

                try
                {{
                    var visibleElementCount = new FilteredElementCollector(doc, activeView.Id)
                        .WhereElementIsNotElementType()
                        .GetElementCount();
                    sb.AppendLine($""- {{{viewElements}}}: {{visibleElementCount}}"");
                }}
                catch
                {{
                }}

                try
                {{
                    var visibleCats = doc.Settings.Categories
                        .Cast<Category>()
                        .Where(cat => cat != null && cat.get_Visible(activeView))
                        .Select(cat => cat.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Take(20)
                        .ToList();

                    sb.AppendLine($""- {{{visibleCategories}}}: {{(visibleCats.Count > 0 ? string.Join("", "", visibleCats) : {noneText})}}"");
                }}
                catch
                {{
                }}
            }}

            sb.AppendLine();
            sb.AppendLine(""["" + {selectionHeader} + ""]"");
            var selectedIds = uidoc.Selection.GetElementIds();
            sb.AppendLine($""- {{{selectedCount}}}: {{selectedIds.Count}}"");
            if (selectedIds.Count > 0)
            {{
                var selectedCats = selectedIds
                    .Select(id => doc.GetElement(id))
                    .Where(elem => elem != null && elem.Category != null)
                    .GroupBy(elem => elem.Category.Name)
                    .OrderByDescending(group => group.Count())
                    .Take(10)
                    .Select(group => $""{{group.Key}} ({{group.Count()}})"")
                    .ToList();

                sb.AppendLine($""- {{{selectedCategories}}}: {{(selectedCats.Count > 0 ? string.Join("", "", selectedCats) : {noneText})}}"");
            }}

            return sb.ToString().Trim();
        }}
    }}
}}";
        }

        private string BuildTaskExecutionPrompt(TaskState task)
        {
            string executionLog = BuildExecutionLogContext();
            string commentLangRule = AppLanguage.IsEnglish
                ? "\n- Write all C# code comments and user-visible string literals in English."
                : "\n- 코드 주석과 사용자에게 표시되는 문자열은 모두 한국어로 작성하세요.";

            return $@"Implement the following Revit task.

Task title:
{task.Title}

Task summary:
{task.Summary}

Task kind:
{task.Kind}

Required behavior:
{string.Join(Environment.NewLine, (task.Steps ?? new List<string>()).Select((step, index) => $"{index + 1}. {step}"))}

User-provided details:
{string.Join(Environment.NewLine, (task.CollectedInputs ?? new List<string>()).Select(input => $"- {input}"))}

Recent execution history (use this to avoid repeating the same errors):
{executionLog}

Constraints:
- Respect the current Revit version and available API surface.
- Do not guess requirements beyond the task summary.
- Prefer safe, minimal changes.
- If a previous execution failed, analyze the error and generate code that avoids the same failure.
- Return ONLY a ```csharp``` block containing statements for the body of Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx).
- Use ctx.Log(""message"") to record intermediate progress (e.g. element counts, decisions, skipped items). These appear in the BIBIM panel after execution.
- Do NOT include using directives, namespace, class, method signature, markdown outside the code block, or explanation text.{commentLangRule}";
        }

        private ApiInspectionReport InspectGeneratedCode(string sourceCode, ExecutionResult dryRunResult = null)
        {
            var validationConfig = ConfigService.GetRagConfig();
            bool validationEnabled = validationConfig?.ValidationGateEnabled ?? true;
            bool verifyStageEnabled = validationConfig?.VerifyStageEnabled ?? false;

            var inspector = new ApiInspectorService(
                validationEnabled || verifyStageEnabled ? _analyzerService : null,
                ConfigService.GetEffectiveRevitVersion());
            return inspector.Inspect(sourceCode, dryRunResult);
        }

        private TaskReviewSummary BuildTaskReviewSummary(ApiInspectionReport report, ExecutionResult dryRunResult = null)
        {
            return new TaskReviewSummary
            {
                SafeCount = report?.SafeCount ?? 0,
                VersionSpecificCount = report?.VersionSpecificCount ?? 0,
                DeprecatedCount = report?.DeprecatedCount ?? 0,
                AffectedElementCount = dryRunResult?.AffectedElementCount ?? report?.DryRunSummary?.AffectedElementCount ?? 0,
                PreviewSuccess = dryRunResult?.Success ?? report?.DryRunSummary?.Success ?? false,
                PreviewError = dryRunResult?.ErrorMessage ?? report?.DryRunSummary?.ErrorMessage,
                ExecutionSummary = dryRunResult?.Output,
                AnalyzerDiagnostics = report?.AnalyzerDiagnostics?.Select(d => new TaskDiagnosticSummary
                {
                    Id = d.Id,
                    Message = d.Message,
                    Severity = d.Severity.ToString().ToLowerInvariant(),
                    Line = d.Line
                }).ToList() ?? new List<TaskDiagnosticSummary>()
            };
        }

        private async Task<ExecutionResult> ExecuteCompiledCodeAsync(bool isDryRun)
        {
            return await ExecuteCompilationAsync(
                _lastCodeGenResult?.CompilationResult,
                isDryRun,
                GetActiveTask());
        }

        private async Task<ExecutionResult> ExecuteCompilationAsync(
            CompilationResult compilationResult,
            bool isDryRun,
            TaskState task = null)
        {
            if (compilationResult?.Assembly == null)
                throw new InvalidOperationException("No compiled assembly is available.");

            var request = new ExecutionRequest
            {
                CompiledAssembly = compilationResult.Assembly,
                EntryTypeName = "BibimGenerated.Program",
                EntryMethodName = "Execute",
                IsDryRun = isDryRun,
                ExpectedDocumentTitle = task?.TargetDocumentTitle,
                ExpectedDocumentPath = task?.TargetDocumentPath,
                Callback = new TaskCompletionSource<ExecutionResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };

            BibimApp.ExecutionHandler.Enqueue(request);
            BibimApp.ExecutionEvent.Raise();
            return await request.Callback.Task;
        }

        private async Task RunBuiltInCurrentContextSummaryTaskAsync(TaskState task)
        {
            _bridge.PostMessage("progress", new[] {
                new { label = UiText("Analyzing the current model and view...", "현재 모델과 뷰를 분석하는 중..."), status = "active" }
            });

            try
            {
                var compiler = new RoslynCompilerService();
                string code = BuildCurrentContextSummaryCode();
                var compileResult = compiler.Compile(code);

                if (!compileResult.Success)
                {
                    task.Stage = TaskStages.Review;
                    task.ResultSummary = $"Built-in analysis failed: {compileResult.ErrorSummary}";
                    task.GeneratedCode = code;
                    UpsertTask(task, autoOpen: true);
                    PostStreamingEndMessage(task.ResultSummary, "error");
                    return;
                }

                task.GeneratedCode = code;
                var execResult = await ExecuteCompilationAsync(compileResult, isDryRun: true, task);
                BindTaskToExecutedDocument(task, execResult);
                task.Review = new TaskReviewSummary
                {
                    AffectedElementCount = 0,
                    PreviewSuccess = execResult.Success,
                    PreviewError = execResult.Success ? null : execResult.ErrorMessage,
                    ExecutionSummary = execResult.Output
                };
                task.ExecutionSuccess = execResult.Success;
                task.Stage = execResult.Success ? TaskStages.Completed : TaskStages.Review;
                task.ResultSummary = execResult.Success
                    ? execResult.Output
                    : $"Execution failed: {execResult.ErrorMessage}";
                UpsertTask(task, autoOpen: true);

                SendAssistantMessage(task.ResultSummary, "text", messageType: execResult.Success ? "normal" : "error");
            }
            catch (Exception ex)
            {
                Logger.LogError("BuiltInReadTask", ex);
                task.Stage = TaskStages.Review;
                task.ResultSummary = $"Built-in analysis failed: {ex.Message}";
                UpsertTask(task, autoOpen: true);
                SendAssistantMessage(task.ResultSummary, "text", messageType: "error");
            }
            finally
            {
                _bridge.PostMessage("progress", new object[0]);
            }
        }

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
                        Logger.Log("BridgeHandler", $"user_message processing: {payloadJson}");

                        var payload = Newtonsoft.Json.Linq.JObject.Parse(payloadJson);
                        string userText = payload["text"]?.ToString() ?? "";

                        if (string.IsNullOrWhiteSpace(userText))
                        {
                            PostStreamingEndMessage(
                                UiText("Empty message.", "빈 메시지입니다."));
                            return;
                        }

                        if (string.IsNullOrEmpty(LlmOrchestrationService.ResolveApiKey()))
                        {
                            PostStreamingEndMessage(UiText(
                                "Anthropic API key is not configured. Please go to **Settings** (gear icon) and enter your API key.",
                                "Anthropic API 키가 설정되어 있지 않습니다. **설정** (톱니바퀴 아이콘)에서 API 키를 입력해 주세요."));
                            return;
                        }

                        await ChatWithLlmServiceAsync(userText);
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
                                text = $"Error: {ex.GetType().Name}: {ex.Message}",
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
                    _bridge.PostMessage("system_message",
                        UiText("Spec confirmed. Generating code...", "스펙이 확인되었습니다. 코드를 생성합니다..."));
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
            RegisterAsyncHandler("question_answers", payload =>
            {
                var answers = payload["answers"] as Newtonsoft.Json.Linq.JArray;
                Logger.Log("BridgeHandler", $"question_answers: {answers?.Count ?? 0} answers");

                var task = GetActiveTask();
                if (task == null || answers == null) return Task.CompletedTask;

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

                lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = qaText, IsUser = true });
                _sessionManager?.AddMessage(_activeSessionId, "user", "text", qaText);

                task.Questions = new List<QuestionItem>();
                task.Stage = TaskStages.Review;
                UpsertTask(task, autoOpen: true);
                SendSessionList();

                _bridge.PostMessage("streaming_end", new
                {
                    text = qaText,
                    type = "normal",
                    isUser = true,
                    inputTokens = 0,
                    outputTokens = 0,
                    elapsedMs = 0
                });

                return Task.CompletedTask;
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
                        _bridge.PostMessage("system_message",
                            UiText("Task confirmed. Analyzing the current model and view...",
                                "작업을 확인했습니다. 현재 모델과 뷰를 분석합니다..."));
                        await RunBuiltInCurrentContextSummaryTaskAsync(task);
                    }
                    else
                    {
                        _bridge.PostMessage("system_message",
                            UiText("Task confirmed. Generating code and running a preview...",
                                "작업을 확인했습니다. 코드를 생성하고 미리 검증합니다..."));
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


                if (_lastCodeGenResult?.CompilationResult?.Assembly == null)
                {
                    _bridge.PostMessage("system_message",
                        UiText("No compiled code available. Generate code first.", "실행할 컴파일 결과가 없습니다. 먼저 코드를 생성하세요."));
                    return;
                }

                _bridge.PostMessage("progress", new[] {
                    new { label = isDryRun
                        ? UiText("Running preview...", "미리보기를 실행하는 중...")
                        : UiText("Applying changes...", "변경 사항 적용 중..."), status = "active" }
                });

                var execResult = await ExecuteCompiledCodeAsync(isDryRun);
                var task = GetActiveTask();
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

                var report = !string.IsNullOrWhiteSpace(_lastCodeGenResult.GeneratedCode)
                    ? InspectGeneratedCode(_lastCodeGenResult.GeneratedCode, execResult)
                    : null;

                if (task != null && isDryRun)
                {
                    task.Stage = TaskStages.PreviewReady;
                    task.Review = BuildTaskReviewSummary(report, execResult);
                    task.ResultSummary = execResult.Success
                        ? UiText("Preview completed. Review the result before applying changes.",
                            "미리 검증이 완료되었습니다. 결과를 확인한 뒤 실제 적용하세요.")
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
                        ? execResult.Output
                        : $"Execution failed: {execResult.ErrorMessage}";
                    task.Review = BuildTaskReviewSummary(report, execResult);
                    UpsertTask(task, autoOpen: !execResult.Success);
                }

                if (!isDryRun || !execResult.Success)
                {
                    var action = (!isDryRun && execResult.Success)
                        ? TrackAppliedAction(task)
                        : null;

                    // Append ctx.Log() entries to output when present
                    string baseOutput = execResult.Success
                        ? execResult.Output
                        : $"Execution failed: {execResult.ErrorMessage}";
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

                    if (action != null)
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

                _bridge.PostMessage("system_message",
                    UiText("Regenerating with your feedback...", "피드백을 반영하여 다시 생성합니다..."));
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
                    RestoreLastCodeGenerationResult(restoredTask.ResultSummary ?? restoredTask.Title, restoredTask.GeneratedCode, 0, 0);


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
    

                    var compiler = new RoslynCompilerService();
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
                            : $"Preview failed: {execResult.ErrorMessage}",
                    };
                    BindTaskToExecutedDocument(task, execResult);
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
            RegisterSyncHandler("save_api_key", payload =>
            {
                string newKey = payload["apiKey"]?.ToString() ?? "";
                try
                {
                    ConfigService.SaveApiKey(newKey);
                    // Reset cached LLM services so next call picks up new key
                    _llmService = null;
                    _plannerLlmService = null;
                    Logger.Log("BridgeHandler", "API key saved successfully");
                    SendApiKeyStatus();
                    _bridge.PostMessage("api_key_save_result", new { success = true });
                }
                catch (Exception ex)
                {
                    Logger.Log("BridgeHandler", $"save_api_key failed: {ex.Message}");
                    _bridge.PostMessage("api_key_save_result", new { success = false, error = ex.Message });
                }
            });
            RegisterSyncHandler("save_gemini_api_key", payload =>
            {
                string newKey = payload["apiKey"]?.ToString() ?? "";
                try
                {
                    ConfigService.SaveGeminiApiKey(newKey);
                    Logger.Log("BridgeHandler", "Gemini API key saved successfully");
                    SendApiKeyStatus();
                    _bridge.PostMessage("gemini_key_save_result", new { success = true });
                }
                catch (Exception ex)
                {
                    Logger.Log("BridgeHandler", $"save_gemini_api_key failed: {ex.Message}");
                    _bridge.PostMessage("gemini_key_save_result", new { success = false, error = ex.Message });
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

                string downloadsFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                System.IO.Directory.CreateDirectory(downloadsFolder);

                string fileName = string.IsNullOrWhiteSpace(version)
                    ? "BIBIM_AI_Setup.exe"
                    : $"BIBIM_AI_v{version}_Setup.exe";
                string filePath = System.IO.Path.Combine(downloadsFolder, fileName);

                _bridge?.PostMessage("download_progress", new { status = "downloading" });

                using (var stream = await _downloadHttpClient.GetStreamAsync(url))
                using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Create))
                {
                    await stream.CopyToAsync(fs);
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
                string masked = ConfigService.GetMaskedApiKey();
                bool configured = !string.IsNullOrEmpty(masked);
                string claudeModel = ConfigService.GetRagConfig()?.ClaudeModel ?? "claude-sonnet-4-6";
                string geminiMasked = ConfigService.GetMaskedGeminiApiKey();
                bool geminiConfigured = !string.IsNullOrEmpty(geminiMasked);
                _bridge?.PostMessage("api_key_status", new
                {
                    configured,
                    maskedKey = masked,
                    claudeModel,
                    geminiConfigured,
                    geminiMaskedKey = geminiMasked,
                });
            }
            catch (Exception ex)
            {
                Logger.LogError("SendApiKeyStatus", ex);
                _bridge?.PostMessage("api_key_status", new
                {
                    configured = false,
                    maskedKey = "",
                    claudeModel = "claude-sonnet-4-6",
                    geminiConfigured = false,
                    geminiMaskedKey = "",
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
                    releaseNotes = update?.ReleaseNotes
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
                    releaseNotes = result?.ReleaseNotes
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
                var compiler = new RoslynCompilerService();
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

        /// <summary>
        /// Ensure LlmOrchestrationService is initialized with a valid API key.
        /// Lazy-init so we don't fail at panel creation time if config isn't ready.
        /// </summary>
        private LlmOrchestrationService EnsureLlmService()
        {
            if (_llmService != null) return _llmService;

            string apiKey = LlmOrchestrationService.ResolveApiKey();
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException(
                    "API key not found. Set it in rag_config.json or CLAUDE_API_KEY environment variable.");

            var compiler = new RoslynCompilerService();
            _llmService = new LlmOrchestrationService(apiKey, compiler);

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
                TokenTracker.Track("chat", "claude", info.Model,
                    info.InputTokens, info.OutputTokens, info.RequestId);
            };

            Logger.Log("BibimDockablePanel", "LlmOrchestrationService initialized");
            return _llmService;
        }

        /// <summary>
        /// Lightweight LLM instance for the Task Planner.
        /// No streaming delta events — only token usage is tracked.
        /// </summary>
        private LlmOrchestrationService EnsurePlannerService()
        {
            if (_plannerLlmService != null) return _plannerLlmService;

            string apiKey = LlmOrchestrationService.ResolveApiKey();
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException(
                    "API key not found. Set it in rag_config.json or CLAUDE_API_KEY environment variable.");

            _plannerLlmService = new LlmOrchestrationService(apiKey, new RoslynCompilerService());
            _plannerLlmService.OnTokenUsage += (info) =>
            {
                TokenTracker.Track("task_planner", "claude", info.Model,
                    info.InputTokens, info.OutputTokens, info.RequestId);
            };

            return _plannerLlmService;
        }

        /// <summary>
        /// Returns a windowed slice of _chatHistory for LLM calls.
        /// Keeps the most recent ChatHistoryMaxTurns messages to cap token cost.
        /// The full history is preserved in _chatHistory for session persistence.
        /// </summary>
        private List<ChatMessage> GetHistoryWindow()
        {
            lock (_chatHistoryLock)
            {
                if (_chatHistory.Count <= ChatHistoryMaxTurns)
                    return _chatHistory.ToList();
                return _chatHistory.Skip(_chatHistory.Count - ChatHistoryMaxTurns).ToList();
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
        private async Task ChatWithLlmServiceAsync(string userText)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Cancel any previous streaming
            var ct = ReplaceCts();

            try
            {
                EnsureActiveSession();
                string resolvedText = ResolveContextTags(userText);
                TaskPlanResponse plan = null;

                try
                {
                    plan = await PlanUserIntentAsync(userText, resolvedText, ct);
                }
                catch (Exception planEx)
                {
                    Logger.Log("TaskPlanner", $"Falling back to direct chat: {planEx.Message}");
                }

                lock (_chatHistoryLock) _chatHistory.Add(new ChatMessage { Text = resolvedText, IsUser = true });
                _sessionManager?.AddMessage(_activeSessionId, "user", "text", userText);
                SendSessionList();

                if (plan == null || string.Equals(plan.Mode, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    if (plan != null && !string.IsNullOrWhiteSpace(plan.AssistantMessage))
                    {
                        SendAssistantMessage(plan.AssistantMessage);
                    }
                    else
                    {
                        await SendDirectChatResponseAsync(userText, resolvedText, sw, ct);
                    }

                    return;
                }

                await HandlePlannedTaskAsync(plan, userText, ct);
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
                    text = $"Error: {ex.GetType().Name}: {ex.Message}",
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
            var llm = EnsureLlmService();
            string systemPrompt = await BuildSystemPromptWithRagAsync(resolvedText, false, ct);
            var response = await llm.SendMessageAsync(GetHistoryWindow(), systemPrompt, ct, maxTokens: 4096);

            if (response.Success)
            {
                SendAssistantMessage(response.Text, "text",
                    response.InputTokens, response.OutputTokens);
                return;
            }

            string chatErrMsg = response.IsContextLengthExceeded
                ? UiText("The conversation is too long. Please start a new session and try again.",
                         "대화가 너무 길어졌습니다. 새 세션을 시작한 뒤 다시 시도해 주세요.")
                : $"Error: {response.ErrorMessage}";

            // Use SendAssistantMessage so the error is saved to _chatHistory and _sessionManager
            SendAssistantMessage(chatErrMsg, "text", messageType: "error", elapsedMs: sw.ElapsedMilliseconds);
        }

        private async Task HandlePlannedTaskAsync(TaskPlanResponse plan, string userText, CancellationToken ct)
        {
            if (string.Equals(plan.TaskRelation, "ask", StringComparison.OrdinalIgnoreCase))
            {
                string askText = string.IsNullOrWhiteSpace(plan.AssistantMessage)
                    ? UiText("Should I update the current task or start a new one?",
                        "현재 작업에 반영할까요, 새 작업으로 시작할까요?")
                    : plan.AssistantMessage;
                SendAssistantMessage(askText, "question", messageType: "question");
                return;
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

            if (IsBuiltInCurrentContextSummaryTaskV2(task))
            {
                ApplyCurrentContextSummaryDefaults(task);
            }

            ApplyParameterCreationGuard(task);

            if (task.Questions != null && task.Questions.Count > 0)
            {
                task.Stage = TaskStages.NeedsDetails;
                UpsertTask(task, autoOpen: true);
                string questionText = BuildTaskQuestionsMessage(task);
                SendAssistantMessage(questionText, "question", messageType: "question");
                return;
            }

            if (IsBroadReadReviewTask(task))
            {
                task.Stage = TaskStages.Review;
                UpsertTask(task, autoOpen: true);
                string reviewText = BuildReadTaskReviewMessage(task);
                SendAssistantMessage(reviewText, "question", messageType: "question");
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
            // RAG context injection disabled — search_revit_api tool removed from OSS release.
            // GeminiRagService retained for future re-enablement.
        }

        /// <summary>
        /// Build system prompt for plain chat (non-code-gen).
        /// RAG injection disabled in OSS release — see GeminiRagService for future re-enablement.
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

        /// <summary>
        /// Create a BibimToolService wired to the current Revit context and Roslyn services.
        /// The mainThreadInvoker dispatches RevitContext calls to the WPF/Revit main thread.
        /// </summary>
        private BibimToolService CreateToolService()
        {
            EnsureContextProvider();
            return new BibimToolService(
                _contextProvider,
                new RoslynCompilerService(),
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
                });
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

        private async Task GenerateCodeFromTaskAsync(TaskState task, bool runAfterGeneration, string additionalContext = null)
        {
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
                    taskPrompt += $"\n\n[Additional context]\n{additionalContext}";
                string revitVersion = ConfigService.GetEffectiveRevitVersion();
                string systemPrompt = CodeGenSystemPrompt.Build(revitVersion, true)
                    + CodeGenSystemPrompt.AppendToolUseInstructions();
                debugDirectory = CodegenDebugRecorder.CreateRunDirectory(task.TaskId, Guid.NewGuid().ToString("N").Substring(0, 8), "task_codegen");
                CodegenDebugRecorder.WriteJson(debugDirectory, "task_snapshot.json", CreateTaskDebugSnapshot(task));
                CodegenDebugRecorder.WriteText(debugDirectory, "task_prompt.txt", taskPrompt);
                CodegenDebugRecorder.WriteText(debugDirectory, "system_prompt.txt", systemPrompt);

                _analyzerService.SetRevitVersion(revitVersion);

                var generationHistory = new List<ChatMessage>(GetHistoryWindow())
                {
                    new ChatMessage { Text = taskPrompt, IsUser = true }
                };

                var agentResult = await llm.GenerateWithToolsAsync(
                    generationHistory, systemPrompt,
                    BibimToolService.GetToolDefinitions(),
                    CreateToolService().ExecuteAsync,
                    maxTurns: 15, debugDirectory, ct);

                _lastCodeGenResult = agentResult;

                if (!agentResult.Success)
                {
                    task.Stage = TaskStages.Review;
                    string failMsg = agentResult.IsContextLengthExceeded
                        ? UiText("The conversation is too long. Please start a new session and try again.",
                                 "대화가 너무 길어졌습니다. 새 세션을 시작한 뒤 다시 시도해 주세요.")
                        : agentResult.ErrorMessage ?? "Code generation failed.";
                    task.ResultSummary = failMsg;
                    UpsertTask(task, autoOpen: true);
                    SendAssistantMessage(failMsg, "text",
                        agentResult.TotalInputTokens, agentResult.TotalOutputTokens,
                        messageType: "error");
                    return;
                }

                if (!agentResult.IsCodeResponse)
                {
                    task.Stage = TaskStages.Completed;
                    task.ResultSummary = agentResult.RawResponse;
                    UpsertTask(task, autoOpen: false);
                    SendAssistantMessage(agentResult.RawResponse, "text",
                        agentResult.TotalInputTokens, agentResult.TotalOutputTokens);
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
                    var execResult = await ExecuteCompiledCodeAsync(isDryRun: true);
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
                        : $"Execution failed: {execResult.ErrorMessage}";
                    UpsertTask(task, autoOpen: true);
                    SendAssistantMessage(task.ResultSummary, "text",
                        _lastCodeGenResult?.TotalInputTokens ?? 0,
                        _lastCodeGenResult?.TotalOutputTokens ?? 0,
                        codeToCompile,
                        messageType: execResult.Success ? "normal" : "error");
                    return;
                }

                if (runAfterGeneration)
                {
                    var previewResult = await ExecuteCompiledCodeAsync(isDryRun: true);
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
                    task.ResultSummary = previewResult.Success
                        ? UiText("Preview completed. Review the result before applying changes.",
                            "미리 검증이 완료되었습니다. 결과를 확인한 뒤 실제 적용하세요.")
                        : $"Preview failed: {previewResult.ErrorMessage}";
                    UpsertTask(task, autoOpen: true);

                    if (!previewResult.Success)
                    {
                        SendAssistantMessage(task.ResultSummary, "text",
                            _lastCodeGenResult?.TotalInputTokens ?? 0,
                            _lastCodeGenResult?.TotalOutputTokens ?? 0,
                            codeToCompile,
                            messageType: "error",
                            elapsedMs: sw.ElapsedMilliseconds);
                    }
                    else
                    {
                        SendAssistantMessage(task.ResultSummary, "text",
                            _lastCodeGenResult?.TotalInputTokens ?? 0,
                            _lastCodeGenResult?.TotalOutputTokens ?? 0,
                            codeToCompile,
                            messageType: "system",
                            elapsedMs: sw.ElapsedMilliseconds);
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
                string errorText = $"Error: {ex.Message}";
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
            var ct = ReplaceCts();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Interlocked.Increment(ref _suppressStreamingDeltaCount);

            try
            {
                var llm = EnsureLlmService();

                string revitVersion2 = ConfigService.GetEffectiveRevitVersion();
                string systemPrompt = CodeGenSystemPrompt.Build(revitVersion2, true)
                    + CodeGenSystemPrompt.AppendToolUseInstructions();

                _analyzerService.SetRevitVersion(revitVersion2);

                // Use windowed history to cap token cost across the multi-turn tool loop
                var specHistory = new List<ChatMessage>(GetHistoryWindow());

                string specDebugDir = CodegenDebugRecorder.CreateRunDirectory(
                    "spec", Guid.NewGuid().ToString("N").Substring(0, 8), "spec_codegen");
                CodegenDebugRecorder.WriteText(specDebugDir, "system_prompt.txt", systemPrompt);

                var agentResult = await llm.GenerateWithToolsAsync(
                    specHistory, systemPrompt,
                    BibimToolService.GetToolDefinitions(),
                    CreateToolService().ExecuteAsync,
                    maxTurns: 15, specDebugDir, ct);

                _lastCodeGenResult = agentResult;

                if (!agentResult.Success)
                {
                    string specFailMsg = agentResult.IsContextLengthExceeded
                        ? UiText("The conversation is too long. Please start a new session and try again.",
                                 "대화가 너무 길어졌습니다. 새 세션을 시작한 뒤 다시 시도해 주세요.")
                        : agentResult.ErrorMessage ?? "Code generation failed.";
                    _bridge.PostMessage("streaming_end", new
                    {
                        text = specFailMsg,
                        type = "error",
                        inputTokens = agentResult.TotalInputTokens,
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
                        inputTokens = agentResult.TotalInputTokens,
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
                    inputTokens: agentResult.TotalInputTokens,
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
                    text = $"Error: {ex.Message}",
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
