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
    // Task planner: prompt, live model-state input, structured-output call, plan parsing.
    public partial class BibimDockablePanelProvider
    {
        private string BuildTaskPlannerPrompt()
        {
            string outputLanguage = AppLanguage.IsEnglish ? "English" : "Korean";
            string categoryChecklist = CategoryQuestionTemplates.BuildPlannerChecklist();
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
11. `taskCategory` names the PRIMARY action: query = read/count/list/inspect only; export = writes files (PDF/DWG/DXF/CSV/Excel/IFC/image) without changing the model; model_edit = change existing elements or parameter values; create = new elements/views/sheets/parameters; delete = remove elements; view_selection = change only the active view, selection, or visibility; annotation = tags/text/dimensions/revision clouds; other = anything else. If a request both changes the model AND exports, choose the model-changing category. For mode = ""chat"" use ""other"".

JSON schema:
{{
  ""mode"": ""chat"" | ""task"",
  ""taskKind"": ""read"" | ""write"",
  ""taskCategory"": ""query"" | ""export"" | ""model_edit"" | ""create"" | ""delete"" | ""view_selection"" | ""annotation"" | ""other"",
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
- Provide 2-5 clickable options ONLY when you can enumerate real or sensible values
  (from [MODEL CONTEXT], or universal choices like skip/overwrite/all).
  If you cannot honestly enumerate options (e.g. a free-form path or custom name),
  return an empty options array — the UI shows a free-text input instead.
- Use ""single"" when only one answer makes sense, ""multi"" when multiple can apply.
- Options should be short, clear labels (not full sentences).
- The user can also type a custom answer, so options don't need to cover every case.

IDENTIFIER RESOLUTION (CRITICAL):
A [MODEL CONTEXT] block above lists the ACTUAL level/sheet/phase/workset names in
this project. When the user refers to a floor, level, or sheet loosely
(e.g. ""2층"", ""지하"", ""1F"", ""the third floor"", ""A1 sheets""):
1. Match it against [MODEL CONTEXT] silently.
   - Exactly ONE plausible match -> use that real name. DO NOT ask. Include the
     mapping in the task summary in the user's UI language:
     Korean: 해석: '2층' → 'L2'   |   English: Interpreted: '2nd floor' → 'L2'
   - MULTIPLE plausible matches -> ask ONE question whose options are EXACTLY those
     real names copied verbatim from [MODEL CONTEXT] (plus ""모두""/""All"" if applicable).
   - ZERO matches -> ask, listing the real names from [MODEL CONTEXT] as options.
2. NEVER invent identifier options. If [MODEL CONTEXT] is absent, ask a free-text
   question (empty options array) instead of fabricating options.
3. LANGUAGE: all user-facing text (questions, summaries, mapping line) MUST be in
   the user's UI language. [MODEL CONTEXT] itself is always English (internal only).

EXPLICIT-INFO RULE (CRITICAL):
Before generating questions, extract everything the user already stated: values with
units (1200mm), target scope, processing order (""개수 먼저 알려주고 확인 후 적용""),
output format (Excel). NEVER re-ask anything already stated.
No ""just to confirm"" questions. Ask ONLY about genuinely missing or conflicting info.

INTENT-PRIORITY RULE:
DECISIVE: if the request sets/inputs/fills a VALUE into a parameter (named or not —
including ""Comments"", ""Mark"", any title), the category is parameter VALUE-EDIT.
Do NOT classify it as parameter-creation and do NOT emit binding-category / data-type /
parameter-group / instance-vs-type questions — no matter how the parameter is phrased.
Those questions belong ONLY to literally creating a new parameter definition.

The PRIMARY VERB determines the task category. The noun ""파라미터/parameter"" NEVER
by itself makes this a parameter-creation task.
- 추출/내보내/출력/export/excel/csv -> data EXPORT (read + file output)
- 알려줘/보여줘/list/count/몇 개 -> read/query
- 변경/수정/일괄/입력/기입/설정/채워/넣어/set/change/input/enter/fill (a value)
  -> edit an EXISTING parameter's VALUE. Entering a value INTO a parameter — even a
  named one like ""Comments"" — is ALWAYS a value edit, never creation.
- Create a NEW parameter ONLY when the user creates the PARAMETER ITSELF
  (파라미터를 만들/생성/정의, create/define a parameter). ""값을 입력/추가"" = edit, NOT create.

PARAMETER-EDIT RULE (fixes mis-asking binding/type/group):
For a value edit, do NOT ask binding-category / data-type / parameter-group / instance-
vs-type questions — those apply ONLY to creating a NEW parameter. If you are unsure
whether the named parameter exists, do NOT guess ""create"" and do NOT ask: let codegen
verify at runtime with LookupParameter and report honestly if it is absent.

MODEL-STATE RULE: the input may contain a [MODEL STATE] line (live selection
count/categories + active view). USE it instead of asking: if the user says
""these/selected/이거/선택한"" and a selection exists, do NOT ask which elements;
if selection is none but the request points at one, ask (or route to chat).

CLARIFY-VIA-TASK RULE (CRITICAL): if the request is ACTIONABLE but under-
specified (""문 좀 정리해줘"", ""벽 정리""), ALWAYS return mode=""task"" with
clarifying questions — NEVER mode=""chat"" with a prose questionnaire in
assistantMessage. Prose menus bypass the structured question card, so the
user gets an inconsistent flow and their picks can detach from the task.
mode=""chat"" is ONLY for greetings, capability/knowledge questions, and
troubleshooting explanations — never for clarifying an actionable request.

QUESTION-OPTIONS RULE: every question SHOULD offer 2-4 concrete options the
user can click (plus they can always type a custom value). For output folder /
file path questions ALWAYS offer: [""바탕화면"", ""내 문서"", ""다운로드""]. Never
emit an options-less question when sensible defaults exist.

PENDING-ANSWER RULE: when [ACTIVE TASK] shows unanswered questions (stage
needs_details) and the user's message plausibly answers any of them (e.g. the
question asked for a level and the message is ""3층""), classify as mode=""task"",
taskRelation=""update"" and fold the answer into the plan — NEVER reply as plain
chat, or the answer silently detaches from the task.

{CapabilityManifest.PlannerBlock}

{categoryChecklist}{AttachedDocumentRules.Planner}";
        }

        /// <summary>
        /// One-line live model state for the planner: active view + selection count
        /// with top categories. MUST run on the Revit main thread (caller invokes it
        /// inside the identifier-probe Dispatcher hop). Failure → empty (non-fatal).
        /// </summary>
        private static string BuildPlannerModelStateLine(RevitContextProvider provider)
        {
            try
            {
                var view = provider.GetCurrentView();
                var sel = provider.GetSelectedElements();

                string viewPart = !string.IsNullOrEmpty(view?.ViewName)
                    ? $"active view: {view.ViewName} ({view.ViewType})"
                    : "active view: (unknown)";

                string selPart;
                if (sel == null || sel.TotalCount == 0)
                {
                    selPart = "selection: none";
                }
                else
                {
                    string cats = sel.Elements != null && sel.Elements.Count > 0
                        ? string.Join(", ", sel.Elements
                            .GroupBy(e => e.Category ?? "?")
                            .OrderByDescending(g => g.Count())
                            .Take(3)
                            .Select(g => $"{g.Key}×{g.Count()}"))
                        : null;
                    selPart = $"selection: {sel.TotalCount} element(s)"
                        + (cats != null ? $" [{cats}]" : "");
                }

                return $"[MODEL STATE] {viewPart}; {selPart}";
            }
            catch (Exception ex)
            {
                Logger.Log("PlannerState", $"model-state line skipped: {ex.Message}");
                return string.Empty;
            }
        }

        private string BuildPlannerInput(string userText, string resolvedText, TaskState activeTask,
            AttachedDocument attachment = null)
        {
            string[] recentHistory;
            lock (_chatHistoryLock)
            {
                recentHistory = _chatHistory
                    .Skip(Math.Max(0, _chatHistory.Count - PlannerContextWindow))
                    .Select(m =>
                    {
                        // Earlier attached documents collapse to a one-line note — the
                        // active task's target list is supplied separately below.
                        string text = AttachedDocument.Redact(m.Text ?? string.Empty);
                        // Cap each turn so a single noisy turn (long code block, big
                        // tool output) doesn't blow the planner prompt.
                        if (text.Length > BibimConstants.PlannerHistoryTurnMaxChars)
                            text = text.Substring(0, BibimConstants.PlannerHistoryTurnMaxChars) + "...";
                        return $"{(m.IsUser ? "USER" : "ASSISTANT")}: {text}";
                    })
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

            // Probe actual project identifiers (levels, sheets, phases, worksets)
            // so the planner can resolve loose references ("2층", "A1 시트") without
            // asking. RevitContextProvider + FilteredElementCollector require the
            // Revit/WPF main thread — dispatch synchronously with a short timeout.
            // Returns empty string on failure (non-fatal; planner still works, just
            // without the identifier block).
            string identifierBlock = string.Empty;
            string modelStateBlock = string.Empty;
            try
            {
                EnsureContextProvider();
                if (_contextProvider != null && System.Windows.Application.Current != null)
                {
                    var provider = _contextProvider;
                    // One main-thread hop produces BOTH blocks — the planner used to
                    // decide shouldAutoRun / enumerate options completely blind to selection
                    // and active view unless the user typed an @tag, which is where
                    // the "weird questions" class of complaints came from.
                    var probe = System.Windows.Application.Current.Dispatcher
                        .Invoke(() => Tuple.Create(
                                    ModelIdentifierProbe.BuildContextBlock(provider),
                                    BuildPlannerModelStateLine(provider)),
                                System.Windows.Threading.DispatcherPriority.Background);
                    identifierBlock = probe.Item1;
                    modelStateBlock = probe.Item2;
                }
            }
            catch (Exception probeEx)
            {
                Logger.Log("IdentifierProbe", $"Skipped in planner: {probeEx.Message}");
            }

            // Attached document: the latest message carries the full document block (+ the
            // pre-extracted target list). A follow-up turn without a new attachment still sees
            // the ACTIVE task's document as a compact target list.
            string latestMessage = attachment != null ? attachment.ComposeForLlm(userText) : userText;
            var taskAttachment = attachment == null && activeTask != null && !IsTaskTerminal(activeTask)
                ? activeTask.Attachment : null;
            if (taskAttachment != null)
            {
                string list = taskAttachment.BuildTargetList(100);
                currentTask += $"\n\n[ACTIVE TASK ATTACHMENT] {taskAttachment.Name}" +
                    (list.Length > 0 ? "\n" + list : " (no ElementId table)");
            }

            return $@"[LATEST USER MESSAGE]
{latestMessage}

[RESOLVED REVIT CONTEXT]
{contextBlock}{(string.IsNullOrEmpty(modelStateBlock) ? "" : $"\n{modelStateBlock}")}

[ACTIVE TASK]
{currentTask}

[RECENT EXECUTION LOG]
{BuildExecutionLogContext()}

[RECENT CONVERSATION]
{string.Join(Environment.NewLine, recentHistory)}
{(string.IsNullOrEmpty(identifierBlock) ? "" : $"\n{identifierBlock}")}
[Output format: respond with JSON only — no markdown, no commentary.]";
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
            string userText, string resolvedText, CancellationToken ct, AttachedDocument attachment = null)
        {
            var planner = EnsurePlannerService();

            var planningHistory = new List<ChatMessage>
            {
                new ChatMessage
                {
                    Text = BuildPlannerInput(userText, resolvedText, GetActiveTask(), attachment),
                    IsUser = true
                }
            };

            // First attempt — schema-constrained output (Anthropic output_config.format /
            // OpenAI strict json_schema); self-hosted servers fall back to JSON mode.
            var response = await planner.SendMessageNonStreamingAsync(
                planningHistory, BuildTaskPlannerPrompt(),
                maxTokens: PlannerMaxTokens, ct: ct, options: BuildPlannerRequestOptions());
            if (!response.Success || string.IsNullOrWhiteSpace(response.Text))
                // NOTE: the prefix must remain a keyword LlmOrchestrationService.
                // IsContextLengthError matches — BuildPlannerFailureHalt re-derives
                // the context-length condition from this exception's message.
                throw new InvalidOperationException(
                    (response.IsContextLengthExceeded ? "context_length_exceeded: " : "") +
                    (response.ErrorMessage ?? "Task planner failed."));

            // Parse with one retry on failure — schema mode makes this rare, but
            // self-hosted models (JSON mode only) still return malformed JSON sometimes;
            // a stronger instruction usually recovers it on the second try.
            int plannerInTok = response.ProcessedInputTokens;
            int plannerOutTok = response.OutputTokens;

            TaskPlanResponse plan = TryParsePlan(response.Text, out string parseError);
            if (plan == null)
            {
                Logger.Log("TaskPlanner",
                    $"First parse failed ({parseError}); retrying with explicit JSON-only nudge");

                var retryHistory = new List<ChatMessage>(planningHistory)
                {
                    new ChatMessage { Text = response.Text, IsUser = false },
                    new ChatMessage
                    {
                        Text = "Your previous response was not valid JSON (parse error: " +
                               parseError + "). " +
                               "Return ONLY a single complete JSON object that matches the schema. " +
                               "Do NOT use markdown. Do NOT use code fences. " +
                               "Do NOT add any commentary before or after the JSON.",
                        IsUser = true
                    }
                };

                var retryResponse = await planner.SendMessageNonStreamingAsync(
                    retryHistory, BuildTaskPlannerPrompt(),
                    maxTokens: PlannerMaxTokens, ct: ct, options: BuildPlannerRequestOptions());
                plannerInTok += retryResponse.ProcessedInputTokens;
                plannerOutTok += retryResponse.OutputTokens;
                if (!retryResponse.Success || string.IsNullOrWhiteSpace(retryResponse.Text))
                    throw new InvalidOperationException(
                        (retryResponse.IsContextLengthExceeded ? "context_length_exceeded: " : "") + parseError);

                plan = TryParsePlan(retryResponse.Text, out string retryError);
                if (plan == null)
                {
                    Logger.Log("TaskPlanner", $"Retry parse also failed: {retryError}");
                    throw new InvalidOperationException(retryError);
                }
            }

            plan.PlannerInputTokens = plannerInTok;
            plan.PlannerOutputTokens = plannerOutTok;
            return plan;
        }

        // Thinking-by-default models spend part of this on (low-effort) reasoning.
        private const int PlannerMaxTokens = 4096;

        private static LlmRequestOptions BuildPlannerRequestOptions() => new LlmRequestOptions
        {
            Effort = ConfigService.GetRagConfig()?.EffortPlanner ?? "low",
            ResponseSchema = PlannerSchema.Build(),
            ResponseSchemaName = PlannerSchema.Name,
            JsonMode = true
        };

        private static string NormalizeTaskCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return null;
            string c = category.Trim().ToLowerInvariant();
            return Array.IndexOf(TaskCategories.All, c) >= 0 ? c : null;
        }

        /// <summary>
        /// Try to extract + deserialize a TaskPlanResponse from raw model output.
        /// Returns null with errorMessage set on failure so the caller can decide
        /// whether to retry. Also normalises the questions array (string-form
        /// fallback for older outputs).
        /// </summary>
        private TaskPlanResponse TryParsePlan(string rawText, out string errorMessage)
        {
            errorMessage = null;
            string json = ExtractJsonObject(rawText);
            if (string.IsNullOrWhiteSpace(json))
            {
                errorMessage = "Task planner returned non-JSON content.";
                return null;
            }

            TaskPlanResponse plan;
            try
            {
                plan = JsonHelper.Deserialize<TaskPlanResponse>(json);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return null;
            }

            if (plan == null || string.IsNullOrWhiteSpace(plan.Mode))
            {
                errorMessage = "Task planner returned an invalid payload.";
                return null;
            }

            // Backward compatibility: questions may come back as plain strings or
            // as QuestionItem objects with partial fields. Normalize manually so
            // downstream UI always gets a clean list.
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

            // Some models (self-hosted, JSON mode only) wrap JSON in markdown fences
            text = Regex.Replace(text, @"^\s*```(?:json)?\s*\n?", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"\n?\s*```\s*$", "", RegexOptions.Multiline);
            text = text.Trim();

            var match = Regex.Match(text, "\\{[\\s\\S]*\\}");
            return match.Success ? match.Value : null;
        }
    }
}
