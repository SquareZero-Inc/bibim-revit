# BIBIM_REVIT — Claude Working Notes

## Project
LLM-powered Revit C# add-in (BYOK: Anthropic / OpenAI / self-hosted). Multi-target:
- `net48` — Revit 2022–2024
- `net8.0-windows` — Revit 2025–2026 (2025.5 / 2026.5+ hosts run it on .NET 10)
- `net10.0-windows` — Revit 2027+

## Build
- Diagnose compile errors: `dotnet build "Bibim.Core\Bibim.Core.csproj" -c R2026 -p:TargetFramework=net8.0-windows`
- Also check `-c R2024 -p:TargetFramework=net48`, `-c R2027 -p:TargetFramework=net10.0-windows` before committing.
- `.\build.ps1` auto-elevates admin — errors close window before `pause`; always diagnose via dotnet directly
- Build configs: `R2022`–`R2027`; full release: `.\build.ps1 -SkipFrontend -SkipTests`
- R2027 requires .NET 10 SDK — build.ps1 skips gracefully if not installed
- Tests: `dotnet test Bibim.Core.Tests\Bibim.Core.Tests.csproj` (links Revit-free source files — keep RevitAPI types out of linked files; see the csproj list)
- Frontend: `cd Bibim.Core\frontend && npm run build` → commits `wwwroot/`

## C# Gotchas
- `volatile` not valid on `double`/`long` — use `Volatile.Read(ref field)` / `Volatile.Write(ref field, value)`
- `BibimDockablePanelProvider` is a partial class split across `BibimDockablePanelProvider*.cs` — use Grep, don't read whole files
- `RunRoslynCheck` in `BibimToolService.cs` runs BIBIM001-005 analyzers + `ApplyAutoFixes` — NOT just a compile check. Don't remove/bypass it as "redundant."
- `HttpClient`: never create per-request with `using` — use shared static field. `_downloadHttpClient` in `BibimDockablePanelProvider`; `_httpClient` (static) in `LlmOrchestrationService`.
- `RegisterAsyncHandler` lambdas are `Func<JObject, Task>` — removing `async` requires explicit `return Task.CompletedTask;` at every exit point.
- net48 needs the explicit `System.Net.Http` framework reference (csproj) — it used to arrive via the removed Anthropic SDK.
- `ElementId` → integer: `IntegerValue` on 2022/2023, `Value` on 2024+ (`#if REVIT_2022 || REVIT_2023`).
- Python edit scripts on Windows: preserve BOM / line endings of the original file (several files have no BOM; the .iss files must stay BOM-less).

## Key Files
- `BibimDockablePanelProvider*.cs` — JS↔C# bridge handlers, planner, task flow, codegen dispatch, commit verification
- `Services/Providers/ModelCatalog.cs` — **single source of truth** for selectable models + per-model request capabilities (thinking default, append-only history, server fallbacks, effort, structured output, max output)
- `Common/ConfigService.cs` — rag_config.json loader, migrations (gemini→default, plaintext keys→DPAPI), per-provider key save, `GetActiveCredentials()`, `llm` tuning block, `GetAnthropicEndpoint()` (optional gateway override, null by default)
- `Common/SecretProtector.cs` — DPAPI (CurrentUser) encryption of stored keys (`dpapi:<base64>`)
- `Services/LlmOrchestrationService.cs` — provider-agnostic tool loop, Roslyn retry, runtime self-correction, live progress, refusal handling
- `Services/Providers/` — `ILlmProvider` (+ `LlmRequestOptions`) + `AnthropicProvider` / `OpenAIProvider` / `LocalProvider` + `LlmProviderFactory`
- `Services/BibimToolService.cs` — LLM tool definitions + execution (RAG, Roslyn check, 5 context tools, read tools `count_elements` / `list_elements`, `search_code_library` when the library has snippets)
- `Services/CodeLibrarySearch.cs` — keyword search over saved snippets (Korean spacing-insensitive) behind `search_code_library`
- `Services/RevitContextProvider.Query.cs` — typed read queries behind the read tools (main thread only)
- `Services/CommitVerifier.cs` — post-commit measured verification (delta vs preview, files on disk)
- `Services/AuditLogService.cs` — `%APPDATA%\BIBIM\audit\audit_YYYYMM.jsonl`, one line per commit/undo
- `Services/ProgressNarrator.cs` — streamed deltas → one-line progress banner text
- `Services/HistoryWindowPolicy.cs` — stepped chat-history window (cache-stable prefix)
- `Services/Prompts/CodeGenSystemPrompt.cs` — code-gen system prompt. `Build(rev, isCodeGen, isFileOutput, allowDirectReadAnswer)`
- `Services/Prompts/PlannerSchema.cs` — JSON schema for the planner's structured output (incl. `taskCategory`)
- `Services/Prompts/CapabilityManifest.cs` — in-session-impossible operations, injected into planner + codegen prompts
- `Services/Prompts/CategoryQuestionTemplates.cs` — planner question checklist + `PlannerGate` (exact greeting/ack match only; `ContainsWriteIntent` for the planner-failure halt)
- `Models/TaskFlowModels.cs` — `TaskState` (incl. `Category`), `TaskCategories`, `TaskPlanResponse`
- `build.ps1` — full build pipeline (frontend → C# → tests → Inno Setup → codesign incl. 2027)

## Models & Providers (v1.2.0)
- Anthropic: `claude-sonnet-5` (default), `claude-opus-5-5`, `claude-opus-5`, `claude-fable-5-1`, legacy `claude-sonnet-4-6`, `claude-opus-4-7`.
- OpenAI (Responses API): `gpt-6-sol`, `gpt-6-luna`, `gpt-5.6-sol`, `gpt-5.6-terra`, legacy `gpt-5.5`.
- Local: `local` (OpenAI-compatible Chat Completions server; model name resolved at runtime).
- **Gemini removed in v1.2.0** — `ModelCatalog.MigrateModelId` rewrites stored `gemini-*` to the default.
- Add a model: one row in `ModelCatalog.Models` + the `MODELS` array in `frontend/src/components/SettingsPanel.tsx` (+ i18n note keys). Routing is by id prefix (`ModelCatalog.ResolveProvider`).
- Canonical message format inside the orchestrator is **Anthropic-shaped JArray**; OpenAI/Local adapters translate.

## Anthropic request rules (live-verified 2026-09-28 on all six Claude models)
- Caching: tools + system markers `ttl: "1h"`, tail marker 5-minute (longer TTLs must come first). No beta header needed. `ApplyIncrementalMessageCaching` strips stale markers and walks past thinking blocks — do not remove it.
- `output_config.effort` per route (config `llm.effort_planner|chat|codegen`, defaults low/medium/high). `output_config.format` json_schema for the planner (works on 4.6/4.7 too).
- Thinking: omitted → model default. When the tool loop wants progress (`LlmRequestOptions.OnProgress`) on thinking-by-default models, send `thinking: {type:"adaptive", display:"summarized"}`. Never send `thinking: disabled` to Opus 5.5 / Fable 5.1 (400).
- Tool-loop turns stream (SSE assembled back into one message by `SseMessageAssembler`); 180 s idle timeout. max_tokens 32000 (clamped by `ModelInfo.MaxOutputTokens`).
- `fallbacks: "default"` + beta `server-side-fallback-2026-07-01` on classifier models (Opus 5 / 5.5, Fable 5.1). `SanitizeFallbackBoundary` drops pre-boundary thinking/tool_use.
- `stop_reason: "refusal"` → `LlmErrorPresenter.RefusalMessage()`, never treated as code/clarification.
- **Append-only models** (Opus 5.5, Fable 5.1 — preserved thinking): the tool loop must not prune earlier turns (`ModelInfo.AppendOnlyHistory` gates `PrunePriorCompileAttempts`). Echo assistant `content` back unchanged.
- No sampling params (temperature/top_p) and no assistant prefill anywhere — both 400 on current models.

## OpenAI request rules
- Responses API for tool turns and the planner: `reasoning.effort` (low/medium/high), planner `text.format` strict `json_schema` (fallback `json_object` needs the word "json" in the input — `BuildPlannerInput` ends with a JSON trailer).
- Streaming chat uses Chat Completions with `reasoning_effort`.
- Not live-verified for GPT-6 (no key available when v1.2.0 was built) — smoke-test before recommending.

## BYOK / API Keys
- Per-provider keys in `%APPDATA%\BIBIM\rag_config.json` under `api_keys.{anthropic|openai|local}_api_key`, **DPAPI-encrypted** (`dpapi:` prefix). Plaintext values are encrypted on the next load (`.bak` kept). Placeholders (`YOUR_...`, `..._HERE`) are left alone. A config copied from another user/PC decrypts to null → "not configured".
- Legacy `claude_api_key` is read as a fallback and mirrored on save.
- Env var overrides (never written to disk): `ANTHROPIC_API_KEY` (or `CLAUDE_API_KEY`), `OPENAI_API_KEY`, `BIBIM_LOCAL_LLM_*`.
- Bridge handlers `save_api_key` (anthropic), `save_openai_api_key`, `save_local_llm_config` call `ConfigService`. **Reset `_llmService` and `_plannerLlmService` to null on any key/model save.** (`_codeGenByTask` is deliberately NOT cleared — session-scoped.)
- `EnsurePlannerService()` applies the optional `planner_model` override (known models only; cross-provider needs its own key). Note: Haiku-class models have a 4096-token cache minimum — the ~2.5k planner prompt would not cache there.

## Harness (v1.2.0)
- Planner returns structured JSON (schema) incl. `taskCategory` (query/export/model_edit/create/delete/view_selection/annotation/other). `IsZeroDeltaByDesign` and file-output rules use the category first; keyword heuristics are only the fallback for category-less tasks. Do not grow keyword lists — extend the schema.
- READ tasks get the read tools and may answer in plain text (non-code response → task Completed).
- Post-commit: `CommitVerifier` block appended to the result; `AuditLogService` line per commit/undo.
- Stepped history window (`ChatHistoryMaxTurns` 10, `ChatHistoryWindowStep` 6) + summary cached per (session, start).
- Self-correction: dry-run judge (`JudgeRuntimeResult`) regenerates once on runtime exception / unexpected 0-delta; scale guard 500.

## RAG (local, on by default)
- `LocalRevitRagService.FetchAsync()` indexes `RevitAPI.xml` (+ `RevitAPIUI.xml`, `RevitAPIIFC.xml`) next to the loaded RevitAPI.dll on first call (~0.5 s), cached for the process lifetime.
- Exposed via the `search_revit_api` tool. `TopK=3`, `MaxChunkDisplayChars=1200`, `MaxMembersPerChunk=30`. English queries only (tokenizer splits on non-ASCII).
- Debug logs: `[INDEX_BUILD_DONE]`, `[HIT]`, `[MISS]` in `%APPDATA%\BIBIM\logs\bibim_debug.txt`.

## Loading-State Safety
- `LlmOrchestrationService.SendMessageAsync` and `GenerateWithToolsAsync` both wrap the body in try/catch with `finally { OnStatusUpdate?.Invoke(null); }`. **Do not remove the finally block** — without it an LLM error leaves the panel stuck.
- Frontend self-heals on `streaming_end`/Stop; the banner label ellipsizes, so long live-progress text is safe. `LoadingModal.REVIT_EXECUTING_LABELS` must match the C# execution labels (substring match).

## Commit Workflow
1. Scan changes: `git status --porcelain`
2. Stage files explicitly — never `git add -A` or `git add .`
3. Commit message format:
   ```
   <type>: <subject>

   <body>

   Co-Authored-By: <the Claude model that made the change> <noreply@anthropic.com>
   ```
   Types: `feat / fix / refactor / docs / chore / test`
4. No `--no-verify`. No force push to main.
