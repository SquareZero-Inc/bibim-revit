# Getting started

BIBIM turns plain language into Revit actions — you describe the task, and BIBIM writes, checks, and runs the C# for you. This guide takes you from zero to your first successful command.

## 1. Install

1. Download the latest installer from the [Releases page](https://github.com/SquareZero-Inc/bibim-revit/releases) (pick the `_EN` build for English, `_kr` for Korean).
2. Close Revit, then run the installer. It automatically registers BIBIM for every Revit version it detects (2022–2027).
3. Open Revit. A **BIBIM AI** tab appears in the ribbon — click it to open the panel.

## 2. Choose how to power it

BIBIM is **Bring Your Own Key (BYOK)**: it talks directly to a language model using *your* credentials — no subscription, and nothing is sent to us. You have three ways to power it, from free to premium.

### Option A — Run a local model · no API key, no cost

Don't want to deal with API keys at all? Point BIBIM at a model running on your own machine.

- Works with Ollama, LM Studio, vLLM, llama.cpp — anything serving an OpenAI-compatible endpoint.
- No key, no per-query cost, and nothing leaves your computer.
- Full steps: **[Run with a local model](./local-llm)**.

### Option B — Google Gemini · cheapest cloud, free tier to start

- Get a key at [aistudio.google.com/apikey](https://aistudio.google.com/apikey). Google AI Studio has a free tier you can start on.

### Option C — Claude or GPT · best quality

- Recommended default: **Claude Sonnet 4.6**. Also available: Claude Opus 4.7, OpenAI GPT-5.5.
- Step-by-step key issuance (account → billing → key): **[Anthropic / OpenAI key setup guide](https://squarezero.notion.site/BIBIM-API-Key-Setup-Guide-34fa486f5e6d805a8b6af067ea7024a5)**.
- A typical request costs only a few cents. Anthropic's $5 minimum top-up covers hundreds of everyday queries, and the Settings panel shows a live cost estimate per model.

::: warning Keep your key safe
Treat any API key like a password — never share it or commit it to source control.
:::

## 3. Enter your credentials and pick a model

1. In the BIBIM panel, click the gear icon (⚙) to open **Settings**.
2. Paste your key into the matching provider field — or, for a local model, enter your server URL (see [Run with a local model](./local-llm)). Click **Save**.
3. Pick a model from the selector. Models without a configured key are greyed out, with a tooltip telling you which key to add.

## 4. Your first command

Type a request in plain language, for example:

> Select all doors on Level 1 and rename them sequentially.

BIBIM will:

- break the task into steps and ask any clarifying questions,
- generate and validate the C# — a Roslyn safety gate runs before anything touches your model,
- show you a preview (use **Dry-run** to inspect the affected elements first),
- run it — and let you **Undo** any applied change with one click.

That's your first BIBIM command. To go fully key-free, read **[Run with a local model](./local-llm)** next.
