# BIBIM v1.1.0

**Release date**: 2026-05-18

> **Self-hosted LLM release** — Connect BIBIM to your own Ollama, LM Studio, vLLM, or llama.cpp server. Built for teams under NDA, enterprises with data-residency requirements, and heavy users tired of paying per token.

---

## TL;DR

**Drop in your local LLM server URL and BIBIM runs on it. No cloud round-trip, no data leaving your network, zero token cost.**

v1.0.2 opened BIBIM to three cloud LLMs (Claude / GPT / Gemini) via BYOK. **v1.1.0 opens it to self-hosted, completing the BYOK promise** — and at the same time tightens code-generation accuracy, session startup, auto-updater security, and Settings UX across the board.

---

## This release at a glance

| Area | What changed | Who feels it |
|------|------|---------|
| 🆕 **Self-hosted LLM support** | Connect directly to Ollama / LM Studio / vLLM / llama.cpp — any OpenAI-compatible server. One URL field + auto model discovery. | NDA / data-residency teams · firms with corporate GPU · heavy users tired of per-call cost |
| ✅ **Code-generation accuracy ↑** | The BIBIM001 (Transaction-required) analyzer no longer short-circuits when it sees a method named `Execute` — a longstanding false-positive that hid missing-Transaction bugs in generated code. | **Every user** — code generation is meaningfully more robust |
| ⚡ **1–2 s faster session start** | `RoslynCompilerService` is now a real singleton (was being `new`'d six times); local RAG cache fast-path runs lock-free. | Heavy use patterns (frequently re-opening the panel) |
| 🔒 **Auto-updater hardened** | Host whitelist (`github.com` / `objects.githubusercontent.com` only) + 10-min timeout + partial-download cleanup. | All users — auto-update is safer end-to-end |
| 🎨 **Settings panel overhaul** | Active-model chip at the top · configured key sections collapse · single Local LLM entry in the model picker. | Returning users — roughly half the visual density |
| 🧹 **Code health** | `−432` lines of dead `#if NET48` conditional compilation · zero-reference class removed · 64 / 64 unit tests passing. | Future build stability · contributor onramp |

---

## What's new

### 1. Self-hosted local LLM support (NEW)

Connect BIBIM directly to any **OpenAI-compatible LLM server** running on your corporate GPU, a rented cloud GPU instance (RunPod / Vast.ai), or your own workstation.

Tested setups:
- **Ollama** — most common, ideal for personal workstation
- **LM Studio** — GUI-first option
- **vLLM** — production / corporate GPU cluster standard
- **llama.cpp server** — lightweight, works on CPU / Apple Silicon
- Any other server speaking the OpenAI `/v1/chat/completions` shape (gateways like OpenRouter included)

**Setup matches cloud BYOK friction — one field, one paste**:

1. Settings → Local LLM (Self-hosted) section
2. Paste the server URL (e.g. `http://localhost:11434/v1`)
3. Click **Test & Save**
4. Models installed on your server are auto-detected and shown in a picker
5. Done — start chatting immediately

### 2. Curated recommended models — BIBIM-validated

We ran an OpenRouter sweep against our Revit 2024 smoke matrix to see which open-weights models actually hold up on Revit API C# (generic coding benchmarks don't translate). **Models that support tool calling, 30B+ parameters**:

| Model | VRAM (4-bit) | Notes |
|------|------|------|
| **Gemma 4 26B A4B IT** ⭐ | ~16GB | BIBIM-validated top non-Claude pick. Fits RTX 4090 |
| Codestral 2508 (22B) | ~14GB | Fastest. Simple tasks only |
| Llama 3.3 70B Instruct | ~40GB+ | Won't fit a single 24GB GPU. A100 / dual 3090 class |

⚠ Smaller models (≤7B) have low Revit API tool-calling reliability and tend to fail code generation. Non-recommended models still work but result quality is not guaranteed.

### 3. Authenticated self-hosted setups (Bearer token)

For setups behind auth, the Advanced section now has an **API key (Bearer token)** field covering:

- vLLM launched with `--api-key <token>`
- Self-hosted LLM behind an authenticated reverse proxy
- Cloud GPU rental endpoints (RunPod, Vast.ai, etc.) with endpoint keys
- Tailscale / Cloudflare Tunnel gateway tokens

The value is sent as `Authorization: Bearer <value>` on every request. **Leave blank for default Ollama / LM Studio (no auth)**.

### 4. Settings panel UX overhaul

- **Active model chip** — One-line summary at the top of the panel: "Active: Claude Sonnet 4.6 · sk-ant-...Ab3c". You always know what's powering BIBIM at a glance.
- **Configured key sections collapse** — Provider sections whose key is already saved render as a compact `✓ Key configured: ...Ab3c [Replace]` row. Returning users see roughly half the visual density.
- **Single Local LLM entry in the model picker** — The three previously-separate OSS options (Gemma / Llama / Codestral) are now one **Local LLM (Self-hosted)** entry whose note dynamically shows your active server-side model (e.g. `Active: gemma2:27b`).
- **Section reorder** — Guide → Current setup → Provider keys → Model picker → Feedback. Reads top-to-bottom as the actual setup flow.

---

## Automatic migration

When existing v1.0.2 / v1.0.3 users first launch v1.1.0:

- If `claude_model` holds an old OpenRouter id (e.g. `google/gemma-4-26b-a4b-it`), it migrates to `"local"` and stashes the model fragment as `local.model_name`
- A `rag_config.json.bak` backup is created automatically

→ **Existing setup keeps working** with no reconfiguration.

One-line marker in the debug log:
```
[ConfigService]: Migrated saved model id 'google/gemma-4-26b-a4b-it' → 'local' (local.model_name = 'gemma-4-26b-a4b-it', rewrote rag_config.json).
```

---

## Bug fixes / improvements

### Local LLM polish

- **Chat unblocked for unauthenticated Local LLM setups.** The pre-flight gate in the `user_message` handler used to reject any request with an empty `ApiKey`, which broke the default Ollama / LM Studio install (no auth = empty key is correct). The gate is now provider-aware: Local validates `LocalServerUrl`, cloud providers validate `ApiKey`. Error message for Local now points to the URL field instead of misdirecting users to add a key.
- Friendly guidance when model auto-detect fails — "Settings → Advanced → fill in the model name override" message (previously a confusing 404 error).
- API key field is now correctly labelled "API key (Bearer token)"; tooltip leads with "Sent as `Authorization: Bearer <value>` header".
- Settings panel visual density reduced ~50% for returning users.

### Security hardening

- **Auto-updater is now host-whitelisted and time-bounded.** The `download_update` handler accepts only `https://github.com` / `https://objects.githubusercontent.com` URLs (defense-in-depth against a hypothetical GitHub Releases compromise or WebView hijack), wraps the full body stream in a 10-minute `CancellationTokenSource`, and cleans up partial downloads on timeout / network failure. Typed progress messages (`timeout` / `network` / `untrusted_url`) let the UI route specific error toasts.

### Analyzer correctness

- **BIBIM001 (Transaction-required) no longer hides missing-Transaction bugs in generated code.** The previous heuristic short-circuited as "satisfied" whenever the enclosing method was named `Execute` — on the false premise that `BibimExecutionHandler` wraps `Execute` in an outer Transaction. It does not (`RunCommit` uses no wrapper; `RunDryRun` uses a `TransactionGroup`, which can't host modification APIs directly). Bare `doc.Delete(id)` inside `Execute` was passing silently, then either failing at runtime or leaving the document inconsistent. The heuristic now requires a real `Transaction` `using` ancestor; `TransactionGroup` / `SubTransaction` are explicitly rejected via regex as false-positive satisfiers. Roslyn retry usually auto-fixes the resulting warning, so most users will experience this as "code generation is slightly more robust".

### Performance

- **`RoslynCompilerService` is now a true panel-level singleton.** Its constructor scans `AppDomain.GetAssemblies()` (~100–300 ms each). Six call sites in `BibimDockablePanelProvider` were calling `new RoslynCompilerService()` directly despite `BibimApp.OnStartup` already registering a process-wide instance. Reclaims ~1–2 s of compounded startup work per session.
- **`LocalRevitRagService` cache fast-path no longer takes a lock.** The BM25 engine for `search_revit_api` builds once per session under a build lock, but cached reads now skip the lock entirely (volatile read + double-check). Parallel `search_revit_api` tool_use blocks dispatched in a single LLM turn no longer serialize.

### Logging

- **Log path moved**: `%USERPROFILE%\bibim_v3_debug.txt` → `%APPDATA%\BIBIM\logs\bibim_debug.txt`. Matches the rest of the addon's storage layout under `%APPDATA%\BIBIM\` and stops leaving an orphan file at the home-directory root. Old logs are not migrated — left in place so you don't lose support artifacts.
- **Diagnostics report now includes a `LogFile` check** that surfaces the current log size and flags the legacy path if a stale `bibim_v3_debug.txt` is still present.

### Code health (no user-visible change)

- `#if NET48` conditional serialization attributes removed across `CodeLibraryModels.cs`, `SessionModels.cs`, `TaskFlowModels.cs` (112 occurrences). `JsonHelper` uses Newtonsoft.Json on both targets, so the `[JsonPropertyName]` branch on net8 was being silently ignored at runtime. Single `[JsonProperty]` now applies to both. **−432 lines of zero-value conditional compilation.**
- `ConversationContextManager` deleted — 122-line class with zero references.
- `GeminiProvider.SendStreamingAsync` now parses `functionCall` + `thoughtSignature` from SSE chunks and stashes them on `StreamResult.ToolUseBlocks` for forward compatibility with a future streaming + tools wiring.
- `BibimApp.OnShutdown` now logs the exception message if `DocumentChanged` unsubscribe throws (was swallowed silently).
- `*.tsbuildinfo` added to `.gitignore` so TypeScript's incremental-build cache stops cluttering `git status`.

### UX / i18n

- Feedback buttons in chat messages now render `👍 Helpful` / `👎 Not Helpful` (was literal "Up" / "Down" — the i18n keys already existed and were used in tooltips, just not in button labels).
- Multi-select question card now reads `Select all that apply` from i18n (was hardcoded English) and is translated in the Korean build.

64 / 64 unit tests passing (3 new tests added for the BIBIM001 heuristic fix; 1 stale test rewritten to assert the corrected behavior).

---

## Affected users

| Environment | v1.1.0 |
|------|------|
| Cloud BYOK (Anthropic / OpenAI / Gemini) | No change — existing keys keep working |
| Already running self-hosted LLM | **New option** — connect via Local LLM section |
| NDA / data-residency constraints | **New option** — BIBIM now runs entirely on your infrastructure |

---

## Builds

| Target | Status |
|----------|------|
| Revit 2024 (net48) | ✅ |
| Revit 2025 (net8.0-windows) | ✅ |
| Revit 2026 (net8.0-windows) | ✅ |
| Revit 2027 (net10.0-windows) | ✅ |

---

## Requirements

- Autodesk Revit 2022 or later (Windows)
- At least one of:
  - A cloud API key ([console.anthropic.com](https://console.anthropic.com/) (Claude) / [platform.openai.com/api-keys](https://platform.openai.com/api-keys) (GPT) / [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (Gemini))
  - **NEW**: A self-hosted OpenAI-compatible LLM server (Ollama / LM Studio / vLLM / llama.cpp). Tool-calling support and 30B+ parameters recommended.

## Source

[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
