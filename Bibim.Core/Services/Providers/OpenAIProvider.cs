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
    /// OpenAI provider using the Responses API for tool/agent workflows.
    /// Endpoint: https://api.openai.com/v1/responses
    ///
    /// Translation strategy:
    ///   • Anthropic-shaped messages JArray → OpenAI Responses input array
    ///   • Anthropic-shaped tools JArray → OpenAI tools array (function format)
    ///   • OpenAI response → re-shaped into Anthropic-style {content, stop_reason, usage}
    ///     so the orchestrator's tool-loop logic stays unchanged.
    /// </summary>
    public class OpenAIProvider : ILlmProvider
    {
        private const string ResponsesEndpoint = "https://api.openai.com/v1/responses";
        // Chat Completions endpoint kept for streaming chat (no tools) — simpler API.
        private const string ChatEndpoint = "https://api.openai.com/v1/chat/completions";

        private readonly string _apiKey;
        private readonly string _modelId;
        private readonly HttpClient _httpClient;

        public string ProviderName => "openai";
        public string ModelId => _modelId;

        public OpenAIProvider(string apiKey, string modelId, HttpClient httpClient)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("OpenAI API key is required.", nameof(apiKey));
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
            // Build OpenAI Responses API input array from Anthropic-shaped messages.
            // OpenAI uses: input = [ { type: "message", role, content: [{type:"input_text", text}] }, ... ]
            // For tool_use/tool_result blocks we pass through pre-translated structures.
            var input = TranslateMessagesToOpenAI(messages);

            var requestBody = new JObject
            {
                ["model"] = _modelId,
                ["instructions"] = systemPrompt ?? string.Empty,
                ["input"] = input,
                ["max_output_tokens"] = maxTokens,
                ["stream"] = false
            };

            if (tools != null && tools.Count > 0)
                requestBody["tools"] = TranslateToolsToOpenAI(tools);

            // JSON-only output mode for the Task Planner.
            // Responses API uses `text.format` with `type: "json_object"`.
            if (jsonMode)
            {
                requestBody["text"] = new JObject
                {
                    ["format"] = new JObject { ["type"] = "json_object" }
                };
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, ResponsesEndpoint))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"OpenAI API {(int)response.StatusCode}: {body}");
                    var openAiResponse = JObject.Parse(body);
                    return TranslateResponseToAnthropicShape(openAiResponse);
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
            // For chat-only streaming (no tools), use Chat Completions API — simpler SSE format.
            var chatMessages = new JArray();
            if (!string.IsNullOrEmpty(systemPrompt))
            {
                chatMessages.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                });
            }
            // Translate Anthropic-shaped history into chat completions format
            foreach (JObject msg in messages ?? new JArray())
            {
                string role = msg["role"]?.ToString();
                string content = ExtractTextFromAnthropicContent(msg["content"]);
                chatMessages.Add(new JObject
                {
                    ["role"] = role,
                    ["content"] = content ?? string.Empty
                });
            }

            var requestBody = new JObject
            {
                ["model"] = _modelId,
                ["messages"] = chatMessages,
                ["max_completion_tokens"] = maxTokens,
                ["stream"] = true,
                ["stream_options"] = new JObject { ["include_usage"] = true }
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
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
                            $"OpenAI API {(int)httpResponse.StatusCode}: {error}");
                    }

                    var fullText = new StringBuilder();
                    int inputTokens = 0;
                    int outputTokens = 0;
                    int cachedTokens = 0;

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
                                    cachedTokens = usage["prompt_tokens_details"]?["cached_tokens"]?.Value<int>() ?? cachedTokens;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log("OpenAIProvider", $"SSE parse skipped: {ex.Message}");
                            }
                        }
                    }

                    return new StreamResult
                    {
                        FullText = fullText.ToString(),
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        CachedInputTokens = cachedTokens
                    };
                }
            }
        }

        // ───────────────────────────── translators ─────────────────────────────

        /// <summary>
        /// Convert Anthropic-shaped messages to OpenAI Responses API input format.
        /// Handles plain text, tool_use blocks (assistant), and tool_result blocks (user).
        /// </summary>
        private static JArray TranslateMessagesToOpenAI(JArray anthropicMessages)
        {
            var input = new JArray();
            if (anthropicMessages == null) return input;

            foreach (JObject msg in anthropicMessages)
            {
                string role = msg["role"]?.ToString();
                var content = msg["content"];

                if (content is JValue contentVal && contentVal.Type == JTokenType.String)
                {
                    // Simple text message
                    input.Add(new JObject
                    {
                        ["type"] = "message",
                        ["role"] = role,
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = role == "user" ? "input_text" : "output_text",
                                ["text"] = contentVal.ToString()
                            }
                        }
                    });
                }
                else if (content is JArray contentArray)
                {
                    // Block array — split into separate input items (OpenAI keeps function calls
                    // and tool outputs as top-level items, not embedded in messages).
                    foreach (JObject block in contentArray)
                    {
                        string blockType = block["type"]?.ToString();
                        switch (blockType)
                        {
                            case "text":
                                input.Add(new JObject
                                {
                                    ["type"] = "message",
                                    ["role"] = role,
                                    ["content"] = new JArray
                                    {
                                        new JObject
                                        {
                                            ["type"] = role == "user" ? "input_text" : "output_text",
                                            ["text"] = block["text"]?.ToString() ?? ""
                                        }
                                    }
                                });
                                break;

                            case "tool_use":
                                // Anthropic tool_use → OpenAI function_call
                                input.Add(new JObject
                                {
                                    ["type"] = "function_call",
                                    ["call_id"] = block["id"]?.ToString(),
                                    ["name"] = block["name"]?.ToString(),
                                    ["arguments"] = block["input"]?.ToString(Formatting.None) ?? "{}"
                                });
                                break;

                            case "tool_result":
                                // Anthropic tool_result → OpenAI function_call_output
                                input.Add(new JObject
                                {
                                    ["type"] = "function_call_output",
                                    ["call_id"] = block["tool_use_id"]?.ToString(),
                                    ["output"] = block["content"]?.ToString() ?? ""
                                });
                                break;
                        }
                    }
                }
            }
            return input;
        }

        /// <summary>
        /// Convert Anthropic-shaped tools to OpenAI Responses API tools format.
        /// Anthropic: { name, description, input_schema }
        /// OpenAI:    { type: "function", name, description, parameters }
        /// </summary>
        private static JArray TranslateToolsToOpenAI(JArray anthropicTools)
        {
            var openAiTools = new JArray();
            if (anthropicTools == null) return openAiTools;

            foreach (JObject tool in anthropicTools)
            {
                openAiTools.Add(new JObject
                {
                    ["type"] = "function",
                    ["name"] = tool["name"]?.ToString(),
                    ["description"] = tool["description"]?.ToString(),
                    ["parameters"] = tool["input_schema"] ?? new JObject()
                });
            }
            return openAiTools;
        }

        /// <summary>
        /// Re-shape an OpenAI Responses API response into Anthropic-style:
        ///   { content: [...], stop_reason, usage: { input_tokens, output_tokens } }
        /// so the orchestrator's tool-loop logic works without modification.
        /// </summary>
        private static JObject TranslateResponseToAnthropicShape(JObject openAiResponse)
        {
            var content = new JArray();
            string stopReason = "end_turn";

            // OpenAI response.output is an array of items: messages, function_calls, reasoning
            var output = openAiResponse["output"] as JArray ?? new JArray();
            foreach (JObject item in output)
            {
                string itemType = item["type"]?.ToString();

                if (itemType == "message")
                {
                    // Extract text content
                    var msgContent = item["content"] as JArray;
                    if (msgContent != null)
                    {
                        foreach (JObject part in msgContent)
                        {
                            string partType = part["type"]?.ToString();
                            if (partType == "output_text")
                            {
                                content.Add(new JObject
                                {
                                    ["type"] = "text",
                                    ["text"] = part["text"]?.ToString() ?? ""
                                });
                            }
                        }
                    }
                }
                else if (itemType == "function_call")
                {
                    // OpenAI arguments come as stringified JSON — parse them
                    JObject argsObj;
                    try
                    {
                        string argsStr = item["arguments"]?.ToString() ?? "{}";
                        argsObj = JObject.Parse(argsStr);
                    }
                    catch
                    {
                        argsObj = new JObject();
                    }

                    content.Add(new JObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = item["call_id"]?.ToString(),
                        ["name"] = item["name"]?.ToString(),
                        ["input"] = argsObj
                    });
                    stopReason = "tool_use";
                }
                // reasoning items: ignored for now (could surface as status updates later)
            }

            // Map OpenAI usage to Anthropic shape
            var openAiUsage = openAiResponse["usage"] ?? new JObject();
            var usage = new JObject
            {
                ["input_tokens"] = openAiUsage["input_tokens"] ?? openAiUsage["prompt_tokens"] ?? 0,
                ["output_tokens"] = openAiUsage["output_tokens"] ?? openAiUsage["completion_tokens"] ?? 0,
                ["cache_read_input_tokens"] =
                    openAiUsage["input_tokens_details"]?["cached_tokens"] ??
                    openAiUsage["prompt_tokens_details"]?["cached_tokens"] ?? 0
            };

            // Honour explicit finish/stop hints if present
            string finishReason = openAiResponse["status"]?.ToString();
            if (finishReason == "incomplete") stopReason = "max_tokens";

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stopReason,
                ["usage"] = usage,
                ["model"] = openAiResponse["model"] ?? ""
            };
        }

        /// <summary>
        /// Helper to flatten Anthropic block-array content back to a plain string for chat completions.
        /// </summary>
        private static string ExtractTextFromAnthropicContent(JToken content)
        {
            if (content == null) return "";
            if (content is JValue val && val.Type == JTokenType.String) return val.ToString();
            if (content is JArray arr)
            {
                var sb = new StringBuilder();
                foreach (JObject block in arr)
                {
                    if (block["type"]?.ToString() == "text")
                        sb.Append(block["text"]?.ToString());
                }
                return sb.ToString();
            }
            return content.ToString();
        }
    }
}
