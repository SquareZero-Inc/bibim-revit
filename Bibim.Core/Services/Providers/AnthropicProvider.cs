// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
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
    /// Anthropic Claude provider — raw HTTP only (no SDK dependency).
    /// Handles both streaming chat (SSE) and non-streaming tool loop.
    /// Endpoint: https://api.anthropic.com/v1/messages
    /// </summary>
    public class AnthropicProvider : ILlmProvider
    {
        private const string Endpoint = "https://api.anthropic.com/v1/messages";

        private readonly string _apiKey;
        private readonly string _modelId;
        private readonly HttpClient _httpClient;

        public string ProviderName => "anthropic";
        public string ModelId => _modelId;

        public AnthropicProvider(string apiKey, string modelId, HttpClient httpClient)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
            if (string.IsNullOrEmpty(modelId))
                throw new ArgumentException("Model id is required.", nameof(modelId));

            _apiKey = apiKey;
            _modelId = modelId;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<JObject> SendNonStreamingAsync(
            JArray messages,
            string systemPrompt,
            JArray tools,
            CancellationToken ct,
            int maxTokens,
            bool jsonMode = false)
        {
            // jsonMode is intentionally not wired to a native flag here —
            // Anthropic doesn't expose a JSON-only mode, and Claude reliably
            // follows "Return JSON only" instructions in the system prompt.
            // Parameter is accepted for interface uniformity.

            var requestBody = new JObject
            {
                ["model"] = _modelId,
                ["max_tokens"] = maxTokens,
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = systemPrompt ?? string.Empty,
                        ["cache_control"] = new JObject { ["type"] = "ephemeral" }
                    }
                },
                ["messages"] = messages ?? new JArray()
            };
            if (tools != null && tools.Count > 0)
                requestBody["tools"] = MarkLastToolForCaching(tools);

            // Anthropic rejects unknown fields in tool_result blocks.
            // Strip 'name' here (provider-specific) so the Gemini adapter can
            // still rely on it in the shared orchestrator payload.
            var messagesArray = requestBody["messages"] as JArray ?? new JArray();
            foreach (JToken msgToken in messagesArray)
            {
                if (!(msgToken is JObject msg)) continue;
                if (!(msg["content"] is JArray contentArr)) continue;
                foreach (JToken blockToken in contentArr)
                {
                    if (!(blockToken is JObject block)) continue;
                    if (block["type"]?.ToString() == "tool_result")
                        block.Remove("name");
                }
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Anthropic API {(int)response.StatusCode}: {body}");
                    return JObject.Parse(body);
                }
            }
        }

        public async Task<StreamResult> SendStreamingAsync(
            JArray messages,
            string systemPrompt,
            Action<string> onTextDelta,
            CancellationToken ct,
            int maxTokens)
        {
            var requestBody = new JObject
            {
                ["model"] = _modelId,
                ["max_tokens"] = maxTokens,
                ["system"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "text",
                        ["text"] = systemPrompt ?? string.Empty,
                        ["cache_control"] = new JObject { ["type"] = "ephemeral" }
                    }
                },
                ["stream"] = true,
                ["messages"] = messages ?? new JArray()
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, Endpoint))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                using (var httpResponse = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct))
                {
                    if (!httpResponse.IsSuccessStatusCode)
                    {
                        string error = await httpResponse.Content.ReadAsStringAsync();
                        throw new HttpRequestException(
                            $"Anthropic API {(int)httpResponse.StatusCode}: {error}");
                    }

                    var fullText = new StringBuilder();
                    int inputTokens = 0;
                    int outputTokens = 0;
                    int cachedTokens = 0;
                    int cacheCreationTokens = 0;

                    using (var stream = await httpResponse.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string currentEvent = null;
                        var currentData = new StringBuilder();
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (line.Length == 0)
                            {
                                ProcessSseEvent(
                                    currentEvent, currentData.ToString(),
                                    fullText, onTextDelta,
                                    ref inputTokens, ref outputTokens, ref cachedTokens, ref cacheCreationTokens);
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
                                if (currentData.Length > 0) currentData.AppendLine();
                                currentData.Append(line.Substring("data:".Length).TrimStart());
                            }
                        }
                        ProcessSseEvent(
                            currentEvent, currentData.ToString(),
                            fullText, onTextDelta,
                            ref inputTokens, ref outputTokens, ref cachedTokens, ref cacheCreationTokens);
                    }

                    return new StreamResult
                    {
                        FullText = fullText.ToString(),
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        CachedInputTokens = cachedTokens,
                        CacheCreationInputTokens = cacheCreationTokens
                    };
                }
            }
        }

        // ───────────────────────────── private ─────────────────────────────

        /// <summary>
        /// Adds <c>cache_control: ephemeral</c> to the last tool definition so the
        /// entire system+tools prefix becomes one cacheable block on subsequent
        /// requests within the 5-minute TTL window. Returns a shallow copy of the
        /// array with only the last element cloned, so the caller's source array
        /// is never mutated.
        /// </summary>
        private static JArray MarkLastToolForCaching(JArray tools)
        {
            var copy = new JArray();
            for (int i = 0; i < tools.Count; i++)
            {
                if (i == tools.Count - 1 && tools[i] is JObject lastTool)
                {
                    var cloned = (JObject)lastTool.DeepClone();
                    cloned["cache_control"] = new JObject { ["type"] = "ephemeral" };
                    copy.Add(cloned);
                }
                else
                {
                    copy.Add(tools[i]);
                }
            }
            return copy;
        }

        private static void ProcessSseEvent(
            string eventName,
            string data,
            StringBuilder fullText,
            Action<string> onTextDelta,
            ref int inputTokens,
            ref int outputTokens,
            ref int cachedTokens,
            ref int cacheCreationTokens)
        {
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") return;

            try
            {
                var payload = JObject.Parse(data);
                string type = payload["type"]?.ToString();
                string effectiveType = !string.IsNullOrWhiteSpace(type) ? type : eventName;

                switch (effectiveType)
                {
                    case "content_block_delta":
                        string deltaType = payload["delta"]?["type"]?.ToString();
                        if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                        {
                            string deltaText = payload["delta"]?["text"]?.ToString();
                            if (!string.IsNullOrEmpty(deltaText))
                            {
                                fullText.Append(deltaText);
                                onTextDelta?.Invoke(deltaText);
                            }
                        }
                        break;

                    case "message_start":
                        var startUsage = payload["message"]?["usage"];
                        inputTokens = startUsage?["input_tokens"]?.Value<int>() ?? inputTokens;
                        outputTokens = startUsage?["output_tokens"]?.Value<int>() ?? outputTokens;
                        cachedTokens = startUsage?["cache_read_input_tokens"]?.Value<int>() ?? cachedTokens;
                        cacheCreationTokens = startUsage?["cache_creation_input_tokens"]?.Value<int>() ?? cacheCreationTokens;
                        break;

                    case "message_delta":
                        outputTokens = payload["usage"]?["output_tokens"]?.Value<int>() ?? outputTokens;
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("AnthropicProvider", $"SSE parse skipped: {ex.Message}");
            }
        }
    }
}
