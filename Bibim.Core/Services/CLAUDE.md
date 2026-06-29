# Services — LLM Orchestration Core

## Purpose
The agent loop: turn a user request into validated C# that Revit can run. Provider HTTP is delegated to `Providers/` (see its CLAUDE.md); generated-code execution happens on the main thread in `../BibimExecutionHandler.cs` (see root CLAUDE.md). This dir is everything in between.

## Entry points
- `LlmOrchestrationService.cs` — `SendMessageAsync` (streaming chat) and `GenerateWithToolsAsync` (the tool loop). Provider-agnostic — holds an `ILlmProvider`.
- `BibimToolService.cs` — tool JSON-schema definitions + the Revit/RAG/Roslyn tool executor.

## Tool loop (`GenerateWithToolsAsync`)
- `maxTurns = 10`. Messages stay Anthropic-shaped `JArray` throughout.
- **7 tools**, composed by `BibimToolService.GetToolDefinitions(contextHint)`: `search_revit_api` + `run_roslyn_check` are always present; the 5 Revit-context tools (`get_view_info`, `get_selected_elements`, `get_element_parameters`, `get_family_types`, `get_project_levels`) are emitted **only when `contextHint` keywords match** — saves tokens on tasks that don't need document state.
- **BIBIM-007 coercion** (`LlmOrchestrationService.cs:~316`): if a response carries `tool_use` blocks but `stop_reason != "tool_use"`, force the tool-use branch — guards a provider-truncation edge case. Loop ends on `end_turn`/`stop` with no pending tool blocks; `max_tokens` mid-loop triggers a continuation.
- `max_tokens` is **8192** — the earlier 4096 diet was reverted (comment at `LlmOrchestrationService.cs:~271`: a HARD limit billed only on tokens emitted, so the lower cap bought nothing).
- **Roslyn retry**: on compile failure, `PrunePriorCompileAttempts(messages)` (line 657) drops prior failed attempts before retrying (~700t/round saved); `BuildCompileErrorFeedback(includeRules)` emits the 5-line Rules block on the **first** failure only.

## Loading-state safety
`SendMessageAsync` and `GenerateWithToolsAsync` both end in `finally { OnStatusUpdate?.Invoke(null); }` (lines 156, 568). **Do not remove** — without it an LLM error (429/network) leaves the panel stuck on "Generating response…".

## Planner vs. codegen split
- `PlannerGate.ShouldSkipPlanner(userText, hasActiveTask)` (in `Prompts/CategoryQuestionTemplates.cs`) skips the ~2,500t planner LLM call for greetings/short non-actionable input. Conservative by design — misses default to running the planner.
- The planner (`PlanUserIntentAsync`, invoked from `../BibimDockablePanelProvider.cs`) runs in JSON mode and **retries once** on parse failure ("your previous response was not valid JSON") before falling back to direct chat.

## RAG (local, on by default)
- `LocalRevitRagService.FetchAsync()` builds a BM25 index over `RevitAPI.xml` (+ `RevitAPIUI.xml`, `RevitAPIIFC.xml`) on first call (~0.5 s), cached for the process. `BM25Engine.cs` is pure C# — no NuGet deps.
- Diet constants (`LocalRevitRagService.cs`): `TopK=3`, `MaxChunkDisplayChars=1200`, `MaxMembersPerChunk=30`; signature + summary only (class/member Remarks + param descriptions dropped).
- Debug logs `[INDEX_BUILD_DONE]` / `[HIT]` / `[MISS]` → `%APPDATA%\BIBIM\logs\bibim_debug.txt`.

## Roslyn — compile AND analyze
`run_roslyn_check` (executed via `RoslynCompilerService` + `RoslynAnalyzerService`) compiles in-memory **and** runs the BIBIM001–005 analyzers, then `ApplyAutoFixes`. It is not a redundant pre-compile — **do not strip or bypass it**.

## Other services
`TokenTracker` (per-session input/output/cache_read/cache_create + `SessionCacheHitRatio`) · `HistorySummariser` (collapses aged-out turns into a ~150t synthetic "[Earlier session context]" — pure C#, no LLM call) · `RevitContextProvider` (document state for the 5 context tools) · `ApiInspectorService` (deprecation/version diagnostics) · `CodeLibraryService` / `LocalSessionManager` (disk-backed JSON) · `CodegenDebugRecorder` · `ModelIdentifierProbe` · `LocalizationService` (EN/KR).

## Prompts/
- `CodeGenSystemPrompt.Build(rev, isCodeGen, isFileOutput)` — `isFileOutput` gates the ~700t file-safety block; `LooksLikeFileOutputTask(text)` is the heuristic the caller uses to set it. `BuildBasePrompt` carries the **SELECTION-PRIORITY** block: on EN/KR pointing language ("these doors" / "이 도어들") use `uidoc.Selection.GetElementIds()`, never a model-wide `FilteredElementCollector` (Claude obeyed implicitly; GPT needed it explicit).
- `CategoryQuestionTemplates.BuildPlannerChecklist()` — compact planner question library; also hosts the `PlannerGate` class.

## Depends on
`Providers/` (`ILlmProvider`), `Prompts/`, `../Common/` (ConfigService, Logger), `../Models/`.

## Do not
- Remove the `finally`/`OnStatusUpdate(null)` blocks, or re-send pruned compile attempts on retry.
- Bypass `run_roslyn_check` as "just a compile check" — the analyzers + auto-fix are the safety gate.
