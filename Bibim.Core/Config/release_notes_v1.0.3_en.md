# BIBIM v1.0.3

**Release Date**: 2026-04-29

> **Hotfix release** — addresses seven multi-provider latent defects surfaced by real-user testing of v1.0.2. No new features. Stability-only.

---

## TL;DR

**The "API request failed: 400" error you saw mid-codegen on Claude / GPT-5.5 / Gemini 3.1 Pro — all gone.**

---

## What changes for users

| Symptom (v1.0.2) | Result (v1.0.3) |
|------------------|-----------------|
| Gemini skipped questions and dumped raw C# into the chat bubble | ✅ Proper task → questions → codegen flow |
| GPT-5.5 mid-session "API request failed: 400" | ✅ Gone |
| Claude on complex flows (Excel export, phase plans) hit the same 400 | ✅ Gone |
| Gemini codegen turn 2: `Function call is missing a thought_signature` | ✅ Gone |
| No way to tell which model is faster from Settings | ⚡⚡⚡ → ⚡ glyphs visible at a glance |

---

## Fixes (BIBIM-001 → 007)

### 1. Anthropic 400 — `tool_result.name` rejected (BIBIM-001)
After Claude called a tool (e.g. `search_revit_api`, `run_roslyn_check`) and we sent the tool result back, Anthropic's strict schema validator rejected our `name` field on `tool_result` blocks. **Every second LLM call onwards returned 400** and the tool loop died.

→ `AnthropicProvider` now strips `tool_result.name` before sending. The Gemini adapter still gets to use the field on its side.

### 2. OpenAI 400 — "messages must contain the word 'json'" (BIBIM-002)
Selecting GPT-5.5 broke the task planner on the first call. OpenAI's Responses API rejects `text.format=json_object` mode unless an *input message* contains the literal word "json" — having it in `instructions` (the system prompt) doesn't count.

→ `BuildPlannerInput` ends with `[Output format: respond with JSON only — no markdown, no commentary.]`. Bonus: this also reinforces JSON behaviour for Gemini.

### 3. Gemini model swap (BIBIM-003)
We previously routed Gemini through `gemini-3.1-pro-preview-customtools` — a Google variant specialised for agentic workflows with registered custom tools. It silently ignores `responseMimeType: application/json` when no tools are sent. The planner couldn't get JSON out, fell back to direct chat, and the assistant dumped raw code into a chat bubble.

→ Switched to vanilla `gemini-3.1-pro-preview`. Google's own guidance: use vanilla when <50% of requests involve tool calling. Existing configs holding the old id are **auto-migrated on next launch** with a `.bak` backup.

### 4. Settings panel — response-speed glyphs
Each model in the picker now shows a quick-read speed indicator with a localised tooltip:

| Model | Glyph |
|-------|-------|
| Claude Sonnet 4.6 | ⚡⚡⚡ Fast |
| Claude Opus 4.7 / GPT-5.5 | ⚡⚡ Medium |
| Gemini 3.1 Pro | ⚡ Slow (deeper reasoning may take longer) |

### 5. Gemini `thoughtSignature` echo (BIBIM-006)
Gemini 3.x thinking models attach a `thoughtSignature` on `functionCall` parts and require the client to echo it back when the function call is replayed in conversation history. Missing it returns 400 `Function call is missing a thought_signature`. v1.0.2 captured neither read nor write, so the second turn of a Gemini codegen tool loop always failed.

→ `GeminiProvider` now stashes the signature on the orchestrator's tool_use block (Gemini-private field) and echoes it back on the next request.

### 6. max_tokens truncation + tool_use mishandling (BIBIM-007)
**The biggest fix.** When the model emitted reasoning + tool_use + commentary in a single response and hit the 4096 max_tokens cap, the trailing text was truncated *but the tool_use block was complete*. The orchestrator naively followed the max_tokens branch — it pushed the assistant turn (with tool_use) plus a plain "continue" user message. Both Anthropic and OpenAI strictly require `tool_use → tool_result` pairing in the next message, so the next call returned 400.

→ Two-part fix:
- `max_tokens` raised 4096 → 8192. `max_tokens` is a *ceiling*, not a billed amount — providers charge per actual emitted tokens. Higher cap costs nothing when the model emits less; it just gives reasoning models headroom.
- Whenever the response content contains *any* tool_use block, force `stop_reason="tool_use"` regardless of what the provider reported. Executing the tool produces the required pairing; the model picks up from the tool_result on the next turn even when its trailing commentary was lost.

After this fix, almost every scenario in the test matrix passes.

---

## Provider impact summary

| Provider | v1.0.2 state | v1.0.3 |
|----------|--------------|--------|
| Anthropic Claude | Occasional 400 on complex flows (BIBIM-001 + 007) | Stable |
| OpenAI GPT-5.5 | Planner broke on first call (002) + complex-flow 400 (007) | Stable |
| Google Gemini 3.1 Pro | Planner broken (003) + codegen broken (006) + complex-flow 400 (007) | Stable |

All three providers are now in **production-usable state** — the multi-provider promise from v1.0.2 finally lands.

---

## Auto-migration

When an existing v1.0.2 user launches v1.0.3 for the first time:
- If the saved model id is `gemini-3.1-pro-preview-customtools`, it auto-rewrites to `gemini-3.1-pro-preview` and keeps a `.bak` of the previous `rag_config.json`
- No need to re-pick the model in Settings
- Anthropic / OpenAI users see no migration

The debug log will contain a single line:
```
[ConfigService]: Migrated saved model id 'gemini-3.1-pro-preview-customtools' → 'gemini-3.1-pro-preview' (rewrote rag_config.json).
```

---

## Build / distribution

| Build target | Status |
|-------------|--------|
| Revit 2024 (net48) | ✅ |
| Revit 2025 (net8.0-windows) | ✅ |
| Revit 2026 (net8.0-windows) | ✅ |
| Revit 2027 (net10.0-windows) | ✅ |

---

## Requirements

- Autodesk Revit 2022 or later
- An API key from at least one of:
  - [console.anthropic.com](https://console.anthropic.com/) (Claude)
  - [platform.openai.com/api-keys](https://platform.openai.com/api-keys) (GPT-5.5)
  - [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (Gemini 3.1 Pro)

## Source
[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
