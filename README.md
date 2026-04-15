# BIBIM AI — Revit Add-in

AI-powered Revit add-in that turns natural language into executable C# code — directly inside Revit.

**Bring Your Own Key (BYOK).** Connects directly to Anthropic's Claude API using your own API key. No accounts, no subscriptions, no telemetry.

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

### 1. Get an Anthropic API key

Sign up at [console.anthropic.com](https://console.anthropic.com) and create an API key.

### 2. Install

Download the latest installer from [Releases](../../releases) and run it. The installer registers the add-in for all detected Revit versions automatically.

### 3. Enter your API key and choose a model

Open Revit → BIBIM AI tab → click the gear icon (⚙) in the top-right corner → paste your API key → Save → then pick a Claude model.

**Recommended model: `claude-sonnet-4-6`** — best balance of quality and cost.

| Model | API ID | Est. cost / query | Best for |
|-------|--------|-------------------|----------|
| Haiku 4.5 | `claude-haiku-4-5-20251001` | ~$0.01 | Simple selections, quick tasks |
| **Sonnet 4.6** ⭐ | `claude-sonnet-4-6` | **~$0.04** | **Most workflows** |
| Opus 4.6 | `claude-opus-4-6` | ~$0.20 | Complex multi-step tasks |

Cost estimates assume a typical Revit query (~5,000 input tokens + ~1,000 output tokens).
Actual cost depends on context size (active view, selection, Revit version API hints).

Alternatively, edit `%AppData%\BIBIM\rag_config.json` directly (created after first launch):

```json
{
  "claude_model": "claude-sonnet-4-6",
  "api_keys": {
    "claude_api_key": "sk-ant-api03-..."
  }
}
```

Or via environment variable (useful for CI/scripted installs):
```
CLAUDE_API_KEY=sk-ant-api03-...
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
| `claude_model` | Yes | — | Anthropic model ID (e.g. `claude-sonnet-4-6`) |
| `api_keys.claude_api_key` | Yes* | — | Anthropic API key. Can be set via `CLAUDE_API_KEY` env var instead |
| `validation.gate_enabled` | No | `true` | Run Roslyn analyzer before applying code |
| `validation.auto_fix_enabled` | No | `true` | Attempt auto-fix on analyzer warnings |
| `validation.auto_fix_max_attempts` | No | `2` | Max auto-fix retry rounds |

`rag_config.json` is written to `%AppData%\BIBIM\rag_config.json` when you save the API key via the Settings panel. It is gitignored. For manual setup, copy from `Config/rag_config.template.json`.

---

## Project structure

```
Bibim.Core/
  BibimApp.cs                  — IExternalApplication entry point, ribbon setup
  BibimDockablePanelProvider.cs — WebView2 bridge, all JS↔C# message handlers
  Services/
    LlmOrchestrationService.cs — Anthropic API streaming, tool loop
    BibimToolService.cs        — LLM tool definitions + Revit execution
    RoslynCompilerService.cs   — In-process C# compilation
    RoslynAnalyzerService.cs   — BIBIM001–005 custom analyzers
    RevitContextProvider.cs    — Document/selection/view context → prompts
    LocalSessionManager.cs     — Chat session persistence (disk)
  Common/
    ConfigService.cs           — rag_config.json loader + API key save
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

→ Contact us at **dev@sqzr.team**

---

## License

Apache 2.0 — see [LICENSE](LICENSE).

Copyright 2026 [SquareZero Inc.](https://bibim.app/en)
