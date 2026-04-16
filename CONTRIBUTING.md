# Contributing to BIBIM AI

We built this open source to eliminate the repetitive manual work that BIM engineers deal with every day. Bug reports, feedback, and pull requests are all welcome.

---

## Bug Reports

Search existing issues before filing. If you're reporting something new, include all of the following:

1. **Revit version and BIBIM version**
2. **The prompt that triggered the issue** — paste it verbatim
3. **Debug log** (required): `%USERPROFILE%\bibim_v3_debug.txt`
4. **Codegen output folder** (required): `%AppData%\BIBIM\debug\codegen\YYYYMMDD\`
   — redact any sensitive project data (drawing numbers, internal specs) before attaching

Issues filed without logs may be deprioritized or closed.

If BIBIM crashes on startup, the Revit journal log is usually more useful than the debug log:
```
%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit <year>\Journals\
```

---

## Feature Requests

Before proposing something, ask yourself: is this a **generic BIM workflow** or a **company-specific standard**?

- **Generic BIM workflow** (e.g. "filter elements by shared parameter value", "rename views by sheet"): open a GitHub Issue with the `enhancement` label.
- **Company-specific customization** (e.g. your firm's drawing number format, internal spec templates, custom RAG corpus): this repo is not the right place. Contact us at **seokwoo.hong@sqzr.team** for enterprise options.

This distinction matters — PRs that hardcode company-specific logic into core behavior won't be merged.

---

## Pull Requests

1. Fork the repo
2. Create a branch: `git checkout -b fix/your-bug` or `feature/your-feature`
3. Make your changes and test against at least one Revit version
4. Run the build: `dotnet build "Bibim.Core/Bibim.Core.csproj" -c R2026 -p:TargetFramework=net8.0-windows`
5. Run tests: `dotnet test Bibim.Core.Tests/`
6. Commit and push, then open a PR against `main`

### Commit format

```
<type>: <subject>
```

Types: `feat` `fix` `refactor` `docs` `chore` `test`

### What gets merged

- Bug fixes with a clear description of the problem and how it's verified
- New Revit API tools in `BibimToolService.cs` that cover broadly useful operations
- Prompt and planner improvements with a before/after explanation

### What doesn't get merged

- Changes to the BYOK model (no subscriptions, no telemetry, no bundled keys)
- Architecture changes without a prior issue discussion
- Large unfocused PRs — split them

For anything that significantly touches the core architecture or the LLM tool loop, open an issue first and describe the change. It's easier to align before the code is written.

---

## Prerequisites

| Tool | Requirement |
|------|-------------|
| Autodesk Revit | 2022 or later (at least one version) |
| .NET SDK | 4.8 (R2022–R2024), 8.0 (R2025–R2026), 10.0 (R2027) |
| Node.js | 20+ (frontend panel only) |
| PowerShell | 7+ (full build script) |

---

## License

By contributing, you agree that your contributions will be licensed under the [Apache 2.0 License](LICENSE).
