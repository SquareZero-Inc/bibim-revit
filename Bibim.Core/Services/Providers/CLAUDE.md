# Providers — LLM Adapters

## Purpose
Convert between the orchestrator's canonical **Anthropic-shaped `JArray`** messages (the internal contract — defined in the root CLAUDE.md → Architecture) and each vendor's native wire shape. The orchestrator in `../LlmOrchestrationService.cs` never sees a native shape — keep it that way.

## Files
`ILlmProvider.cs` (interface: `SendNonStreamingAsync` for the tool loop, `SendStreamingAsync` for chat — both take canonical messages, `bool jsonMode`, `maxTokens`) · `AnthropicProvider.cs` · `OpenAIProvider.cs` · `GeminiProvider.cs` · `LocalProvider.cs` · `LlmProviderFactory.cs`.

## Routing (`LlmProviderFactory.ResolveProviderForModel`)
| model-id | provider |
|----------|----------|
| `claude-*` | anthropic |
| `gpt-*`, `o3*`, `o4*` | openai |
| `gemini-*` | gemini |
| `local` (single canonical id) | local |
| legacy OSS prefixes (`google/gemma-`, `meta-llama/`, `mistralai/`, `qwen/`, `nvidia/`, `kwaipilot/`) | local — migrated to `"local"` on next load |

No `selected_provider` field exists — prefix is the only signal. `local` requires a `baseUrl` (from `ConfigService` `LocalServerUrl`).

## Per-provider conversion
- **Anthropic** (`/v1/messages`): closest to canonical. Prompt caching wired here — `cache_control: {type: ephemeral}` on the system block + `MarkLastToolForCaching(tools)` (5-min TTL). `jsonMode` accepted but unused (Claude obeys prompt instructions).
- **OpenAI** (`/v1/responses`, Responses API): `tool_use` → `function_call`, `tool_result` → `function_call_output`; tool-call arguments come back as a **stringified JSON** string — parse via `JObject`. JSON mode = `text.format.type = "json_object"`.
- **Gemini** (`generateContent`, v1beta): `tool_use` → `functionCall` parts; Gemini returns **no call id**, so the adapter mints a synthetic `gemini_call_{guid}` (24 chars). JSON mode = `generationConfig.responseMimeType = "application/json"`.
- **Local** (`/v1/chat/completions`): OpenAI-compatible servers (Ollama, LM Studio, vLLM, llama.cpp). `baseUrl` is mandatory (e.g. `http://localhost:11434/v1`); the server-side `model` string is resolved lazily (`ConfigService.LocalModelName`, else a `/v1/models` probe). Gating is by URL presence, not credentials; an optional bearer key is supported.

## Multi-provider hotfixes (caught in real-user testing — keep them)
- **BIBIM-001** — Anthropic 400 on the tool loop. The orchestrator stamps a `name` onto every `tool_result` block (the Gemini adapter's `functionResponse` mapping needs it), but Anthropic's strict validator rejects unknown fields. Fix: `AnthropicProvider.SendNonStreamingAsync` strips `tool_result` → `name` per message before sending (`AnthropicProvider.cs:~84`). Provider-local defence — don't move it upstream or Gemini loses the field.
- **BIBIM-002** — OpenAI 400 on the planner. The Responses API rejects `json_object` mode unless an **input message** contains the literal word "json" (instructions-only doesn't count). Satisfied by `BuildPlannerInput` (in `../../BibimDockablePanelProvider.cs`) ending with `[Output format: respond with JSON only — no markdown, no commentary.]`.
- **BIBIM-003** — Gemini non-JSON planner output. The `-customtools` variant ignores `responseMimeType` when no tools are sent. Fix: use vanilla `gemini-3.1-pro-preview`; `ConfigService.LoadRagConfig` auto-migrates the stored id (`.bak` backup), and `ExtractJsonObject` strips ```json fences as a secondary defence.

## Adding a model
Three places must agree, or routing/UI breaks:
1. `LlmProviderFactory.ResolveProviderForModel` (prefix → provider)
2. `../../Common/ConfigService.cs` `AvailableModels` (the `(Id, Label, Provider)` tuple list)
3. `../../frontend/src/components/SettingsPanel.tsx` `MODELS` array (see frontend CLAUDE.md)

## Do not
- Leak a native provider shape back to the orchestrator — convert at the boundary.
- Use official SDKs for streaming/tool calls — these are deliberately raw HTTP + SSE (the `Anthropic` NuGet pkg is present but the tool loop goes through this adapter).
