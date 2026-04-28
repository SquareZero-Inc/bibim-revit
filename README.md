# BIBIM AI — Revit Add-in

AI-powered Revit add-in that turns natural language into executable C# code — directly inside Revit.

**Bring Your Own Key (BYOK).** Connects directly to your chosen LLM provider using your own API key. No accounts, no subscriptions, no telemetry.

**Pick from 4 models** — Claude Sonnet 4.6, Claude Opus 4.7, OpenAI GPT-5.5, or Google Gemini 3.1 Pro. Switch any time from the Settings panel.

---

## What it does

- Describe what you want to do in Revit ("Select all doors on Level 1 and rename them") — BIBIM generates, validates, and runs the C# code for you.
- Multi-step task planner: BIBIM breaks complex requests into steps, asks clarifying questions, and confirms before applying changes.
- Code Library: saves generated snippets for reuse across projects.
- Undo support: every applied change can be rolled back with one click.
- Dry-run mode: preview element selections before committing.

---

## Supported Revit versions

| Revit | .NET | Build config |
|-------|------|--------------|
| 2022  | .NET 4.8 | `R2022` |
| 2023  | .NET 4.8 | `R2023` |
| 2024  | .NET 4.8 | `R2024` |
| 2025  | .NET 8.0 | `R2025` |
| 2026  | .NET 8.0 | `R2026` |
| 2027  | .NET 10.0 | `R2027` |

---

## Quick start

### 1. Get an API key

Pick one (or more) — you can register multiple and switch models from Settings:

| Provider | Where to get a key | Key format |
|----------|---------------------|------------|
| Anthropic | [console.anthropic.com](https://console.anthropic.com) | `sk-ant-...` |
| OpenAI | [platform.openai.com/api-keys](https://platform.openai.com/api-keys) | `sk-...` |
| Google | [aistudio.google.com/apikey](https://aistudio.google.com/apikey) | `AIzaSy...` |

In-app: Settings panel has a **"📖 View API Key Setup Guide"** button at the top with step-by-step instructions.

### 2. Install

Download the latest installer from [Releases](../../releases) and run it. The installer registers the add-in for all detected Revit versions automatically.

### 3. Enter your API key(s) and pick a model

Open Revit → BIBIM AI tab → click the gear icon (⚙) → paste each key into its provider's field → Save → choose any model that's now active.

**Recommended default: `claude-sonnet-4-6`** — best balance of quality and cost.

| Model | API ID | Provider | Est. cost / query | Best for |
|-------|--------|----------|-------------------|----------|
| **Sonnet 4.6** ⭐ | `claude-sonnet-4-6` | Anthropic | **~$0.025** | **Most workflows** |
| Opus 4.7 | `claude-opus-4-7` | Anthropic | ~$0.14 | Complex multi-step / agentic tasks |
| GPT-5.5 | `gpt-5.5` | OpenAI | ~$0.06 | Multilingual prompts, strong tool use |
| Gemini 3.1 Pro | `gemini-3.1-pro-preview` | Google | ~$0.02 | Lowest cost, biggest context window |

Cost estimates updated for v1.0.2 — assume a typical Revit query (~3,500 input tokens after caching + ~1,500 output tokens) on the second call within the 5-minute cache window. First call in a fresh session is ~30% higher; sustained sessions trend lower as cache hit ratio grows. See `bibim_v3_debug.txt` for live measurements.

Models without a registered key are disabled in the picker; the tooltip tells you which key to add.

Alternatively, edit `%AppData%\BIBIM\rag_config.json` directly (created after first launch):

```json
{
  "claude_model": "claude-sonnet-4-6",
  "api_keys": {
    "anthropic_api_key": "sk-ant-api03-...",
    "openai_api_key":    "sk-...",
    "gemini_api_key":    "AIzaSy..."
  }
}
```

(`claude_model` is the field name kept for backwards compatibility — it stores any selected model id, including `gpt-5.5` or `gemini-3.1-pro-preview`.)

Or via environment variables (useful for CI / scripted installs):
```
ANTHROPIC_API_KEY=sk-ant-api03-...
OPENAI_API_KEY=sk-...
GEMINI_API_KEY=AIzaSy...
```

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

The build output lands in `Bibim.Core\bin\Release\<year>\`.

---

## Configuration reference (`rag_config.json`)

| Key | Required | Default | Description |
|-----|----------|---------|-------------|
| `claude_model` | No | `claude-sonnet-4-6` | Selected model id (any of the 4 supported models). Field name kept for backwards compat |
| `api_keys.anthropic_api_key` | * | — | Anthropic key. Env: `ANTHROPIC_API_KEY` or `CLAUDE_API_KEY` |
| `api_keys.openai_api_key` | * | — | OpenAI key. Env: `OPENAI_API_KEY` |
| `api_keys.gemini_api_key` | * | — | Google Gemini key. Env: `GEMINI_API_KEY` |
| `validation.gate_enabled` | No | `true` | Run Roslyn analyzer before applying code |
| `validation.auto_fix_enabled` | No | `true` | Attempt auto-fix on analyzer warnings |
| `validation.auto_fix_max_attempts` | No | `2` | Max auto-fix retry rounds |

\* At least one API key matching the selected model is required.

`rag_config.json` is written to `%AppData%\BIBIM\rag_config.json` the first time you save a key via Settings. It is gitignored. For manual setup, copy from `Config/rag_config.template.json`. Upgrades from 1.0.1 are migrated automatically and a `.bak` backup is kept next to the file.

### Revit API documentation search (RAG)

BIBIM ships with a **local BM25 search index** built from `RevitAPI.xml` — the same file Autodesk distributes with every Revit installation. It is the same source the official online API docs render from.

- **No external service, no extra setup.** Works with any of the 4 supported models out of the box.
- **First-call build:** the index is built once per session in ~0.5 s (logged as `[INDEX_BUILD_DONE]` in the debug log) and reused for every subsequent search.
- Coverage: core DB, UI, MEP, Structure, IFC — about 39,000 members across ~2,800 searchable chunks.

The Gemini key field in Settings is reserved for **Gemini as an LLM provider** (Gemini 3.1 Pro), not RAG.

---

## Token usage & cost transparency (v1.0.2)

BIBIM 1.0.2 cuts input-token usage by **~30–40%** for typical sessions. Anthropic prompt caching is enabled by default — within the 5-minute cache window, the system-prompt + tool-definitions prefix is billed at the cached rate (`$0.30 / 1M`, 90% off the normal `$3 / 1M`). OpenAI has automatic caching above 1024-token prefixes (free), and Gemini exposes `cachedContentTokenCount` when content caching is in use.

Cache effectiveness is logged per call and per session in `bibim_v3_debug.txt`:
```
[TokenTracker] rid=abc1234 type=chat in=420 out=180 cache_read=2812 cache_create=0
                session_total_in=8432 session_cache_read=11340 hit_ratio=57.4%
```
- `cache_read`: tokens served from cache (priced at 10% of normal input)
- `cache_create`: tokens written to cache on the first call (priced at 1.25× normal input — recovered on subsequent calls within 5 min)
- `hit_ratio`: cache_read / (input + cache_read) for the current session

Higher `hit_ratio` = lower bill. A typical multi-turn coding session sits at 60–80%.

---

## Project structure

```
Bibim.Core/
  BibimApp.cs                  — IExternalApplication entry point, ribbon setup
  BibimDockablePanelProvider.cs — WebView2 bridge, all JS↔C# message handlers
  Services/
    LlmOrchestrationService.cs — Provider-agnostic tool loop + Roslyn retry
    Providers/
      ILlmProvider.cs          — Provider abstraction (canonical Anthropic-shape)
      AnthropicProvider.cs     — Claude Messages API (raw HTTP, SSE)
      OpenAIProvider.cs        — GPT via Responses API + Chat Completions
      GeminiProvider.cs        — Gemini generateContent / streamGenerateContent
      LlmProviderFactory.cs    — Routes a model id to the matching provider
    BibimToolService.cs        — LLM tool definitions + Revit execution
    RoslynCompilerService.cs   — In-process C# compilation
    RoslynAnalyzerService.cs   — BIBIM001–005 custom analyzers
    LocalRevitRagService.cs    — Local BM25 RAG over RevitAPI.xml
    BM25Engine.cs              — Pure C# BM25, no NuGet deps
    RevitContextProvider.cs    — Document/selection/view context → prompts
    LocalSessionManager.cs     — Chat session persistence (disk)
  Common/
    ConfigService.cs           — rag_config.json loader + per-provider key save
  frontend/                    — Vite + React + TypeScript SPA → wwwroot/
```

---

## Troubleshooting & reporting issues

When something goes wrong, BIBIM writes debug artifacts to two local locations:

| File / Folder | Contents |
|---|---|
| `%USERPROFILE%\bibim_v3_debug.txt` | Main log — all events, errors, and stack traces. Rotates at 10 MB (`.bak` kept). |
| `%AppData%\BIBIM\rag_config.json` | User config file — API keys, selected model, RAG store IDs. |
| `%AppData%\BIBIM\debug\codegen\YYYYMMDD\` | Per-run artifacts: system prompt, task prompt, raw LLM output, compiled `.cs` files, and compiler diagnostics. Created for every code-generation run. |

**To open quickly (Win+R):**
```
%USERPROFILE%\bibim_v3_debug.txt
%AppData%\BIBIM\rag_config.json
%AppData%\BIBIM\debug\codegen
```

When filing a GitHub issue, please attach:
1. `bibim_v3_debug.txt` (or the relevant section)
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

Apache 2.0 — see [LICENSE](LICENSE).

Copyright 2026 [SquareZero Inc.](https://bibim.app/en)
