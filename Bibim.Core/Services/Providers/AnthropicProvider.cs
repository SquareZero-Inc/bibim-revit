// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Anthropic Claude provider — raw HTTP (Messages API), no SDK dependency.
    ///
    /// Request shaping is driven by <see cref="ModelCatalog"/> capabilities:
    ///   • prompt caching: 1-hour TTL on the stable prefix (tools + system), 5-minute
    ///     moving marker on the conversation tail (prefix entries must precede shorter ones)
    ///   • <c>output_config.effort</c> / <c>output_config.format</c> (JSON schema)
    ///   • summarized adaptive thinking when the caller wants live progress
    ///   • <c>fallbacks: "default"</c> on models with safety classifiers
    /// Tool-loop turns stream on the wire (progress + no HTTP timeout on long outputs)
    /// and are assembled back into one Anthropic-shaped message.
    /// Verified against the live API 2026-09-28 (Sonnet 4.6/5, Opus 4.7/5/5.5, Fable 5.1).
    /// </summary>
    public class AnthropicProvider : ILlmProvider
    {
        public const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
        internal const string FallbackBeta = "server-side-fallback-2026-07-01";

        /// <summary>A stalled stream (no bytes for this long) is aborted. The API sends
        /// periodic ping events, so a healthy stream never goes this quiet.</summary>
        private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(180);

        /// <summary>Outputs above this ceiling always stream (avoids HTTP timeouts).</summary>
        private const int StreamAboveMaxTokens = 8192;

        private readonly string _apiKey;
        private readonly string _modelId;
        private readonly HttpClient _httpClient;
        private readonly string _endpoint;
        private readonly string _prefixCacheTtl;

        public string ProviderName => "anthropic";
        public string ModelId => _modelId;

        /// <param name="endpoint">Messages endpoint override (self-hosted gateway). Null = api.anthropic.com.</param>
        /// <param name="prefixCacheTtl">"1h" (default) or "5m" for the tools+system prefix.</param>
        public AnthropicProvider(string apiKey, string modelId, HttpClient httpClient,
            string endpoint = null, string prefixCacheTtl = "1h")
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
            if (string.IsNullOrEmpty(modelId))
                throw new ArgumentException("Model id is required.", nameof(modelId));

            _apiKey = apiKey;
            _modelId = modelId;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _endpoint = string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint.Trim();
            _prefixCacheTtl = string.Equals(prefixCacheTtl, "5m", StringComparison.OrdinalIgnoreCase) ? "5m" : "1h";
        }

        public async Task<JObject> CreateMessageAsync(
            JArray messages,
            string systemPrompt,
            JArray tools,
            CancellationToken ct,
            int maxTokens,
            LlmRequestOptions options = null)
        {
            bool stream = options?.OnProgress != null || maxTokens > StreamAboveMaxTokens;
            var body = BuildRequestBody(_modelId, messages, systemPrompt, tools, maxTokens, options, stream, _prefixCacheTtl);

            using (var request = CreateRequest(body, stream))
            {
                if (!stream)
                {
                    using (var response = await _httpClient.SendAsync(request, ct))
                    {
                        string raw = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                            throw new HttpRequestException($"Anthropic API {(int)response.StatusCode}: {raw}");
                        var msg = JObject.Parse(raw);
                        SanitizeFallbackBoundary(msg["content"] as JArray);
                        return msg;
                    }
                }

                using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        string error = await response.Content.ReadAsStringAsync();
                        throw new HttpRequestException($"Anthropic API {(int)response.StatusCode}: {error}");
                    }

                    var assembler = new SseMessageAssembler(options?.OnProgress);
                    using (var s = await response.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(s))
                    {
                        await PumpSseAsync(reader, assembler.HandleEvent, ct);
                    }
                    var msg = assembler.Build();
                    SanitizeFallbackBoundary(msg["content"] as JArray);
                    return msg;
                }
            }
        }

        public async Task<StreamResult> SendStreamingAsync(
            JArray messages,
            string systemPrompt,
            Action<string> onTextDelta,
            CancellationToken ct,
            int maxTokens,
            LlmRequestOptions options = null)
        {
            var body = BuildRequestBody(_modelId, messages, systemPrompt, null, maxTokens, options, true, _prefixCacheTtl);

            using (var request = CreateRequest(body, true))
            using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    string error = await response.Content.ReadAsStringAsync();
                    throw new HttpRequestException($"Anthropic API {(int)response.StatusCode}: {error}");
                }

                // Chat streams only surface text deltas; reasoning stays hidden in chat.
                var assembler = new SseMessageAssembler((kind, delta) =>
                {
                    if (kind == "text") onTextDelta?.Invoke(delta);
                });
                using (var s = await response.Content.ReadAsStreamAsync())
                using (var reader = new StreamReader(s))
                {
                    await PumpSseAsync(reader, assembler.HandleEvent, ct);
                }

                var msg = assembler.Build();
                var usage = msg["usage"] as JObject ?? new JObject();
                return new StreamResult
                {
                    FullText = ExtractText(msg["content"] as JArray),
                    InputTokens = usage["input_tokens"]?.Value<int>() ?? 0,
                    OutputTokens = usage["output_tokens"]?.Value<int>() ?? 0,
                    CachedInputTokens = usage["cache_read_input_tokens"]?.Value<int>() ?? 0,
                    CacheCreationInputTokens = usage["cache_creation_input_tokens"]?.Value<int>() ?? 0,
                    StopReason = msg["stop_reason"]?.ToString()
                };
            }
        }

        // ───────────────────────────── request shaping ─────────────────────────────

        /// <summary>
        /// Build the Messages API body. Internal + static so the unit tests can assert the
        /// exact wire shape per model without a network call.
        /// </summary>
        internal static JObject BuildRequestBody(
            string modelId, JArray messages, string systemPrompt, JArray tools,
            int maxTokens, LlmRequestOptions options, bool stream, string prefixCacheTtl = "1h")
        {
            var info = ModelCatalog.Describe(modelId);

            var body = new JObject
            {
                ["model"] = modelId,
                ["max_tokens"] = maxTokens,
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = systemPrompt ?? string.Empty,
                        ["cache_control"] = CacheControl(prefixCacheTtl)
                    }
                },
                ["messages"] = messages ?? new JArray()
            };

            if (tools != null && tools.Count > 0)
                body["tools"] = MarkLastToolForCaching(tools, prefixCacheTtl);

            // Single traversal: strip stale markers + tool_result 'name', mark the tail.
            ApplyIncrementalMessageCaching(body["messages"] as JArray);

            var outputConfig = new JObject();
            if (info.SupportsEffort && !string.IsNullOrWhiteSpace(options?.Effort))
                outputConfig["effort"] = options.Effort.Trim().ToLowerInvariant();
            if (info.SupportsStructuredOutput && options?.ResponseSchema != null)
                outputConfig["format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["schema"] = options.ResponseSchema
                };
            if (outputConfig.Count > 0)
                body["output_config"] = outputConfig;

            // Models that think by default return empty thinking text unless display is
            // set. When the caller wants live progress, ask for readable summaries (same
            // billing — display only controls visibility). Older models: leave thinking off.
            if (info.ThinkingOnByDefault && options?.OnProgress != null)
                body["thinking"] = new JObject { ["type"] = "adaptive", ["display"] = "summarized" };

            if (info.ServerSideFallback)
                body["fallbacks"] = "default";

            if (stream)
                body["stream"] = true;

            return body;
        }

        /// <summary>Beta headers required by a request body (comma-joined, or null).</summary>
        internal static string BuildBetaHeader(JObject body)
        {
            var betas = new List<string>();
            if (body["fallbacks"] != null) betas.Add(FallbackBeta);
            return betas.Count > 0 ? string.Join(",", betas) : null;
        }

        private HttpRequestMessage CreateRequest(JObject body, bool stream)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            string beta = BuildBetaHeader(body);
            if (beta != null) request.Headers.Add("anthropic-beta", beta);
            if (stream) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return request;
        }

        /// <summary>5-minute markers carry no ttl field (the API default).</summary>
        private static JObject CacheControl(string ttl)
        {
            var cc = new JObject { ["type"] = "ephemeral" };
            if (string.Equals(ttl, "1h", StringComparison.OrdinalIgnoreCase)) cc["ttl"] = "1h";
            return cc;
        }

        /// <summary>
        /// Incremental conversation caching: mark the LAST content block of the LAST
        /// message (5-minute TTL) so each tool-loop turn cache-hits the whole prior prefix.
        /// The cleanup pass first strips ALL message-level markers — the orchestrator
        /// reuses its messages array across turns, so without cleanup markers would
        /// accumulate and exceed the 4-breakpoint limit (tools 1 + system 1 + tail 1).
        /// </summary>
        internal static void ApplyIncrementalMessageCaching(JArray messages)
        {
            if (messages == null || messages.Count == 0) return;

            foreach (JToken msgToken in messages)
            {
                if (!(msgToken is JObject msg)) continue;
                if (!(msg["content"] is JArray blocks)) continue;
                foreach (JToken b in blocks)
                {
                    if (!(b is JObject blockObj)) continue;
                    blockObj.Remove("cache_control");
                    // Anthropic rejects unknown fields in tool_result blocks.
                    if (blockObj["type"]?.ToString() == "tool_result")
                        blockObj.Remove("name");
                }
            }

            if (!(messages[messages.Count - 1] is JObject last)) return;
            var content = last["content"];
            if (content is JArray arr)
            {
                // Thinking blocks cannot carry cache_control — walk back to a markable block.
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    if (!(arr[i] is JObject blk)) continue;
                    string t = blk["type"]?.ToString();
                    if (t == "thinking" || t == "redacted_thinking") continue;
                    blk["cache_control"] = CacheControl("5m");
                    break;
                }
            }
            else if (content != null && content.Type == JTokenType.String)
            {
                last["content"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = content.ToString(),
                        ["cache_control"] = CacheControl("5m")
                    }
                };
            }
        }

        /// <summary>
        /// Copy of the tool array with the last tool marked for caching (the caller's
        /// array is never mutated). Tools render before system, so this 1-hour marker
        /// sits first in the prefix.
        /// </summary>
        private static JArray MarkLastToolForCaching(JArray tools, string ttl)
        {
            var copy = new JArray();
            for (int i = 0; i < tools.Count; i++)
            {
                if (i == tools.Count - 1 && tools[i] is JObject lastTool)
                {
                    var cloned = (JObject)lastTool.DeepClone();
                    cloned["cache_control"] = CacheControl(ttl);
                    copy.Add(cloned);
                }
                else
                {
                    copy.Add(tools[i].DeepClone());
                }
            }
            return copy;
        }

        /// <summary>
        /// After a server-side fallback, blocks the declined model produced before the
        /// last <c>fallback</c> marker (thinking, tool_use, …) must not be echoed back or
        /// executed; only its text survives. The marker itself is an audit block — drop it.
        /// </summary>
        internal static void SanitizeFallbackBoundary(JArray content)
        {
            if (content == null || content.Count == 0) return;
            int lastFallback = -1;
            for (int i = 0; i < content.Count; i++)
                if (content[i]?["type"]?.ToString() == "fallback") lastFallback = i;
            if (lastFallback < 0) return;

            for (int i = content.Count - 1; i >= 0; i--)
            {
                string t = content[i]?["type"]?.ToString();
                bool beforeBoundary = i < lastFallback;
                if (t == "fallback" ||
                    (beforeBoundary && (t == "thinking" || t == "redacted_thinking" ||
                                        t == "tool_use" || t == "server_tool_use")))
                {
                    content.RemoveAt(i);
                }
            }
            Logger.Log("AnthropicProvider", "Server-side fallback occurred; pre-boundary blocks dropped");
        }

        private static string ExtractText(JArray content)
        {
            var sb = new StringBuilder();
            if (content == null) return string.Empty;
            foreach (var block in content)
                if (block?["type"]?.ToString() == "text") sb.Append(block["text"]?.ToString());
            return sb.ToString();
        }

        // ───────────────────────────── SSE plumbing ─────────────────────────────

        private static async Task PumpSseAsync(StreamReader reader, Action<string, string> onEvent, CancellationToken ct)
        {
            string currentEvent = null;
            var currentData = new StringBuilder();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var readTask = reader.ReadLineAsync();
                // Most lines are already buffered and complete synchronously; only arm
                // the idle timer when we actually have to wait on the network.
                if (!readTask.IsCompleted)
                {
                    var winner = await Task.WhenAny(readTask, Task.Delay(StreamIdleTimeout, ct));
                    if (winner != readTask)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new TimeoutException("Anthropic stream timed out (no data received).");
                    }
                }
                string line = await readTask;
                if (line == null) break;

                if (line.Length == 0)
                {
                    if (currentData.Length > 0) onEvent(currentEvent, currentData.ToString());
                    currentEvent = null;
                    currentData.Clear();
                    continue;
                }
                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    currentEvent = line.Substring("event:".Length).Trim();
                    continue;
                }
                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (currentData.Length > 0) currentData.Append('\n');
                    currentData.Append(line.Substring("data:".Length).TrimStart());
                }
            }
            if (currentData.Length > 0) onEvent(currentEvent, currentData.ToString());
        }

        /// <summary>
        /// Rebuilds a complete Messages API response from its SSE events (text, thinking +
        /// signature, tool_use + partial JSON, fallback markers, usage, stop reason).
        /// Internal for unit tests.
        /// </summary>
        internal sealed class SseMessageAssembler
        {
            private readonly Action<string, string> _onProgress;
            private readonly SortedDictionary<int, JObject> _blocks = new SortedDictionary<int, JObject>();
            private readonly Dictionary<int, StringBuilder> _toolJson = new Dictionary<int, StringBuilder>();
            private readonly JObject _usage = new JObject();
            private string _stopReason;
            private JToken _stopDetails;
            private string _model;

            public SseMessageAssembler(Action<string, string> onProgress)
            {
                _onProgress = onProgress;
            }

            public void HandleEvent(string eventName, string data)
            {
                if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") return;
                JObject payload;
                try { payload = JObject.Parse(data); }
                catch (Exception ex)
                {
                    Logger.Log("AnthropicProvider", $"SSE parse skipped: {ex.Message}");
                    return;
                }

                string type = payload["type"]?.ToString() ?? eventName;
                switch (type)
                {
                    case "message_start":
                        _model = payload["message"]?["model"]?.ToString();
                        MergeUsage(payload["message"]?["usage"] as JObject);
                        break;

                    case "content_block_start":
                    {
                        int index = payload["index"]?.Value<int>() ?? _blocks.Count;
                        var block = payload["content_block"] as JObject ?? new JObject();
                        _blocks[index] = (JObject)block.DeepClone();
                        if (block["type"]?.ToString() == "tool_use")
                            _toolJson[index] = new StringBuilder();
                        break;
                    }

                    case "content_block_delta":
                    {
                        int index = payload["index"]?.Value<int>() ?? 0;
                        if (!_blocks.TryGetValue(index, out var block))
                        {
                            block = new JObject { ["type"] = "text", ["text"] = "" };
                            _blocks[index] = block;
                        }
                        var delta = payload["delta"] as JObject;
                        switch (delta?["type"]?.ToString())
                        {
                            case "text_delta":
                                string text = delta["text"]?.ToString() ?? "";
                                block["text"] = (block["text"]?.ToString() ?? "") + text;
                                if (text.Length > 0) _onProgress?.Invoke("text", text);
                                break;
                            case "thinking_delta":
                                string thinking = delta["thinking"]?.ToString() ?? "";
                                block["thinking"] = (block["thinking"]?.ToString() ?? "") + thinking;
                                if (thinking.Length > 0) _onProgress?.Invoke("thinking", thinking);
                                break;
                            case "signature_delta":
                                block["signature"] = (block["signature"]?.ToString() ?? "") + (delta["signature"]?.ToString() ?? "");
                                break;
                            case "input_json_delta":
                                if (!_toolJson.TryGetValue(index, out var sb))
                                    _toolJson[index] = sb = new StringBuilder();
                                sb.Append(delta["partial_json"]?.ToString() ?? "");
                                break;
                        }
                        break;
                    }

                    case "message_delta":
                        if (payload["delta"]?["stop_reason"] != null)
                            _stopReason = payload["delta"]["stop_reason"].ToString();
                        if (payload["delta"]?["stop_details"] != null)
                            _stopDetails = payload["delta"]["stop_details"];
                        MergeUsage(payload["usage"] as JObject);
                        break;

                    case "error":
                        throw new HttpRequestException(
                            $"Anthropic API {ErrorStatus(payload["error"]?["type"]?.ToString())}: {payload}");
                }
            }

            public JObject Build()
            {
                var content = new JArray();
                foreach (var kv in _blocks)
                {
                    var block = kv.Value;
                    if (block["type"]?.ToString() == "tool_use")
                    {
                        string json = _toolJson.TryGetValue(kv.Key, out var sb) ? sb.ToString() : "";
                        JObject input;
                        try { input = string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json); }
                        catch (Exception ex)
                        {
                            Logger.Log("AnthropicProvider", $"tool_use input JSON incomplete: {ex.Message}");
                            input = new JObject();
                        }
                        block["input"] = input;
                    }
                    content.Add(block);
                }

                var result = new JObject
                {
                    ["content"] = content,
                    ["stop_reason"] = _stopReason ?? "end_turn",
                    ["usage"] = _usage,
                    ["model"] = _model ?? ""
                };
                if (_stopDetails != null) result["stop_details"] = _stopDetails;
                return result;
            }

            private void MergeUsage(JObject usage)
            {
                if (usage == null) return;
                foreach (var prop in usage.Properties())
                {
                    // message_delta repeats cumulative counts; later non-zero values win.
                    if (prop.Value.Type == JTokenType.Integer)
                    {
                        int v = prop.Value.Value<int>();
                        if (v != 0 || _usage[prop.Name] == null) _usage[prop.Name] = v;
                    }
                    else if (prop.Value.Type != JTokenType.Null)
                    {
                        _usage[prop.Name] = prop.Value.DeepClone();
                    }
                }
            }

            private static int ErrorStatus(string errorType)
            {
                switch (errorType)
                {
                    case "overloaded_error": return 529;
                    case "rate_limit_error": return 429;
                    case "invalid_request_error": return 400;
                    case "authentication_error": return 401;
                    case "permission_error": return 403;
                    default: return 500;
                }
            }
        }
    }
}
