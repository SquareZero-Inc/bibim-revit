# BIBIM v1.0.2

**Release Date**: 2026-04-28

## What's New

### Multi-Provider LLM Support (BYOK)
You can now choose any of **4 models** in Settings — pick the one that fits your account and budget.

| Model | Provider | Sample cost / request | Notes |
|-------|----------|----------------------|-------|
| **Claude Sonnet 4.6** ⭐ | Anthropic | ~$0.04 | Recommended balance |
| Claude Opus 4.7 | Anthropic | ~$0.20 | Top quality, agentic tasks |
| GPT-5.5 | OpenAI | ~$0.08 | Multilingual, strong tooling |
| Gemini 3.1 Pro | Google | ~$0.03 | Lowest cost, biggest context |

- Each provider has its own key field in Settings (Anthropic / OpenAI / Google).
- Models without a registered key are greyed out with a tooltip that tells you which key to add.
- Adding a new key activates that provider's models instantly — no restart needed.
- Existing 1.0.1 users: your saved Anthropic key migrates automatically. A `.bak` of the previous config is kept next to it.

### API Key Setup Guide Link
A new **"📖 View API Key Setup Guide"** button at the top of Settings opens a step-by-step Notion guide for getting keys from Anthropic / OpenAI / Google. The link is language-aware (KR or EN).

### 30–40% Token Reduction
The code-generation pipeline is now substantially leaner — same task, fewer tokens. Typical sessions see **~30–40% input-token reduction**, and heavy users (100+ requests/month) save **35–42% on cost**.

Highlights:
- **Anthropic prompt caching enabled**: `cache_control: ephemeral` markers on the system prompt + tool definitions. Within the 5-minute cache window, the prefix portion is billed at the cached rate (`$0.30/1M` — 90% off).
- **Cache-effectiveness telemetry**: `cache_read_input_tokens` and `cache_creation_input_tokens` are now tracked per call and per session in `bibim_v3_debug.txt`, including a session cache hit ratio.
- **Roslyn compile retry pruning**: previously failed code attempts no longer accumulate across retries. Saves ~700 tokens per retry round.
- **Local RAG slimmed**: `search_revit_api` result verbosity reduced (TopK 5→3, chunk 3000→1200 chars, members 60→30). No effect on code-generation quality.
- **Heuristic planner gate**: short greetings/acks ("hi", "ok", "thanks") now skip the planner LLM call entirely — saves ~2,500 tokens per skip.
- **Category question templates extracted**: the planner's ~1,800-token category-question library is now a compact checklist.
- **Long-session stabilisation**: chat history window 20→10 turns, with dropped turns collapsed into a synthetic recap. Context-length errors down ~70%.
- **Context tools shipped on demand**: `get_view_info`, `get_selected_elements`, `get_element_parameters`, `get_family_types`, `get_project_levels` are only included when the user message hints at relevant context.
- **FileOutputRules conditional**: the ~700-token file-safety block is only attached to actual file-output tasks (PDF/DWG/CSV/etc.), not to in-model edits.

### LLM Reliability Improvements
- **Gemini JSON-mode for the planner**: `responseMimeType: "application/json"` is now enforced on Gemini planner calls. Fixes a regression where Gemini's invalid JSON output caused the assistant to dump raw C# code into the chat bubble instead of running the structured task flow.
- **Planner parse-failure retry**: if the planner returns malformed JSON, BIBIM retries once with an explicit "your previous response was not valid JSON" nudge. Both attempts must fail before falling back to direct chat.
- **GPT selection-priority rule**: when the user uses pointing language ("these doors", "the selection", "이 도어들"), GPT is now explicitly told to use `uidoc.Selection.GetElementIds()` instead of falling back to a model-wide `FilteredElementCollector`. Claude already followed this pattern.

### Multi-provider hotfixes (caught in real-user testing)
Three latent bugs introduced during the multi-provider work — all three providers were dropping the task → question → codegen flow. Fixed in one go:
- **Anthropic 400 fix**: `tool_result` blocks carried a `name` field (kept for the Gemini adapter), which Anthropic's strict schema validator rejected, killing the tool loop on the second call onward. `AnthropicProvider` now strips the field before send.
- **OpenAI 400 fix**: OpenAI's Responses API rejects `text.format=json_object` mode unless an input message contains the literal word "json". Added a one-line `[Output format: respond with JSON only…]` directive at the end of the planner user message.
- **Gemini model swap**: `gemini-3.1-pro-preview-customtools` (an agent-and-tool-first variant) → `gemini-3.1-pro-preview` (vanilla). The customtools variant prioritises function-calling output and silently ignored JSON mode in the planner case. Google's own guidance: use vanilla when <50% of requests involve tool calling.
- **Auto-migration**: existing configs holding the old `-customtools` model id are rewritten to vanilla on next launch (`.bak` backup kept). No manual re-selection in Settings required.

### Model selector UX
Each model in the Settings selector now shows a **response-speed indicator (⚡)** so you can compare at a glance:
- ⚡⚡⚡ Claude Sonnet 4.6 (fast)
- ⚡⚡ Claude Opus 4.7, GPT-5.5 (medium)
- ⚡ Gemini 3.1 Pro (slow — deeper reasoning may take longer)

Hover the ⚡ icon for a localised tooltip.

### Loading-State Bug Fix
A 429 / network / billing error from the LLM provider used to leave the chat panel stuck on "Generating response…" forever. Fixed: the progress UI now always clears on error so you can try again or switch models immediately.

## Bug Fixes / Improvements
- Frontend `Saved` toast now reports the correct provider (Anthropic / OpenAI / Gemini) when multiple keys are saved in sequence.
- Settings panel scrolls when content overflows on smaller monitors.
- Cleaner log lines: provider + model + cache hit ratio are now stamped on every LLM call.
- Streaming chat path no longer drops `cache_creation_input_tokens` (first-message cache-write cost was previously logged as 0).
- Removed unused `GeminiRagService` dead code (local BM25 RAG has been the only RAG since v1.0.1).

## Requirements
- Autodesk Revit 2022 or later
- An API key from at least one of:
  - [console.anthropic.com](https://console.anthropic.com/) (Claude)
  - [platform.openai.com/api-keys](https://platform.openai.com/api-keys) (GPT)
  - [aistudio.google.com/apikey](https://aistudio.google.com/apikey) (Gemini)

## Source
[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
