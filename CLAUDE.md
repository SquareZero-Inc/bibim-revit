# BIBIM_REVIT — Claude Working Notes

## Project
Claude/LLM-powered Revit C# add-in (BYOK) by SquareZero Inc., Apache-2.0. A WebView2 dockable panel runs a React/TS SPA; the C# backend drives an LLM tool-loop that generates C# and executes it inside Revit. Multi-target:

| Revit | Build config | TargetFramework |
|-------|--------------|-----------------|
| 2022–2024 | `R2022`–`R2024` | `net48` |
| 2025–2026 | `R2025`–`R2026` | `net8.0-windows` |
| 2027+ | `R2027` | `net10.0-windows` |

`R2026` is the default baseline (plain `Release`/`Debug` map to it). Current version: 1.1.2.

## Layout
```
Bibim.Core/                       main add-in project (C# backend + embedded SPA)
  BibimApp.cs                     Revit IExternalApplication entry; creates the ExternalEvent
  BibimShowPanelCommand.cs        IExternalCommand — toggles the dockable pane
  BibimDockablePanelProvider.cs   WebView2 host + ALL JS↔C# bridge handlers (4000+ lines — grep, never read whole)
  BibimExecutionHandler.cs        IExternalEventHandler — runs generated code on Revit's main thread
  WebView2Bridge.cs               injects the window.bibim send/on object into the WebView
  DocumentChangeTracker.cs        counts elements touched during dry-run / commit
  Common/                         ConfigService (rag_config + BYOK), Logger, ServiceContainer (custom DI)
  Services/                       LLM orchestration core           → Bibim.Core/Services/CLAUDE.md
    Providers/                    Anthropic/OpenAI/Gemini/Local    → Bibim.Core/Services/Providers/CLAUDE.md
    Prompts/                      system-prompt + question builders
  Models/                         DTOs: chat, session, task-flow, execution, code-library
  Config/                         rag_config templates, release_notes_*.md, i18n/{en,kr}.json (C#-side strings)
  Assets/Icons/                   ribbon / panel icons
  frontend/                       React 19 + Vite 6 + TS 5.7 SPA   → Bibim.Core/frontend/CLAUDE.md
  wwwroot/                        Vite build output — GENERATED, never hand-edit
  redist/                         WebView2 bootstrapper (committed binary)
Bibim.Core.Tests/                 xunit; links source files instead of ProjectReference (dodges Revit SDK)
build.ps1                         5-stage build/sign pipeline
Bibim.V3.sln                      2 projects: Core + Tests
```

## Setup
| Need | How |
|------|-----|
| Build a single config | `dotnet build "Bibim.Core\Bibim.Core.csproj" -c R2026 -p:TargetFramework=net8.0-windows` |
| Diagnose compile errors | Use the `dotnet build` above — **not** `build.ps1` (it auto-elevates admin and closes the window on error before `pause`) |
| Frontend dev (HMR) | `cd Bibim.Core/frontend && npm install && npm run dev` |
| Prerequisites | .NET 8 SDK (R2025–26 + tests + frontend tooling), Node 20+; .NET 10 SDK only for R2027; ≥1 Revit 2022–2027 at `C:\Program Files\Autodesk\Revit {year}`. Inno Setup 6 + signtool optional |
| Revit SDK path override | `REVIT_SDK_PATH` env var (else csproj falls back to the default install path) |

## Build
`build.ps1` runs: **0** clean `bin/Release*`+`obj` (keeps `Output/`) → **1** frontend `npm install`+`npm run build` → **2** `dotnet build` per Revit year (dual output `Release/` KO + `Release_EN/` EN) → **3** `dotnet test` → **4** Inno Setup `BibimInstaller.iss`/`_EN.iss` → **5** `signtool` (skips gracefully if cert absent).

| Command | Effect |
|---------|--------|
| `.\build.ps1` | Full release: all Revit versions, both languages, installers, signing |
| `.\build.ps1 -RevitConfig R2026` | One config only |
| `.\build.ps1 -SkipFrontend -SkipTests` | Skip frontend rebuild + tests (also `-SkipInstaller`, `-Lang {ko\|en\|all}`) |
| `dotnet test "Bibim.Core.Tests\Bibim.Core.Tests.csproj" --no-restore` | Tests only (net8.0, no Revit SDK needed) |

R2027 skips automatically if the .NET 10 SDK is absent. Missing Revit installs are skipped (build guards on `RevitAPI.dll` existence).

## Architecture
Rules to respect when working in this codebase. Subsystem detail lives in the subdirectory CLAUDE.md files linked above.

- **C#↔JS bridge**: `WebView2Bridge.cs` injects the `window.bibim` object; backend handlers are registered in `BibimDockablePanelProvider` via `RegisterAsyncHandler(type, Func<JObject,Task>, swallowCancellation?)` (wraps in `Task.Run`, catches exceptions). The JS-side wrappers + the kebab-case message catalogue → `frontend/CLAUDE.md`.
- **Generated-code execution is two-threaded**: a background thread compiles via Roslyn and enqueues an `ExecutionRequest`, then calls `BibimApp.ExecutionEvent.Raise()`; Revit's main thread runs `BibimExecutionHandler.Execute(UIApplication)`, which wraps the call in a `TransactionGroup` (dry-run → rolled back; commit → committed) and wakes the background thread via `TaskCompletionSource<ExecutionResult>`. **Never call the Revit API off the main thread** — Revit-context tools dispatch through the same ExternalEvent; non-Revit tools (`search_revit_api`, `run_roslyn_check`) run anywhere.
- **Provider-agnostic LLM core**: the orchestrator's canonical message format is **Anthropic-shaped `JArray`** (`{role, content[]}` with `tool_use`/`tool_result` blocks); routing is by model-id prefix. Four providers (Anthropic/OpenAI/Gemini/Local) — conversion mechanics + per-provider quirks → `Services/Providers/CLAUDE.md`.
- **BYOK / config**: `ConfigService` reads `rag_config.json` from the assembly's `Config/` dir but **writes to `%AppData%\BIBIM\rag_config.json`** (Program Files is read-only). Per-provider keys under `api_keys.{anthropic|openai|gemini|local}_api_key`; legacy `claude_api_key` auto-migrates (`.bak` backup). Env overrides: `ANTHROPIC_API_KEY` (or legacy `CLAUDE_API_KEY`), `OPENAI_API_KEY`, `GEMINI_API_KEY`, `BIBIM_LOCAL_LLM_{URL,API_KEY,MODEL}`. **On any key/model save, reset `_llmService` and `_plannerLlmService` to null** so the next call rebuilds with new credentials.
- **DI**: custom static `ServiceContainer` (`Dictionary<Type,object>`) — `Microsoft.Extensions.DependencyInjection` is deliberately avoided (assembly-loading conflicts in the Revit host). Don't introduce it.
- **HttpClient**: never `new HttpClient()` per request. Shared static fields only — `LlmOrchestrationService._httpClient`, and a separate `_downloadHttpClient` in `BibimDockablePanelProvider` for file downloads.
- **C# language**: `Nullable` and `ImplicitUsings` are **disabled** — use explicit `using`s and `string` (not `string?`); `LangVersion=latest`. Every `.cs` opens with the `// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0...` header. `volatile` is invalid on `double`/`long` — use `Volatile.Read/Write(ref field)`.
- **Async handlers**: a `RegisterAsyncHandler` lambda is `Func<JObject,Task>` — if you drop `async`, add explicit `return Task.CompletedTask;` at **every** exit.
- **Models**: `claude-sonnet-4-6` (default), `claude-opus-4-7`, `gpt-5.5`, `gemini-3.1-pro-preview`, `local`. Adding one touches three places (see `Services/Providers/CLAUDE.md`).

## Testing
| | |
|---|---|
| Runner | xunit 2.9.3, TargetFramework `net8.0` (no Revit SDK) |
| Run all | `dotnet test "Bibim.Core.Tests\Bibim.Core.Tests.csproj" --no-restore` |
| Scope | Roslyn compile/analyzer/auto-fix, API inspector, session compatibility, streaming cutoff, result formatting — pure C#, no LLM/Revit calls |
| Fixtures | `Bibim.Core.Tests/TestData/`; `TestData/generated/` is gitignored + regenerated per run |

## Do Not
- Hand-edit `Bibim.Core/wwwroot/` (Vite output) or commit `Bibim.Core/Config/rag_config.json` / `Bibim.Core/ClientBuildConfig.cs` — all gitignored (the last two hold secrets).
- Read `BibimDockablePanelProvider.cs` whole — grep it (4000+ lines).
- `git add -A` / `git add .` — stage files explicitly. No `--no-verify`. No force-push to `main`.

## Commit
- `git status --porcelain` first; if no changes, say so and stop.
- Message: `<type>: <subject>` (`feat`/`fix`/`refactor`/`docs`/`chore`/`test`) + body + `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>`.
- Branches: `fix/...` or `feature/...`; PRs target `main`. Open an issue first for changes to core architecture or the LLM tool loop (see `CONTRIBUTING.md`).
