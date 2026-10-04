# BIBIM AI — Revit Add-in

AI-powered Revit add-in that turns natural language into executable C# code — directly inside Revit.

**Bring Your Own Key (BYOK).** Connects directly to your chosen LLM provider using your own API key. No accounts, no subscriptions, no telemetry.

**Current-generation models** — Claude Sonnet 5 (default), Opus 5.5, Opus 5, Fable 5.1, GPT-6 Sol / Luna, GPT-5.6 Sol / Terra, plus the previous generation (Sonnet 4.6, Opus 4.7, GPT-5.5) and any self-hosted OpenAI-compatible server. Switch any time from the Settings panel.

---

## What it does

- Describe what you want to do in Revit ("Select all doors on Level 1 and rename them") — BIBIM plans the task, asks only what is genuinely missing, generates C#, compiles and statically checks it, runs a rolled-back preview, and applies it when you press **Apply**.
- **Honest by design**: requests that are impossible from a running session (ribbon buttons, other add-ins' UI, …) are declined up front with an alternative; after Apply, the completion message reports **measured** changes (elements added / modified / deleted, files verified on disk) instead of trusting the generated code's own "done".
- **Fast lookups**: simple questions ("how many doors on L2?") are answered straight from typed read tools — no code generation.
- **Live progress**: while code is being generated the banner shows what the model is reasoning about or writing.
- Code Library: saves generated snippets for reuse across projects — and the model can search it (`search_code_library`) to adapt proven code for similar tasks.
- Undo support: the last applied change can be rolled back with one click.
- Local audit log of every apply / undo.

---

## Supported Revit versions

| Revit | .NET | Build config |
|-------|------|--------------|
| 2022  | .NET 4.8 | `R2022` |
| 2023  | .NET 4.8 | `R2023` |
| 2024  | .NET 4.8 | `R2024` |
| 2025  | .NET 8.0 (2025.5+ hosts run on .NET 10) | `R2025` |
| 2026  | .NET 8.0 (2026.5+ hosts run on .NET 10) | `R2026` |
| 2027  | .NET 10.0 | `R2027` |

The 2025 / 2026 builds target .NET 8 so they load on every update of those versions; Revit 2025.5 / 2026.5 and later host them on .NET 10.

---

## Quick start

### 1. Get an API key

Pick one (or both) — you can register several and switch models from Settings:

| Provider | Where to get a key | Key format |
|----------|---------------------|------------|
| Anthropic | [console.anthropic.com](https://console.anthropic.com) | `sk-ant-...` |
| OpenAI | [platform.openai.com/api-keys](https://platform.openai.com/api-keys) | `sk-...` |

In-app: Settings panel has a **"📖 View API Key Setup Guide"** button at the top with step-by-step instructions.

### 2. Install

Download the latest installer from [Releases](../../releases) and run it. The installer registers the add-in for all detected Revit versions automatically.

### 3. Enter your API key(s) and pick a model

Open Revit → BIBIM AI tab → click the gear icon (⚙) → paste each key into its provider's field → Save → choose any model that's now active.

**Recommended default: `claude-sonnet-5`** — best balance of quality, speed and cost.

| Model | API ID | Provider | Est. cost / query | Best for |
|-------|--------|----------|-------------------|----------|
| **Sonnet 5** ⭐ | `claude-sonnet-5` | Anthropic | **~$0.03** | **Most workflows** |
| Opus 5.5 | `claude-opus-5-5` | Anthropic | ~$0.16 | Complex multi-step / agentic tasks |
| Opus 5 | `claude-opus-5` | Anthropic | ~$0.20 | Long agentic tasks |
| Fable 5.1 | `claude-fable-5-1` | Anthropic | ~$0.40 | Hardest problems (most capable, highest cost) |
| Sonnet 4.6 | `claude-sonnet-4-6` | Anthropic | ~$0.04 | Previous generation |
| Opus 4.7 | `claude-opus-4-7` | Anthropic | ~$0.20 | Previous generation |
| GPT-6 Sol | `gpt-6-sol` | OpenAI | ~$0.03 | Balanced GPT option |
| GPT-6 Luna | `gpt-6-luna` | OpenAI | ~$0.002 | Simple lookups at minimal cost |
| GPT-5.6 Sol | `gpt-5.6-sol` | OpenAI | ~$0.08 | GPT-5.6 flagship |
| GPT-5.6 Terra | `gpt-5.6-terra` | OpenAI | ~$0.03 | GPT-5.6 mid tier |
| GPT-5.5 | `gpt-5.5` | OpenAI | ~$0.08 | Previous generation |

Cost estimates are rough per-query figures scaled from each provider's list prices (September 2026), assuming prompt caching is warm. Real cost depends on task complexity and session length — see the token log lines in the debug log.

Models without a registered key are disabled in the picker; the tooltip tells you which key to add.

Alternatively, edit `%AppData%\BIBIM\rag_config.json` directly (created after first launch):

```json
{
  "claude_model": "claude-sonnet-5",
  "api_keys": {
    "anthropic_api_key": "sk-ant-api03-...",
    "openai_api_key":    "sk-..."
  }
}
```

Keys typed here in plain text are **encrypted automatically** (Windows DPAPI, current user) the next time BIBIM starts. `claude_model` is a legacy field name — it stores any model id from the list above.

Or via environment variables (useful for CI / scripted installs; never written to disk):
```
ANTHROPIC_API_KEY=sk-ant-api03-...
OPENAI_API_KEY=sk-...
```

> **Gemini** was removed as a provider in v1.2.0. A saved Gemini selection is migrated to `claude-sonnet-5` automatically.

---

## Build from source

**Prerequisites**
- Visual Studio 2022 or .NET SDK 8.0+ (+ .NET 10 SDK for R2027)
- Revit installed at the default path (`C:\Program Files\Autodesk\Revit <year>`)
- Node.js 20+ (for frontend build)

**Build a specific Revit version:**
```powershell
dotnet build "Bibim.Core\Bibim.Core.csproj" -c R2026 -p:TargetFramework=net8.0-windows
```

**Full release build (all versions, KO + EN):**
```powershell
.\build.ps1
```

**Skip frontend and tests for quick iteration:**
```powershell
.\build.ps1 -SkipFrontend -SkipTests -RevitConfig R2026
```

**Unit tests:**
```powershell
dotnet test "Bibim.Core.Tests\Bibim.Core.Tests.csproj"
```

The build output lands in `Bibim.Core\bin\Release\<year>\`.

---

## Configuration reference (`rag_config.json`)

| Key | Required | Default | Description |
|-----|----------|---------|-------------|
| `claude_model` | No | `claude-sonnet-5` | Selected model id (any model in the Settings list). Field name kept for backwards compat |
| `api_keys.anthropic_api_key` | * | — | Anthropic key (DPAPI-encrypted at rest). Env: `ANTHROPIC_API_KEY` or `CLAUDE_API_KEY` |
| `api_keys.openai_api_key` | * | — | OpenAI key (DPAPI-encrypted at rest). Env: `OPENAI_API_KEY` |
| `planner_model` | No | — | Optional cheaper model for the planning step only (same provider, known ids) |
| `llm.effort_planner` / `effort_chat` / `effort_codegen` | No | `low` / `medium` / `high` | Reasoning effort per route |
| `llm.prompt_cache_ttl` | No | `1h` | Cache lifetime of the system-prompt + tools prefix (`1h` or `5m`) |
| `validation.gate_enabled` | No | `true` | Run Roslyn analyzer before applying code |
| `validation.auto_fix_enabled` | No | `true` | Attempt auto-fix on analyzer warnings |
| `validation.self_correction_enabled` | No | `true` | Dry-run after compile and regenerate once on runtime failure |

\* At least one API key matching the selected model is required (the self-hosted option needs a server URL instead).

`rag_config.json` is written to `%AppData%\BIBIM\rag_config.json` the first time you save a key via Settings. For manual setup, copy from `Config/rag_config.template.json`. Every migration keeps a one-time `rag_config.json.bak` next to the file.

### Revit API documentation search (RAG)

BIBIM ships with a **local BM25 search index** built from `RevitAPI.xml` — the same file Autodesk distributes with every Revit installation, and the source the official online API docs render from.

- **No external service, no extra setup.** Works with every supported model.
- **Version-exact:** the index is built from the XML next to the `RevitAPI.dll` loaded in the running Revit, so the docs always match your version.
- **First-call build:** the index is built once per session in ~0.5 s and reused for every subsequent search.
- Coverage: core DB, UI, MEP, Structure, IFC — about 39,000 members across ~2,800 searchable chunks. Queries are English API names (the model translates).

---

## Token usage & cost

- **Prompt caching**: the system prompt and tool definitions are cached for 1 hour; the growing conversation tail for 5 minutes. The chat-history window moves in steps so the cached prefix stays stable across turns.
- **Per-route effort**: planning runs at low effort, chat at medium, code generation at high.
- Cache effectiveness is logged per call in the debug log:
```
[TokenTracker] rid=abc1234 type=chat in=420 out=180 cache_read=2812 cache_create=0
                session_total_in=8432 session_cache_read=11340 hit_ratio=57.4%
```
- `cache_read`: tokens served from cache (≈10% of the normal input price)
- `cache_create`: tokens written to cache (1.25× for 5-minute entries, 2× for 1-hour entries — recovered on later hits)
- Token figures shown in the panel include cached input (fresh + cache read + cache write).

---

## Project structure

```
Bibim.Core/
  BibimApp.cs                  — IExternalApplication entry point, ribbon setup
  BibimDockablePanelProvider.cs — WebView2 bridge handlers, planner, task flow, codegen dispatch
  BibimExecutionHandler.cs     — Main-thread execution: dry-run (rolled back) / commit / undo
  Services/
    LlmOrchestrationService.cs — Provider-agnostic tool loop, Roslyn retry, self-correction
    Providers/
      ModelCatalog.cs          — Selectable models + per-model request capabilities
      ILlmProvider.cs          — Provider abstraction (canonical Anthropic-shape)
      AnthropicProvider.cs     — Claude Messages API (raw HTTP, SSE assembly, caching, fallbacks)
      OpenAIProvider.cs        — GPT via Responses API + Chat Completions
      LocalProvider.cs         — Self-hosted OpenAI-compatible servers
      LlmProviderFactory.cs    — Routes a model id to the matching provider
    Prompts/                   — Code-gen prompt, capability manifest, planner schema
    BibimToolService.cs        — LLM tool definitions + execution (RAG, Roslyn check, model context, read tools)
    RevitContextProvider*.cs   — Live model context and typed read queries
    CommitVerifier.cs          — Post-commit measured verification
    AuditLogService.cs         — Local JSONL audit trail
    RoslynCompilerService.cs   — In-process C# compilation
    RoslynAnalyzerService.cs   — BIBIM001–005 custom analyzers
    LocalRevitRagService.cs    — Local BM25 RAG over RevitAPI.xml
  Common/
    ConfigService.cs           — rag_config.json loader, migrations, per-provider key save
    SecretProtector.cs         — DPAPI encryption of stored keys
  frontend/                    — Vite + React + TypeScript SPA → wwwroot/
Bibim.Core.Tests/              — xUnit tests (analyzers, compiler, request shapes, verifier, …)
```

---

## Troubleshooting & reporting issues

When something goes wrong, BIBIM writes debug artifacts locally:

| File / Folder | Contents |
|---|---|
| `%AppData%\BIBIM\logs\bibim_debug.txt` | Main log — all events, errors, and stack traces. Rotates automatically. |
| `%AppData%\BIBIM\rag_config.json` | User config — selected model, encrypted keys, options. |
| `%AppData%\BIBIM\debug\codegen\YYYYMMDD\` | Per-run artifacts: system prompt, task prompt, raw LLM output, compiled `.cs` files, compiler diagnostics. |
| `%AppData%\BIBIM\audit\audit_YYYYMM.jsonl` | One line per apply / undo: document, model, affected element ids, code hash, verification result. |

**To open quickly (Win+R):**
```
%AppData%\BIBIM\logs
%AppData%\BIBIM\debug\codegen
%AppData%\BIBIM\audit
```

When filing a GitHub issue, please attach:
1. `bibim_debug.txt` (or the relevant section)
2. The `codegen/YYYYMMDD/...` folder for the failing run (contains the prompt and generated code — **review for sensitive project data before sharing**)

→ [Open an issue](https://github.com/SquareZero-Inc/bibim-revit/issues)

---

## Enterprise & custom deployments

BIBIM is free and open source for individual and team use. If you need:

- **Managed deployment** across a large firm with centralized API key management
- **Custom LLM routing** (Azure OpenAI, private endpoints, usage tracking)
- **Firm-specific prompt packs** tuned to your standards and workflows
- **Priority support** and SLA

→ Contact us at **seokwoo.hong@sqzr.team**

---

## License

Apache 2.0 — see [LICENSE](LICENSE). Third-party components: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Copyright 2026 [SquareZero Inc.](https://bibim.app/en)
