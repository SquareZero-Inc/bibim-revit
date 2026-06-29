# frontend — WebView2 SPA

## Purpose
The React/TS UI rendered inside the Revit dockable panel's WebView2. React 19 · Vite 6 · TS 5.7 (`strict`). Talks to the C# backend only through the injected `window.bibim` bridge.

## Entry & build
- `index.html` → `src/main.tsx` → `src/App.tsx` (layout: ChatPanel + History/CodeLibrary/Settings panels).
- `npm run build` = `tsc && vite build`. Vite writes to **`../wwwroot`** (`outDir`, `emptyOutDir: true`) with `base: './'` so assets resolve under the `file://`-style WebView host. `../wwwroot` is committed build output — regenerate it, never hand-edit.
- `npm run dev` = Vite HMR (browser, no Revit); `npm run preview` serves the built output.

## Bridge contract (`src/bridge.ts`)
- `window.bibim` is injected by `../WebView2Bridge.cs` — exposes `.send(type, payload)` / `.on(type, handler)`. `isWebView2()` checks `window.chrome?.webview?.postMessage`.
- Use the wrappers, not `window.bibim` directly: `sendToBackend(type, payload?)`, `onBackendMessage(type, handler)`.
- Message `type` strings are **kebab-case** and must match the C# handler names registered in `../BibimDockablePanelProvider.cs` (`RegisterAsyncHandler`). Outbound examples: `question_answers`, `execute`, `task_action`, `save_api_key`. Inbound: `streaming_delta` (text chunk), `streaming_end` (final `{text, csharpCode, type, tokens, elapsedMs}`), `system_message`, `revit_warning`, `task_state`.

## State (`src/hooks/`)
- `useChat.ts` — the main hook: messages, streaming buffer (transient id `__streaming__` replaced on `streaming_end`), sessions, task flow, and per-provider key UI state (`{anthropic|openai|gemini|local}Configured/Masked/SaveResult`). Uses `useRef` request-locks to block concurrent sends; emits token-usage on completion.
- `useAppInfo.ts` (version/update/language) and `useCodeLibrary.ts` (snippets) are domain sub-hooks called by `useChat`.

## i18n (`src/i18n.ts`)
- Hardcoded `STRINGS = { kr: {…}, en: {…} }` (~400 keys each); `TranslationKey = keyof typeof STRINGS.kr`; access via `t(key)`. **Default language is `kr`.** No i18n library, no CDN.
- Separate from `../Config/i18n/{en,kr}.json`, which holds **C#-side / installer** strings — keep both in sync when adding user-facing copy.

## Model selector (`src/components/SettingsPanel.tsx`)
The `MODELS` array (`{id, label, cost, speed, provider, recommended?}`) is the UI source of truth for the picker — speed glyphs ⚡⚡⚡/⚡⚡/⚡ are display-only. Its `id`s must match backend `ConfigService.AvailableModels` and the factory routing (see `../Services/Providers/CLAUDE.md`).

## Conventions
- TS `strict` + `noUnusedLocals`, `noUnusedParameters`, `noFallthroughCasesInSwitch`, `isolatedModules`. No ESLint/Prettier/Biome config — `tsc` is the gate.
- **Export style is mixed**: components (`*.tsx`) use `export default`; utilities/hooks (`bridge.ts`, `i18n.ts`, `use*.ts`) use **named** exports. Follow the neighbour.
- Functional components + hooks only; props typed inline as `interface Props`. Styling via CSS custom properties in `src/tokens.css` + `src/global.css` — no Tailwind, no styled-components.
- Components are message-type dispatchers: `ChatMessage.tsx` renders differently per `type` (`feedback_request`, `revit_warning`, `code`, …).

## Do not
- Hand-edit `../wwwroot`. Add a model in only one of the three sync points and forget the others. Reach into `window.bibim` directly instead of the `bridge.ts` wrappers.
