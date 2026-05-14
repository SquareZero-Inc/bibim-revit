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
    /// Self-hosted local LLM provider. Targets any server exposing an OpenAI-compatible
    /// <c>/v1/chat/completions</c> endpoint (Ollama, LM Studio, vLLM, llama.cpp server).
    ///
    /// IMPORTANT: this is intentionally NOT a subclass of OpenAIProvider — the upstream
    /// uses the OpenAI Responses API (<c>/v1/responses</c>) which is OpenAI-only.
    /// Self-hosted servers only implement Chat Completions, so the translation layer
    /// (tools, tool_calls, tool role messages) is different and lives here.
    ///
    /// Anthropic-shape canonical translation:
    ///   • tool_use   block ↔ assistant.tool_calls[].function
    ///   • tool_result block ↔ {"role":"tool", "tool_call_id":..., "content":...}
    ///   • tools defs ↔ [{"type":"function", "function":{name, description, parameters}}]
    /// </summary>
    public class LocalProvider : ILlmProvider
    {
        private readonly string _apiKey;             // optional — empty for unauthenticated localhost
        private readonly string _modelId;            // canonical id (used for routing/logs)
        private string _serverModelName;             // what we actually send in the "model" field
        private bool _serverModelNameResolved;       // false until lazy resolution succeeds
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;            // e.g. http://localhost:11434/v1

        public string ProviderName => "local";
        public string ModelId => _modelId;

        public LocalProvider(
            string apiKey,
            string modelId,
            HttpClient httpClient,
            string baseUrl,
            string serverModelName = null)
        {
            if (string.IsNullOrEmpty(modelId))
                throw new ArgumentException("Model id is required.", nameof(modelId));
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("Local LLM base URL is required.", nameof(baseUrl));

            _apiKey = apiKey;   // may be null/empty — that's allowed for unauth localhost
            _modelId = modelId;
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            // Two paths for the server-side model name:
            //   (1) Caller passed an explicit name → use it as-is, no discovery.
            //   (2) Caller passed null/empty → defer resolution to the first request,
            //       where we'll call /v1/models and pick the first available id.
            //       This is the zero-friction path — user only configures the URL
            //       and we figure out the model from the server's own catalogue.
            if (!string.IsNullOrWhiteSpace(serverModelName))
            {
                _serverModelName = serverModelName;
                _serverModelNameResolved = true;
            }
            else
            {
                _serverModelName = null;
                _serverModelNameResolved = false;
            }
        }

        /// <summary>
        /// Resolve the model name to send in the "model" field. If the caller
        /// supplied one at construction time we return it immediately. Otherwise
        /// we call /v1/models on first use and cache the first available id.
        /// Fails open by stripping the vendor prefix from <see cref="_modelId"/>
        /// when /v1/models is unreachable — last-resort heuristic so the request
        /// at least attempts to go out.
        /// </summary>
        private async Task<string> ResolveServerModelNameAsync(CancellationToken ct)
        {
            if (_serverModelNameResolved && !string.IsNullOrEmpty(_serverModelName))
                return _serverModelName;

            string resolved = null;
            try
            {
                var modelsJson = await ListModelsAsync(ct);
                var data = modelsJson?["data"] as JArray;
                if (data != null && data.Count > 0)
                    resolved = data[0]?["id"]?.ToString();
            }
            catch (Exception ex)
            {
                Logger.Log("LocalProvider",
                    $"Auto-discovery /v1/models failed; falling back to stripped canonical id: {ex.Message}");
            }

            _serverModelName = !string.IsNullOrWhiteSpace(resolved)
                ? resolved
                : StripVendorPrefix(_modelId);
            _serverModelNameResolved = true;
            return _serverModelName;
        }

        // ────────────────────────────── public API ──────────────────────────────

        public async Task<JObject> SendNonStreamingAsync(
            JArray messages,
            string systemPrompt,
            JArray tools,
            CancellationToken ct,
            int maxTokens,
            bool jsonMode = false)
        {
            var chatMessages = TranslateMessagesToChatCompletions(messages, systemPrompt);
            string modelToSend = await ResolveServerModelNameAsync(ct);

            var requestBody = new JObject
            {
                ["model"] = modelToSend,
                ["messages"] = chatMessages,
                ["max_tokens"] = maxTokens,
                ["stream"] = false
            };

            if (tools != null && tools.Count > 0)
            {
                requestBody["tools"] = TranslateToolsToChatCompletions(tools);
                requestBody["tool_choice"] = "auto";
            }

            if (jsonMode)
            {
                requestBody["response_format"] = new JObject { ["type"] = "json_object" };
            }

            using (var request = BuildHttpRequest("/chat/completions"))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Local LLM ({_baseUrl}) {(int)response.StatusCode}: {body}");
                    var raw = JObject.Parse(body);
                    return TranslateResponseToAnthropicShape(raw);
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
            var chatMessages = TranslateMessagesToChatCompletions(messages, systemPrompt);
            string modelToSend = await ResolveServerModelNameAsync(ct);

            var requestBody = new JObject
            {
                ["model"] = modelToSend,
                ["messages"] = chatMessages,
                ["max_tokens"] = maxTokens,
                ["stream"] = true,
                ["stream_options"] = new JObject { ["include_usage"] = true }
            };

            using (var request = BuildHttpRequest("/chat/completions"))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
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
                            $"Local LLM ({_baseUrl}) {(int)httpResponse.StatusCode}: {error}");
                    }

                    var fullText = new StringBuilder();
                    int inputTokens = 0;
                    int outputTokens = 0;

                    using (var stream = await httpResponse.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                            string data = line.Substring("data:".Length).Trim();
                            if (data == "[DONE]") break;
                            if (string.IsNullOrWhiteSpace(data)) continue;

                            try
                            {
                                var payload = JObject.Parse(data);
                                var choices = payload["choices"] as JArray;
                                if (choices != null && choices.Count > 0)
                                {
                                    string deltaText = choices[0]?["delta"]?["content"]?.ToString();
                                    if (!string.IsNullOrEmpty(deltaText))
                                    {
                                        fullText.Append(deltaText);
                                        onTextDelta?.Invoke(deltaText);
                                    }
                                }

                                var usage = payload["usage"];
                                if (usage != null)
                                {
                                    inputTokens = usage["prompt_tokens"]?.Value<int>() ?? inputTokens;
                                    outputTokens = usage["completion_tokens"]?.Value<int>() ?? outputTokens;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log("LocalProvider", $"SSE parse skipped: {ex.Message}");
                            }
                        }
                    }

                    return new StreamResult
                    {
                        FullText = fullText.ToString(),
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens
                    };
                }
            }
        }

        /// <summary>
        /// Lightweight readiness check for the "Test connection" button. Hits
        /// <c>/models</c> and returns the parsed JSON on success, throws on failure.
        /// </summary>
        public async Task<JObject> ListModelsAsync(CancellationToken ct)
        {
            using (var request = BuildHttpRequest("/models", HttpMethod.Get))
            using (var response = await _httpClient.SendAsync(request, ct))
            {
                string body = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Local LLM ({_baseUrl}) {(int)response.StatusCode}: {body}");
                return JObject.Parse(body);
            }
        }

        // ────────────────────────────── translators ──────────────────────────────

        private static string StripVendorPrefix(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return modelId;
            int slash = modelId.IndexOf('/');
            return slash >= 0 && slash < modelId.Length - 1
                ? modelId.Substring(slash + 1)
                : modelId;
        }

        private HttpRequestMessage BuildHttpRequest(string path, HttpMethod method = null)
        {
            method = method ?? HttpMethod.Post;
            string url = _baseUrl + path;
            var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrWhiteSpace(_apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return request;
        }

        /// <summary>
        /// Convert Anthropic-shaped messages to OpenAI Chat Completions format.
        /// Block-array assistant messages are collapsed into one message with both
        /// content + tool_calls (Chat Completions requirement). User tool_result
        /// blocks split into individual {role:"tool"} messages.
        /// </summary>
        private static JArray TranslateMessagesToChatCompletions(JArray anthropicMessages, string systemPrompt)
        {
            var output = new JArray();

            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                output.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                });
            }

            if (anthropicMessages == null) return output;

            foreach (JObject msg in anthropicMessages)
            {
                string role = msg["role"]?.ToString();
                var content = msg["content"];

                // Simple string content
                if (content is JValue contentVal && contentVal.Type == JTokenType.String)
                {
                    output.Add(new JObject
                    {
                        ["role"] = role,
                        ["content"] = contentVal.ToString()
                    });
                    continue;
                }

                if (!(content is JArray contentArray)) continue;

                if (role == "assistant")
                {
                    // Collapse text + tool_use blocks into ONE assistant message
                    var textSb = new StringBuilder();
                    var toolCalls = new JArray();
                    foreach (JObject block in contentArray)
                    {
                        switch (block["type"]?.ToString())
                        {
                            case "text":
                                textSb.Append(block["text"]?.ToString() ?? "");
                                break;
                            case "tool_use":
                                toolCalls.Add(new JObject
                                {
                                    ["id"] = block["id"]?.ToString(),
                                    ["type"] = "function",
                                    ["function"] = new JObject
                                    {
                                        ["name"] = block["name"]?.ToString(),
                                        ["arguments"] = block["input"]?.ToString(Formatting.None) ?? "{}"
                                    }
                                });
                                break;
                        }
                    }

                    var asstMsg = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = textSb.Length > 0 ? (JToken)textSb.ToString() : JValue.CreateNull()
                    };
                    if (toolCalls.Count > 0) asstMsg["tool_calls"] = toolCalls;
                    output.Add(asstMsg);
                }
                else if (role == "user")
                {
                    // Split tool_result blocks into separate {role:"tool"} messages;
                    // text blocks become a normal user message.
                    var userTextSb = new StringBuilder();
                    foreach (JObject block in contentArray)
                    {
                        switch (block["type"]?.ToString())
                        {
                            case "text":
                                userTextSb.Append(block["text"]?.ToString() ?? "");
                                break;
                            case "tool_result":
                                output.Add(new JObject
                                {
                                    ["role"] = "tool",
                                    ["tool_call_id"] = block["tool_use_id"]?.ToString(),
                                    ["content"] = block["content"]?.ToString() ?? ""
                                });
                                break;
                        }
                    }
                    if (userTextSb.Length > 0)
                    {
                        output.Add(new JObject
                        {
                            ["role"] = "user",
                            ["content"] = userTextSb.ToString()
                        });
                    }
                }
            }

            return output;
        }

        /// <summary>
        /// Anthropic tools → Chat Completions tools format.
        /// </summary>
        private static JArray TranslateToolsToChatCompletions(JArray anthropicTools)
        {
            var output = new JArray();
            if (anthropicTools == null) return output;

            foreach (JObject tool in anthropicTools)
            {
                output.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = tool["name"]?.ToString(),
                        ["description"] = tool["description"]?.ToString(),
                        ["parameters"] = tool["input_schema"] ?? new JObject()
                    }
                });
            }
            return output;
        }

        /// <summary>
        /// Chat Completions response → Anthropic-shaped {content, stop_reason, usage}.
        /// </summary>
        private static JObject TranslateResponseToAnthropicShape(JObject raw)
        {
            var content = new JArray();
            string stopReason = "end_turn";

            var choices = raw["choices"] as JArray;
            if (choices != null && choices.Count > 0)
            {
                var choice0 = choices[0] as JObject;
                var message = choice0?["message"] as JObject;
                if (message != null)
                {
                    // Text content (may be null when only tool_calls are returned)
                    var msgContent = message["content"];
                    if (msgContent != null && msgContent.Type != JTokenType.Null)
                    {
                        string text = msgContent.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            content.Add(new JObject
                            {
                                ["type"] = "text",
                                ["text"] = text
                            });
                        }
                    }

                    // tool_calls → tool_use blocks
                    var toolCalls = message["tool_calls"] as JArray;
                    if (toolCalls != null && toolCalls.Count > 0)
                    {
                        foreach (JObject call in toolCalls)
                        {
                            var fn = call["function"] as JObject;
                            string argsStr = fn?["arguments"]?.ToString() ?? "{}";
                            JObject argsObj;
                            try { argsObj = JObject.Parse(argsStr); }
                            catch { argsObj = new JObject(); }

                            content.Add(new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = call["id"]?.ToString(),
                                ["name"] = fn?["name"]?.ToString(),
                                ["input"] = argsObj
                            });
                        }
                    }
                }

                // Map finish_reason → Anthropic stop_reason.
                // Per BIBIM-007 contract: if tool_use blocks exist, stop_reason must be
                // "tool_use" so the orchestrator pairs them with tool_results.
                string finish = choice0?["finish_reason"]?.ToString();
                bool hasToolUse = false;
                foreach (JObject b in content)
                {
                    if (b["type"]?.ToString() == "tool_use") { hasToolUse = true; break; }
                }
                if (hasToolUse) stopReason = "tool_use";
                else if (finish == "length") stopReason = "max_tokens";
                else stopReason = "end_turn";
            }

            var usageIn = raw["usage"] ?? new JObject();
            var usage = new JObject
            {
                ["input_tokens"] = usageIn["prompt_tokens"] ?? 0,
                ["output_tokens"] = usageIn["completion_tokens"] ?? 0,
                ["cache_read_input_tokens"] = usageIn["prompt_tokens_details"]?["cached_tokens"] ?? 0
            };

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stopReason,
                ["usage"] = usage,
                ["model"] = raw["model"] ?? ""
            };
        }
    }
}
