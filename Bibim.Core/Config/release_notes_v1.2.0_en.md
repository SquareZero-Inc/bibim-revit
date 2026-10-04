# BIBIM v1.2.0

**Release date**: 2026-10-04

> **Major feature release** — latest model generation, Gemini removed, honesty/cost/speed overhaul of the agent harness, stronger security, .NET 10 readiness.

---

## TL;DR

**Pick Claude Sonnet 5 / Opus 5.5 / Fable 5.1 or GPT-6, and BIBIM stops pretending. Impossible requests are declined up front, applied changes are reported from measured facts, and code generation shows what it is doing in real time. Simple lookups are answered in seconds without writing code.**

---

## ⭐ New features

- **Attach a document**: the paperclip button (or drag & drop) attaches one `.md` / `.txt` / `.csv` file to a message, e.g. a model-check result table plus "fix the violating elements in this document". Tables with an ElementId/UniqueId column are parsed into a target list: you get a "N target elements, rule X" digest before anything runs, and preview/apply results carry a "0 changed outside the list" check. The document body is sent only with the AI request; it is never stored in the session history or logs.
- **Run Again**: a finished task card can re-preview its own code with the current model and selection — no need to start over after running with nothing selected.

## 🐛 Fixes

- **False version warnings removed**: `Element.LevelId` and `Parameter.AsInteger()` were flagged (and auto-rewritten) as "removed in Revit 2024" although both still exist in 2024 and 2026. `ElementId.IntegerValue` is now a warning on 2024 and an error on 2026, as in the API. The auto-fix that broke workset code (`WorksetId.IntegerValue`) is fixed.
- **Fewer compile errors in generated code**: `System.IO` and `System.Text` are imported by default (`Path`, `File`, `StringBuilder`).
- **Missing Apply / Run button**: a long result no longer pushes the input and the buttons off-screen (the task card scrolls inside itself). Write requests such as "create dimensions" no longer fall into the built-in model summary and finish with nothing done, and model-changing tasks can no longer be classified as read-only (which hid Apply).
- **Ramps**: the Revit API cannot create ramps. Instead of faking one with an uneditable DirectShape, BIBIM now says so and offers a **sloped floor with a slope arrow** of the same size.
- **Copying grid 2D settings between views**: extents, end positions and 2D/3D state are now applied (`PropagateToViews`), not only the bubbles.
- **Odd-dimension audit**: round values such as 2345.000 were treated as odd and thousands of dimensions were created. BIBIM now asks once for the allowed precision, checks adjacent parallel elements only, and reports instead of creating more than 200. Previews also warn when 200+ new elements would be created.
- **Installer signing**: fixed a build issue where the DLLs inside the installer were unsigned or signed with the wrong certificate, which made Revit show an "Invalid Signature" prompt.

## ⭐ Models

| Provider | Model | Notes |
|---|---|---|
| Anthropic | **Claude Sonnet 5** (new default, recommended) | Fast, low cost, strong agentic coding |
| Anthropic | Claude Opus 5.5 · Opus 5 · Fable 5.1 (new) | Complex multi-step work. Fable 5.1 = most capable, highest cost |
| Anthropic | Claude Sonnet 4.6 · Opus 4.7 | Previous generation, still selectable |
| OpenAI | GPT-6 Sol · GPT-6 Luna · GPT-5.6 Sol · GPT-5.6 Terra (new) | Luna is an ultra-low-cost option for simple lookups |
| OpenAI | GPT-5.5 | Previous generation, still selectable |

- **Gemini removed**: its only Pro model was a preview that Google began pulling from AI Studio. If you had Gemini selected, the update switches you to Claude Sonnet 5 automatically (a `.bak` of your config is kept).
- The new Claude models **think by default**. Models with safety classifiers (Opus 5 / 5.5, Fable 5.1) automatically retry a false-positive refusal on a fallback model server-side.

## ⭐ Harness: no more "looks done but isn't"

- **Impossible requests declined immediately**: ribbon buttons, panel UI, controlling other add-ins and similar in-session-impossible work get an instant reason plus alternative — no multi-minute code generation that fails anyway.
- **Post-commit verification**: after Apply, the completion message carries a **[Verification]** block — measured element changes (added / modified / deleted), the difference from the preview's prediction, and whether files the code reported actually exist on disk. A model-changing task that changed nothing is flagged with ⚠.
- **Measured preview wording**: "Preview completed — N element(s) would be changed" / "no model elements would change (normal for file or view tasks)".
- **Structured task type**: the planner emits a task category (query / export / model edit / create / delete / view-selection / annotation), replacing keyword guesses that misjudged e.g. exports as "0 elements changed — retry".
- **Safe halt on planner failure**: a model-changing request is never run unplanned when the planning step fails.
- **No cross-task code mix-ups**: Apply on task A's card always runs task A's code; tasks bind to the document they were created in.
- **Selection preserved between preview and apply**.

## ⭐ Speed & visibility

- **Code Library reuse**: for similar tasks the model searches your saved snippets first and adapts proven code (same compile / preview / Apply checks).
- **Simple lookups without code**: "how many doors on L2?" is answered straight from the new read tools (`count_elements`, `list_elements`). Anything that needs computation (sums, areas) still gets code as before.
- **Live code-generation progress**: the banner shows one line of what the model is reasoning about or writing, plus a "(3/15)" step counter — minute-long waits no longer look frozen.
- "Analyzing your request..." banner during planning, localized statuses, tray balloon only for questions, errors and preview-ready.

## Cost

- **1-hour prompt cache** for the system prompt and tool definitions; 5-minute cache for the conversation tail. Long dry-runs or a short break no longer drop the cache.
- **Stepped history window**: the conversation prefix stays byte-identical across turns instead of shifting (and re-billing) every turn.
- **Per-route effort**: planner low · chat medium · code generation high (tunable via the `llm` block in rag_config.json).
- Token figures now consistently include cached input.

## Usability

- **Korean IME Enter fix** in every input field.
- **Markdown rendering**, C# syntax highlighting, copy button on messages.
- **Readable errors**: actionable messages ("check your API key") instead of raw `Anthropic API 401: {json}`.
- Question cards no longer hide the chat input ("answer in chat" escape), side drawers overlay on narrow docks, scroll position respected, two-step task cancel, automatic recovery from a stuck busy state.

## Security & audit

- **API keys encrypted at rest** with Windows DPAPI (current user). Existing plaintext keys are encrypted on first launch.
- **Local audit log**: every apply and undo is appended to `%APPDATA%\BIBIM\audit\audit_YYYYMM.jsonl` (document, model, affected element ids, code hash, verification, risk). Nothing is uploaded.

## Platform

- **Ready for Revit 2025.5 / 2026.5 on .NET 10**: removed the unused Anthropic SDK and Markdig packages to reduce assembly-conflict surface in the host.
- Revit 2027 DLLs are now code-signed too.

---

## Upgrade notes

- **Automatic migrations**: Gemini selection → Claude Sonnet 5, plaintext keys → encrypted (both with a `rag_config.json.bak` backup).
- **Downgrading**: keys encrypted by v1.2.0 cannot be read by older versions — re-enter them in Settings after a downgrade.
- **Copying the config to another PC**: encrypted keys do not decrypt there (shown as not configured) — re-enter them on the new machine.

## Build

| Target | Result |
|------|------|
| Revit 2024 (net48) | ✅ |
| Revit 2025 / 2026 (net8.0-windows) | ✅ |
| Revit 2027 (net10.0-windows) | ✅ |
| Unit tests | 146 / 146 passing |

## Source

[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
