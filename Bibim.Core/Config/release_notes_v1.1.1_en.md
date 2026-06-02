# BIBIM v1.1.1

**Release date**: 2026-06-03

> **Hotfix release** — three defects surfaced after v1.1.0 shipped. No new features, stability only.

---

## TL;DR

**Three fixes: the CS1704 compile error that blocked every code generation on Revit 2027, a planner routing bug that diverted modeling requests into the model-summary branch, and the runaway update banner that rendered the entire release notes markdown as a 2,000-pixel text wall.**

---

## Bug fixes

### 1. Revit 2027 — code generation unblocked

- **Symptom**: Every code generation attempt under Revit 2027 failed with `CS1704: An assembly with the same simple name 'Autodesk.Http' has already been imported`.
- **Root cause**: Revit 2027 loads `Autodesk.Http.dll` from two paths simultaneously — the main install directory AND the new `AddIns\ExtendedAPIs\` subfolder. `RoslynCompilerService` deduped by file path, so both copies entered the reference list and Roslyn rejected the compile. Revit 2024–2026 don't have the `ExtendedAPIs` folder, so they're unaffected.
- **Fix**: `BuildBaseReferences()` now deduplicates by simple assembly name, with the non-`ExtendedAPIs` path preferred when duplicates exist.
- **Affected users**: **100% of Revit 2027 users**.

### 2. Modeling-request false-positive routing

- **Symptom**: Requests like "make me a wall" / "model this CAD plan" were misrouted into the *model-summary* branch — the agent only described the model and never generated code. Users had to follow up with a second message ("yes", "proceed with modeling") before BIBIM actually built anything.
- **Root cause**: `IsBuiltInCurrentContextSummaryTaskV2()` returned `true` whenever the task search text contained both a "current view" keyword AND a "describe/analyze" keyword. It had no negative gate for WRITE intent. Q&A answers or planner-paraphrased task steps could incidentally introduce these keywords and trigger the false positive.
- **Fix**: Added a WRITE-verb negative gate (~30 keywords incl. 만들/생성/배치/모델링/수정/내보내/create/place/add/build/edit/delete/move/copy/rotate/export). When any WRITE verb is detected the summary route is skipped.
- **Effect**: Code generation now proceeds on the first attempt.

### 3. Update banner overflow

- **Symptom**: When a new version was available, the alert bar rendered the entire release notes markdown body as a single ~2,000-pixel purple text wall, with the action buttons wrapping awkwardly into a vertical column.
- **Root cause**: `VersionChecker` shipped the full GitHub release body (~9 KB of markdown) verbatim as `ReleaseNotes`. `App.tsx` placed it directly into a single `<span>` with no length cap, no markdown rendering, no CSS clamp.
- **Fix**:
  - Backend: `ExtractReleaseHeadline()` returns just the first meaningful single-line headline (capped at 140 chars). A separate `ReleaseNotesUrl` field carries the GitHub release page URL.
  - Frontend: alert bar now has `maxHeight: 44` + single-line ellipsis. A new **"View notes ↗"** link opens the full release page in the external browser.
- **Effect**: All users — every future update notification renders correctly.

---

## Automatic migration

None required — no data or config changes. Update and you're done.

---

## Builds

| Target | Status |
|--------|--------|
| Revit 2024 (net48) | ✅ |
| Revit 2025 (net8.0-windows) | ✅ |
| Revit 2026 (net8.0-windows) | ✅ |
| Revit 2027 (net10.0-windows) | ✅ |

## Source

[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
