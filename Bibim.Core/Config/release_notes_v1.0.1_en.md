# BIBIM v1.0.1

**Release Date**: 2026-04-21

## What's New

### RAG Re-enabled — Local BM25 Search
RAG (Revit API documentation search) is **back**, and no longer requires a Gemini API key.

Instead of querying a remote Gemini fileSearch store, BIBIM now builds a local BM25 search index directly from `RevitAPI.xml` — the same file Autodesk ships with every Revit installation, and the same source the official API docs render from.

- **No extra setup** — works with your existing Claude API key only
- **Instant after first use** — index is built once on first code generation (~0.5s), then cached for the session
- Covers all Revit domains: core DB, UI, MEP, Structure, IFC (39,770 members, 2,849 searchable chunks)
- Claude now asks clarifying questions before generating Revision Cloud, TextNote, Tag, and other annotation placement code (target view, location, linked Revision)
- Gemini API key input remains in settings — reserved for future use

## Bug Fixes / Improvements
- None in this patch

## Requirements
- Autodesk Revit 2022 or later
- Claude API key ([console.anthropic.com](https://console.anthropic.com/))

## Source
[github.com/SquareZero-Inc/bibim-revit](https://github.com/SquareZero-Inc/bibim-revit)
