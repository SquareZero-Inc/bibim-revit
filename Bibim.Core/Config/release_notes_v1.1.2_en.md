# BIBIM v1.1.2

**Release date**: 2026-06-28

> **Feature release** — introduces self-correction, sharpens planner accuracy, cleans up conversation-flow UX, and closes one security gap.

---

## TL;DR

**BIBIM now actually runs the code it generates and, when a runtime exception or a zero-element result occurs, reads the cause and regenerates on its own. On top of that: the planner no longer mistakes "set a parameter value" for "create a parameter," model identifiers are detected deterministically, the conversation flow is tidied up, and API keys no longer leak in plaintext to the debug log.**

---

## ⭐ New: Self-Correction

Previously, code that merely **compiled** was accepted. Now, after a successful compile, BIBIM dry-runs the code to verify it actually executes.

- **Runtime exception** → the exception is fed back to the LLM and the code is **regenerated automatically**
- **Zero-element result** (e.g. nothing matched) → same: cause is fed back and the code regenerated
- **Regeneration is capped at one retry**, and large changes (>500 affected elements) are blocked by a **scale guard** to prevent runaway edits
- Same structure as the existing compile-error retry loop — only the last failure is fed back, no token waste

→ Tasks that used to fail silently at runtime now get one automatic correction pass with no user intervention.

## Planner accuracy

- **Parameter value-edits classified correctly as EDIT**: requests like "set Comments to reviewed" no longer trigger the **four parameter-creation questions** (binding / data type / group / instance-vs-type). Setting a value never routes to creation.
- **Automatic model-identifier detection**: Level / Sheet / Phase / Workset are extracted deterministically and injected into the planner, reducing name-matching mistakes.

## Conversation-flow UX

- Cleaned-up Q&A echo, removed an unnecessary confirmation gate, improved labels and feedback messages (9 items)
- Graphic-verb gate, first-run welcome guide, elapsed-time timer, expanded i18n

## Security

- **API keys masked in logs**: when a key was saved, every provider key (anthropic / openai / gemini / local) in the payload was being written to the debug log in plaintext — now masked.

## Installer

- **No more missing versions**: the build guard now checks for `RevitAPI.dll` rather than just the folder — a leftover stub folder from an uninstalled Revit no longer fails the entire build.
- **Install path separation**: new installs go to `...\BIBIM AI\Revit` (coexists with other BIBIM products).
- Refreshed installer messages (supported versions 2022–2027 stated, multi-provider guidance).

---

## Automatic migration

None required — no config or data changes. Update and you're done.

## Builds

| Target | Status |
|--------|--------|
| Revit 2024 (net48) | ✅ |
| Revit 2025 (net8.0-windows) | ✅ |
| Revit 2026 (net8.0-windows) | ✅ |
| Revit 2027 (net10.0-windows) | ✅ |

## Source

[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
