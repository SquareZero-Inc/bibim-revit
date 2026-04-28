# BIBIM_REVIT — Claude Working Notes

## Project
Claude-powered Revit C# add-in (BYOK). Multi-target:
- `net48` — Revit 2022–2024
- `net8.0-windows` — Revit 2025–2026
- `net10.0-windows` — Revit 2027+

## Build
- Diagnose compile errors: `dotnet build "Bibim.Core\Bibim.Core.csproj" -c R2026 -p:TargetFramework=net8.0-windows`
- `.\build.ps1` auto-elevates admin — errors close window before `pause`; always diagnose via dotnet directly
- Build configs: `R2022`–`R2027`; full release: `.\build.ps1 -SkipFrontend -SkipTests`
- R2027 requires .NET 10 SDK — build.ps1 skips gracefully if not installed

## C# Gotchas
- `volatile` not valid on `double`/`long` — use `Volatile.Read(ref field)` / `Volatile.Write(ref field, value)`
- `BibimDockablePanelProvider.cs` is 4000+ lines — use Grep, don't read whole file
- `RunRoslynCheck` in `BibimToolService.cs` runs BIBIM001-005 analyzers + `ApplyAutoFixes` — NOT just a compile check. Don't remove/bypass it as "redundant."
- `HttpClient`: never create per-request with `using` — use shared static field. `_downloadHttpClient` in `BibimDockablePanelProvider`; `_httpClient` (static) in `LlmOrchestrationService`.
- `RegisterAsyncHandler` lambdas are `Func<JObject, Task>` — removing `async` requires explicit `return Task.CompletedTask;` at every exit point.

## Key Files
- `BibimDockablePanelProvider.cs` — all JS↔C# bridge handlers, LLM dispatch
- `Common/ConfigService.cs` — rag_config.json loader, per-provider key save, migration, `GetActiveCredentials()`, `AvailableModels`
- `Services/LlmOrchestrationService.cs` — provider-agnostic tool loop + Roslyn retry; delegates HTTP to `ILlmProvider`
- `Services/Providers/` — `ILlmProvider` + `AnthropicProvider` / `OpenAIProvider` / `GeminiProvider` + `LlmProviderFactory`
- `Services/BibimToolService.cs` — LLM tool definitions + Revit code execution
- `Services/TokenTracker.cs` — local session token accumulators (input/output/cache_read/cache_create + `SessionCacheHitRatio`)
- `Services/LocalRevitRagService.cs` — local BM25 RAG over RevitAPI.xml
- `Services/BM25Engine.cs` — pure C# BM25, no NuGet deps
- `Services/HistorySummariser.cs` — collapses dropped sliding-window turns into a synthetic "[Earlier session context]" message
- `Services/Prompts/CodeGenSystemPrompt.cs` — code-gen system prompt builder. `Build(rev, isCodeGen, isFileOutput)` — `isFileOutput` gates the ~700t file-safety block. Has `LooksLikeFileOutputTask(text)` heuristic helper.
- `Services/Prompts/CategoryQuestionTemplates.cs` — `BuildPlannerChecklist()` for compact planner question library + `PlannerGate.ShouldSkipPlanner()` heuristic gate
- `build.ps1` — full build pipeline (frontend → C# → tests → Inno Setup → codesign)
- `TOKEN_OPT_BACKLOG.md` — verified follow-up tickets (BIBIM-102 / 103 / 105 / 203 / 205 / 206 / etc.)

## Multi-Provider Architecture (v1.1+)
- Canonical message format inside the orchestrator is **Anthropic-shaped JArray** (`{role, content[]}` with `tool_use` / `tool_result` blocks).
- Each provider adapter converts to/from native shape:
  - OpenAI: Responses API; tool_use → function_call; arguments come back as stringified JSON, parse via JObject.
  - Gemini: `generateContent`; tool_use → functionCall; synthetic call_id (Gemini lacks one).
- Provider routing is by model-id prefix (`claude-*` / `gpt-*` / `gemini-*`) — no separate `selected_provider` field.
- Add a new model: extend `LlmProviderFactory.ResolveProviderForModel`, `ConfigService.AvailableModels`, and the `MODELS` array in `frontend/src/components/SettingsPanel.tsx`.

## BYOK / API Keys
- Per-provider keys in `Config/rag_config.json` under `api_keys.{anthropic|openai|gemini}_api_key`. Legacy `claude_api_key` is read as a fallback for Anthropic and mirrored on save (auto-migration with `.bak` backup).
- Env var overrides: `ANTHROPIC_API_KEY` (or legacy `CLAUDE_API_KEY`), `OPENAI_API_KEY`, `GEMINI_API_KEY`.
- UI: Settings panel (⚙) — three provider sections + a gated model selector. Bridge handlers `save_api_key` (anthropic), `save_openai_api_key`, `save_gemini_api_key` all call `ConfigService.SaveApiKeyForProvider`.
- `BibimDockablePanelProvider.EnsureLlmService()` resolves the active provider via `ConfigService.GetActiveCredentials()` and constructs through `LlmProviderFactory.Create()`. **Reset `_llmService` and `_plannerLlmService` to null on any key/model save** so the next call picks up the new credentials.
- No key for the active model → bridge sends a friendly guidance message before calling the LLM.

## RAG (local, on by default)
- `LocalRevitRagService.FetchAsync()` indexes `RevitAPI.xml` (+ `RevitAPIUI.xml`, `RevitAPIIFC.xml`) on first call (~0.5 s), caches for the process lifetime.
- Available to all 4 models via the `search_revit_api` tool (definition in `BibimToolService.GetToolDefinitions`).
- Diet (v1.0.2): `TopK=3`, `MaxChunkDisplayChars=1200`, `MaxMembersPerChunk=30`. ClassRemarks / member Remarks / ParamDescriptions dropped — signature + summary only.
- Debug logs: `[INDEX_BUILD_DONE]`, `[HIT]`, `[MISS]` in `%USERPROFILE%\bibim_v3_debug.txt`.

## Token Optimization (v1.0.2)
- **Anthropic prompt caching**: `cache_control: ephemeral` on system prompt + last tool definition (via `MarkLastToolForCaching` in `AnthropicProvider`). 5-min TTL.
- **Cache telemetry**: `cache_read_input_tokens` / `cache_creation_input_tokens` parsed from all 3 providers' usage objects. `LlmResponse` / `CodeGenerationResult` / `TokenUsageInfo` carry both fields. `TokenTracker.Track()` accepts them and emits `hit_ratio` in log lines.
- **Roslyn retry prune**: `LlmOrchestrationService.PrunePriorCompileAttempts()` removes prior failed attempts from `messages` before each retry — avoids re-sending ~700t per round. `BuildCompileErrorFeedback(includeRules)` only emits the 5-line Rules block on the first failure.
- **Planner gate**: `PlannerGate.ShouldSkipPlanner(userText, hasActiveTask)` skips the ~2,500t planner LLM call for greetings/short non-actionable messages. Gate is conservative — misses default to running the planner.
- **History summariser**: `HistorySummariser.Summarise(dropped, sessionContext)` compacts aged-out sliding-window turns into ~150t synthetic message. No LLM call — pure C# from task titles + clipped first/last user message.
- **Conditional FileOutputRules**: `CodeGenSystemPrompt.Build(rev, isCodeGen, isFileOutput)` — only emit the ~700t file-safety block when `LooksLikeFileOutputTask(text)` matches. Caller passes a hint built from task title + summary + source message.
- **Conditional context tools**: `BibimToolService.GetToolDefinitions(contextHint)` — `search_revit_api` + `run_roslyn_check` always; the 5 Revit-context tools (view/selection/parameters/family/levels) only when hint keywords match.
- **Tool loop max_tokens 4096** (was 8192) — sufficient for any single C# block. Continuation handler covers rare truncation.
- **Sliding window 10 turns** (was 20). Anything older → `HistorySummariser`.

## LLM Reliability (v1.0.2)
- **Gemini JSON mode for planner**: `GeminiProvider` accepts `bool jsonMode` and emits `responseMimeType: "application/json"` when true. Used by `PlanUserIntentAsync` to prevent malformed/truncated JSON.
- **Planner parse-failure retry**: `PlanUserIntentAsync` retries once with explicit "your previous response was not valid JSON" instruction if `TryParsePlan` returns null. Both must fail before fallback to direct chat.
- **OpenAI JSON mode**: same `bool jsonMode` plumbing; sets `text.format.type = "json_object"` on Responses API requests.
- **Anthropic**: `jsonMode` accepted but unused — Claude follows JSON-only prompt instructions reliably.
- **GPT selection-priority rule**: `CodeGenSystemPrompt.BuildBasePrompt` has an explicit SELECTION-PRIORITY block instructing the model to use `uidoc.Selection.GetElementIds()` on EN/KR pointing language ("these doors", "이 도어들"), never falling back to model-wide `FilteredElementCollector`. Claude already followed this; GPT needed it explicit.

## Sprint 0 Hotfixes (v1.0.2 c-patch — caught in real-user testing)
Three latent multi-provider bugs that broke the task → question → codegen flow on **all three providers**. All fixed.
- **BIBIM-001 — Anthropic 400 on tool loop**: `LlmOrchestrationService.GenerateWithToolsAsync` adds `"name"` to every tool_result block (kept for the Gemini adapter's `functionResponse` mapping). Anthropic's strict schema validator rejects unknown fields → `messages.N.content.0.tool_result.name: Extra inputs are not permitted`. **Fix**: `AnthropicProvider.SendNonStreamingAsync` strips `tool_result.name` from each message before sending. Provider-specific defence — Gemini adapter still gets to use the field.
- **BIBIM-002 — OpenAI 400 on planner**: OpenAI Responses API rejects `text.format=json_object` mode unless an input message contains the literal word "json" (instructions-only doesn't count). **Fix**: `BuildPlannerInput` ends with `[Output format: respond with JSON only — no markdown, no commentary.]` — also reinforces JSON behaviour for Gemini.
- **BIBIM-003 — Gemini planner non-JSON output**: the `-customtools` model variant is specialised for agentic workflows with registered tools and silently ignores `responseMimeType: application/json` when no tools are sent. **Fix**: model id swapped to vanilla `gemini-3.1-pro-preview` (Google's own guidance: use vanilla when <50% of requests involve tool calling). Auto-migration in `ConfigService.LoadRagConfig` rewrites stored configs on next launch with a `.bak`. `ExtractJsonObject` also strips ```json fences as a defensive secondary.

## Model Selector UX (v1.0.2)
- Each model in the Settings selector shows a response-speed indicator (⚡⚡⚡ fast / ⚡⚡ medium / ⚡ slow) with a localised tooltip. Sonnet 4.6 = ⚡⚡⚡, Opus/GPT-5.5 = ⚡⚡, Gemini 3.1 Pro = ⚡. Source of truth: `MODELS` array in `frontend/src/components/SettingsPanel.tsx`. Dynamo mirror lives in `Views/ApiKeySetupView.xaml`.

## Loading-State Safety
- `LlmOrchestrationService.SendMessageAsync` and `GenerateWithToolsAsync` both wrap the body in try/catch with `finally { OnStatusUpdate?.Invoke(null); }`. **Do not remove the finally block** — without it, an LLM error (429, network, etc.) leaves the chat panel stuck on "Generating response...".

## Commit Workflow
1. Scan changes: `git status --porcelain`
2. Stage files explicitly — never `git add -A` or `git add .`
4. Commit message format:
   ```
   <type>: <subject>

   <body>

   Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
   ```
   Types: `feat / fix / refactor / docs / chore / test`
5. No `--no-verify`. No force push to main.
