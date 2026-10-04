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
    // Execution plumbing: task execution prompt, API inspection, dry-run/commit requests, built-in context summary task.
    public partial class BibimDockablePanelProvider
    {
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

            // The summary shortcut is READ-only. A write plan must never land here, whatever
            // words it contains: on 2026-09-22 "…난치수 … 치수선을 작성해줘" (verb "작성" was not
            // in the list below, plan steps said "분석") ran the canned model summary instead
            // of the task — "Completed", 0 changes, and a wall of text that hid the buttons.
            if (task.Kind == TaskKinds.Write || IsModelChangingCategory(task.Category) ||
                task.Category == TaskCategories.Export)
                return false;

            string text = BuildTaskSearchText(task);

            bool mentionsCurrentContext = ContainsAny(text,
                "현재 뷰", "활성 뷰", "열려있는 모델", "열려 있는 모델", "현재 모델",
                "open model", "current model", "active view", "current view");

            bool asksForDescription = ContainsAny(text,
                "설명", "요약", "분석", "알려", "최대한 많이",
                "describe", "summary", "summarize", "analyze", "tell me");

            // Negative gate — if the prompt also expresses a WRITE intent (create / place /
            // modify / model / export), this is NOT a pure read-only summary task. We saw
            // false positives in the field where a modeling request whose Q&A answers
            // mentioned "현재 뷰" + planner-paraphrased Steps containing "분석" got
            // misrouted into the model-summary branch, requiring the user to re-prompt
            // before any actual codegen ran. The right long-term fix is the Category
            // enum from the planner-revision branch; this gate is the safe shim until
            // that lands.
            bool mentionsWriteAction = ContainsAny(text,
                "만들", "생성", "작성", "배치", "추가", "모델링", "올려", "올리", "치수선",
                "수정", "변경", "삭제", "지워", "삽입", "그리",
                "export", "출력", "내보내", "내보내기", "이동", "옮겨",
                "복사", "복제", "회전", "rename", "rename",
                "create", "make", "place", "add", "modify", "edit", "build",
                "delete", "remove", "move", "copy", "rotate",
                // Graphic override / visibility verbs — these route to code-gen,
                // NOT the model-summary branch. "색상 강조" / "하이라이트" / "hide" etc.
                // NB: "표시" / "보여" intentionally excluded — they appear in READ
                //     requests ("현재 뷰 정보 표시해줘") and would block those.
                "강조", "하이라이트", "색상", "칠해", "칠하", "재지정", "격리", "숨기", "숨겨",
                "override", "highlight", "color", "colour", "paint", "graphic", "isolate", "hide");

            return mentionsCurrentContext && asksForDescription && !mentionsWriteAction;
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
                : "\n- 코드 주석과 사용자에게 표시되는 문자열은 모두 한국어로 작성하세요. 변수·메서드 이름 등 식별자는 영어로 작성하세요.";

            string outputRules = task.Kind == TaskKinds.Read
                ? "- READ task: if count_elements / list_elements answer this completely and exactly, reply in plain text with the exact results (no code block).\n" +
                  "- Otherwise return ONLY a ```csharp``` block containing statements for the body of Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx), using ctx.Log(\"message\") for intermediate progress, with no using directives, namespace, class, or method signature.\n" +
                  "- All clarifications were collected before this request. Do NOT ask questions."
                : "- Return ONLY a ```csharp``` block containing statements for the body of Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx).\n" +
                  "- Use ctx.Log(\"message\") to record intermediate progress (e.g. element counts, decisions, skipped items). These appear in the BIBIM panel after execution.\n" +
                  "- Do NOT include using directives, namespace, class, method signature, markdown outside the code block, or explanation text.\n" +
                  "- All clarifications were collected before this request. Do NOT ask questions or add text outside the code block.";

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
{outputRules}{commentLangRule}{BuildAttachmentSection(task)}";
        }

        /// <summary>
        /// Task prompt section for an attached document: the full document block, the
        /// pre-extracted target list and version-correct id resolution. Empty without one.
        /// </summary>
        private string BuildAttachmentSection(TaskState task)
        {
            var att = task?.Attachment;
            if (att == null) return string.Empty;

            int.TryParse(ConfigService.GetEffectiveRevitVersion(), out int revitYear);
            bool legacyIds = revitYear > 0 && revitYear < 2024;
            string idCtor = legacyIds
                ? "new ElementId((int)id)  // Revit 2022-2023: int constructor"
                : "new ElementId((long)id)  // Revit 2024+: long constructor";
            // Id-heavy code (logging skipped ids) is where IntegerValue slips in — removed in 2026.
            string idValue = legacyIds
                ? "Print ids with elementId.IntegerValue."
                : "ElementId → .Value (long); never ElementId.IntegerValue (removed in Revit 2026).";
            idValue += " WorksetId is NOT an ElementId: it only has .IntegerValue (int), e.g. " +
                       "elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM).Set(worksetId.IntegerValue).";

            return $@"

Attached document — the task's data source (follow ATTACHED DOCUMENT RULES):
{att.ComposeForLlm(task.SourceUserMessage)}

Target resolution:
- UniqueId given → doc.GetElement(uniqueId). Otherwise → doc.GetElement({idCtor}).
- {idValue}
- The target set is exactly the listed ids ({(att.Targets.Count > 0 ? att.Targets.Count + " rows pre-extracted above" : "read them from the document")}). Skip and report ids that do not resolve.
- Change nothing outside the list; the preview is checked against these ids.
- Do not read uidoc.Selection: ""이 문서"" / ""this document"" means the attached document.";
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

        private async Task<ExecutionResult> ExecuteCompiledCodeAsync(
            bool isDryRun, CodeGenerationResult source = null)
        {
            var codeGen = source ?? _lastCodeGenResult;
            return await ExecuteCompilationAsync(
                codeGen?.CompilationResult,
                isDryRun,
                GetActiveTask());
        }

        /// <summary>
        /// Selection captured at the start of the most recent dry-run. Fed back to the
        /// commit ExecutionRequest so selection-based code survives the preview rollback
        /// clearing the live selection (see BibimExecutionHandler.RestoreSelectionIfEmpty).
        /// Overwritten (even with an empty list) on every dry-run so a stale snapshot
        /// from a previous task can never leak into an unrelated commit.
        /// </summary>
        private IList<Autodesk.Revit.DB.ElementId> _lastPreviewSelection;

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
                SelectionSnapshot = isDryRun ? null : _lastPreviewSelection,
                Callback = new TaskCompletionSource<ExecutionResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };

            BibimApp.ExecutionHandler.Enqueue(request);
            BibimApp.ExecutionEvent.Raise();
            var result = await request.Callback.Task;

            if (isDryRun)
                _lastPreviewSelection = result?.SelectionBeforeRun;

            return result;
        }

        private async Task RunBuiltInCurrentContextSummaryTaskAsync(TaskState task)
        {
            _bridge.PostMessage("progress", new[] {
                new { label = UiText("Analyzing the current model and view...", "현재 모델과 뷰를 분석하는 중..."), status = "active" }
            });

            try
            {
                var compiler = GetCompiler();
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
                    : $"{UiText("Execution failed", "실행 실패")}: {execResult.ErrorMessage}";
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
    }
}
