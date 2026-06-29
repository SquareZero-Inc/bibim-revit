# Run with a local model (no API key)

BIBIM can drive a language model running entirely on your own machine — **no API key, no per-query cost, and nothing leaves your computer.** It's the easiest way to try BIBIM if you'd rather not sign up for a cloud provider.

BIBIM talks to any server that exposes an **OpenAI-compatible `/v1` API** — including **Ollama, LM Studio, vLLM, and llama.cpp**.

## 1. Run a local server

Pick one:

**Ollama** — install from [ollama.com](https://ollama.com), then pull a capable code model:

```bash
ollama pull <model-name>
```

Ollama exposes an OpenAI-compatible API at `http://localhost:11434/v1`.

**LM Studio** — install from [lmstudio.ai](https://lmstudio.ai), download a model, then start its local server (Developer tab → **Start Server**). It serves at `http://localhost:1234/v1`.

## 2. Point BIBIM at it

1. Open the BIBIM panel → gear icon (⚙) → **Settings**.
2. In the **Local LLM (Self-hosted)** section, enter the **Server URL**, ending in `/v1`:
   - Ollama → `http://localhost:11434/v1`
   - LM Studio → `http://localhost:1234/v1`
3. Leave **Model name** blank to let BIBIM auto-discover it (it queries `/v1/models` and uses the first available model), or type the exact name your server expects.
4. Leave the API-key field blank — local servers don't require one by default. (Only fill it in if you've put your server behind an authenticated proxy or tunnel.)
5. Click **Save**, then pick **Local LLM (Self-hosted)** in the model selector.

## 3. Good to know

- **Quality tracks the model.** A larger, code-capable model produces noticeably better Revit code than a small one.
- **Speed is the trade-off.** A local model is usually slower than a cloud frontier model — the price you pay for zero cost and full privacy.
- **Switch any time.** You can move to a cloud model from Settings without losing your local configuration.
