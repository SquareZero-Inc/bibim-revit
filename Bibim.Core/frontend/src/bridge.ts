/**
 * WebView2 Bridge — TypeScript side.
 * Communicates with C# via window.bibim (injected by WebView2Bridge.cs).
 */

interface BibimBridge {
  send: (type: string, payload?: unknown) => void;
  on: (type: string, handler: (payload: unknown) => void) => void;
  onMessage: (type: string, payload: unknown) => void;
  _handlers: Record<string, (payload: unknown) => void>;
}

declare global {
  interface Window {
    bibim: BibimBridge;
    chrome: {
      webview: {
        postMessage: (msg: string) => void;
      };
    };
  }
}

/** Send a message to C# backend */
export function sendToBackend(type: string, payload?: unknown): void {
  if (window.bibim?.send) {
    window.bibim.send(type, payload);
  } else {
    console.warn('[Bridge] Not in WebView2 context, message dropped:', type);
  }
}

/** Register a handler for messages from C# backend */
export function onBackendMessage(type: string, handler: (payload: unknown) => void): void {
  if (window.bibim?.on) {
    window.bibim.on(type, handler);
  }
}

/** Check if running inside WebView2 */
export function isWebView2(): boolean {
  return typeof window.chrome?.webview?.postMessage === 'function';
}
