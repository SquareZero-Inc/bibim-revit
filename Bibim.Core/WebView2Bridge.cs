// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Concurrent;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bibim.Core
{
    /// <summary>
    /// Bidirectional messaging bridge between C# and WebView2 (React frontend).
    /// 
    /// C# → JS:  bridge.PostMessage(type, payload)  →  window.bibim.onMessage(type, payload)
    /// JS → C#:  window.chrome.webview.postMessage({type, payload})  →  OnMessageReceived event
    /// 
    /// See design doc §1 — WebView2 Bridge layer.
    /// </summary>
    public class WebView2Bridge : IDisposable
    {
        private readonly WebView2 _webView;
        private readonly ConcurrentDictionary<string, Action<string>> _handlers
            = new ConcurrentDictionary<string, Action<string>>();

        /// <summary>
        /// Fired when the JS frontend sends a message to C#.
        /// </summary>
        public event EventHandler<BridgeMessage> MessageReceived;

        public WebView2Bridge(WebView2 webView)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
        }

        public void Initialize()
        {
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Inject the bridge API into the JS context.
            // _handlers stores arrays so multiple hooks can subscribe to the same event type.
            _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                window.bibim = window.bibim || {};
                window.bibim._handlers = {};
                window.bibim.on = function(type, handler) {
                    if (!window.bibim._handlers[type]) window.bibim._handlers[type] = [];
                    window.bibim._handlers[type].push(handler);
                };
                window.bibim.onMessage = function(type, payload) {
                    var hs = window.bibim._handlers[type];
                    if (hs) hs.forEach(function(h) { h(payload); });
                };
                window.bibim.send = function(type, payload) {
                    window.chrome.webview.postMessage({ type: type, payload: payload });
                };
            ");

            Logger.Log("WebView2Bridge", "Bridge initialized");
        }

        /// <summary>
        /// Send a message from C# to the React frontend.
        /// Uses ExecuteScriptAsync to call window.bibim.onMessage directly.
        /// Thread-safe: automatically marshals to WPF UI thread.
        /// </summary>
        public void PostMessage(string type, object payload = null)
        {
            if (_isDisposed) return;
            try
            {
                string payloadJson = JsonHelper.SerializeCamelCase(payload);
                // Escape for embedding in JS string
                string escapedType = type.Replace("\\", "\\\\").Replace("'", "\\'");
                string script = $"if(window.bibim && window.bibim.onMessage) window.bibim.onMessage('{escapedType}', {payloadJson});";

                if (_webView.Dispatcher.CheckAccess())
                {
                    _webView.CoreWebView2.ExecuteScriptAsync(script);
                }
                else
                {
                    _webView.Dispatcher.Invoke(() =>
                    {
                        _webView.CoreWebView2.ExecuteScriptAsync(script);
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("WebView2Bridge.PostMessage", ex);
            }
        }

        /// <summary>
        /// Register a C# handler for a specific message type from JS.
        /// </summary>
        public void On(string type, Action<string> handler)
        {
            _handlers[type] = handler;
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                // WebMessageAsJson returns proper JSON when postMessage receives an object (not a string)
                string raw = e.WebMessageAsJson;
                // Mask password field in logs to avoid plain-text credential exposure
                string logSafe = raw;
                if (logSafe != null && logSafe.Contains("\"password\""))
                    logSafe = System.Text.RegularExpressions.Regex.Replace(
                        logSafe, "\"password\"\\s*:\\s*\"[^\"]*\"", "\"password\":\"***\"");
                Logger.Log("WebView2Bridge", $"Received: {logSafe?.Substring(0, Math.Min(logSafe?.Length ?? 0, 200))}");

                var msg = JsonHelper.Deserialize<BridgeMessage>(raw);
                if (msg == null) return;

                Logger.Log("WebView2Bridge", $"Parsed: type={msg.Type}");

                // Dispatch to registered handler
                if (_handlers.TryGetValue(msg.Type, out var handler))
                {
                    handler(msg.Payload?.ToString());
                }

                // Also fire the general event
                MessageReceived?.Invoke(this, msg);
            }
            catch (Exception ex)
            {
                Logger.LogError("WebView2Bridge.OnWebMessageReceived", ex);
            }
        }

        private volatile bool _isDisposed;

        public void Dispose()
        {
            _isDisposed = true;
            try
            {
                if (_webView?.CoreWebView2 != null)
                    _webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }
            catch { }
        }
    }

    /// <summary>
    /// Message structure for WebView2 bridge communication.
    /// </summary>
    public class BridgeMessage
    {
        public string Type { get; set; }
        public Newtonsoft.Json.Linq.JToken Payload { get; set; }
    }
}
