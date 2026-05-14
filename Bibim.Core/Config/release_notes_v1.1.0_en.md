# BIBIM v1.1.0

**Release date**: 2026-05-16

> **Self-hosted LLM release** — Connect BIBIM to your own Ollama, LM Studio, vLLM, or llama.cpp server. Built for teams under NDA, enterprises with data-residency requirements, and heavy users tired of paying per token.

---

## TL;DR

**Drop in your local LLM server URL and BIBIM runs on it. No cloud round-trip, no data leaving your network, zero token cost.**

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

- Friendly guidance when model auto-detect fails — "Settings → Advanced → fill in the model name override" message (previously a confusing 404 error)
- API key field is now correctly labelled "API key (Bearer token)"; tooltip leads with "Sent as `Authorization: Bearer <value>` header"
- Settings panel visual density reduced ~50% for returning users

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
