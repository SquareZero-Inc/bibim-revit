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
- `Common/ConfigService.cs` — rag_config.json loader, `SaveApiKey()`, `GetMaskedApiKey()`
- `Services/LlmOrchestrationService.cs` — Anthropic API streaming, tool loop
- `Services/BibimToolService.cs` — LLM tool definitions + Revit code execution
- `Services/TokenTracker.cs` — local session token accumulators (no remote sync)
- `Services/GeminiRagService.cs` — optional RAG via Gemini fileSearch; gracefully skips if no API key
- `build.ps1` — full build pipeline (frontend → C# → tests → Inno Setup → codesign)

## BYOK / API Key
- Key stored in `Config/rag_config.json` under `api_keys.claude_api_key`
- Can override with `CLAUDE_API_KEY` env var
- UI: Settings panel (⚙ button) in the header — calls `save_api_key` bridge → `ConfigService.SaveApiKey()`
- No key → `user_message` handler returns friendly guidance message before calling LLM

## RAG (optional)
- `GeminiRagService.FetchAsync()` fetches Revit API docs via Gemini fileSearch API
- Requires `gemini_api_key` + version-specific store IDs in `rag_config.json`
- Silently skips (`Status = "no_api_key"`) if not configured — core functionality unaffected

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
