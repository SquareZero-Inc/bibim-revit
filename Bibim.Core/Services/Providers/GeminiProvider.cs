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
    /// Google Gemini provider — uses 3.1 Pro with the custom-tools endpoint variant
    /// for better prioritisation of caller-defined tools (search_revit_api etc.).
    /// Endpoint: https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent
    ///
    /// Translation strategy mirrors OpenAIProvider:
    ///   • Anthropic-shaped messages → Gemini contents array
    ///   • Anthropic-shaped tools → Gemini functionDeclarations
    ///   • Gemini response → re-shaped into Anthropic-style {content, stop_reason, usage}
    /// </summary>
    public class GeminiProvider : ILlmProvider
    {
        private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/models/";

        private readonly string _apiKey;
        private readonly string _modelId;
        private readonly HttpClient _httpClient;

        public string ProviderName => "gemini";
        public string ModelId => _modelId;

        public GeminiProvider(string apiKey, string modelId, HttpClient httpClient)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("Gemini API key is required.", nameof(apiKey));
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
            var requestBody = BuildGeminiRequest(messages, systemPrompt, tools, maxTokens, jsonMode);
            string url = $"{ApiBase}{_modelId}:generateContent?key={_apiKey}";

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
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
                            $"Gemini API {(int)response.StatusCode}: {body}");
                    var geminiResponse = JObject.Parse(body);
                    return TranslateResponseToAnthropicShape(geminiResponse);
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
            var requestBody = BuildGeminiRequest(messages, systemPrompt, null, maxTokens, jsonMode: false);
            // Gemini's streamGenerateContent uses Server-Sent Events when alt=sse is set.
            string url = $"{ApiBase}{_modelId}:streamGenerateContent?alt=sse&key={_apiKey}";

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
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
                            $"Gemini API {(int)httpResponse.StatusCode}: {error}");
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
                            if (string.IsNullOrWhiteSpace(data)) continue;

                            try
                            {
                                var payload = JObject.Parse(data);

                                // Extract text deltas from candidate parts
                                var candidates = payload["candidates"] as JArray;
                                if (candidates != null && candidates.Count > 0)
                                {
                                    var parts = candidates[0]?["content"]?["parts"] as JArray;
                                    if (parts != null)
                                    {
                                        foreach (JObject part in parts)
                                        {
                                            string text = part["text"]?.ToString();
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                fullText.Append(text);
                                                onTextDelta?.Invoke(text);
                                            }
                                        }
                                    }
                                }

                                // Usage metadata appears on later chunks
                                var usage = payload["usageMetadata"];
                                if (usage != null)
                                {
                                    inputTokens = usage["promptTokenCount"]?.Value<int>() ?? inputTokens;
                                    outputTokens = usage["candidatesTokenCount"]?.Value<int>() ?? outputTokens;
                                    cachedTokens = usage["cachedContentTokenCount"]?.Value<int>() ?? cachedTokens;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Log("GeminiProvider", $"SSE parse skipped: {ex.Message}");
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

        private static JObject BuildGeminiRequest(
            JArray anthropicMessages,
            string systemPrompt,
            JArray anthropicTools,
            int maxTokens,
            bool jsonMode)
        {
            var generationConfig = new JObject
            {
                ["maxOutputTokens"] = maxTokens
            };

            // JSON-only output mode (Gemini's native flag). Required for the
            // planner — without it Gemini frequently emits prose-wrapped or
            // truncated JSON.
            if (jsonMode)
                generationConfig["responseMimeType"] = "application/json";

            var request = new JObject
            {
                ["contents"] = TranslateMessagesToGemini(anthropicMessages),
                ["generationConfig"] = generationConfig
            };

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                request["systemInstruction"] = new JObject
                {
                    ["parts"] = new JArray
                    {
                        new JObject { ["text"] = systemPrompt }
                    }
                };
            }

            if (anthropicTools != null && anthropicTools.Count > 0)
            {
                request["tools"] = new JArray
                {
                    new JObject
                    {
                        ["functionDeclarations"] = TranslateToolsToGemini(anthropicTools)
                    }
                };
            }

            return request;
        }

        /// <summary>
        /// Convert Anthropic-shaped messages to Gemini contents array.
        /// Gemini uses role "user" / "model" (NOT "assistant").
        /// Each message has a parts array; tool calls become functionCall parts,
        /// tool results become functionResponse parts.
        /// </summary>
        private static JArray TranslateMessagesToGemini(JArray anthropicMessages)
        {
            var contents = new JArray();
            if (anthropicMessages == null) return contents;

            foreach (JObject msg in anthropicMessages)
            {
                string anthropicRole = msg["role"]?.ToString();
                string geminiRole = anthropicRole == "assistant" ? "model" : "user";
                var content = msg["content"];

                var parts = new JArray();

                if (content is JValue contentVal && contentVal.Type == JTokenType.String)
                {
                    parts.Add(new JObject { ["text"] = contentVal.ToString() });
                }
                else if (content is JArray contentArray)
                {
                    foreach (JObject block in contentArray)
                    {
                        string blockType = block["type"]?.ToString();
                        switch (blockType)
                        {
                            case "text":
                                parts.Add(new JObject { ["text"] = block["text"]?.ToString() ?? "" });
                                break;

                            case "tool_use":
                                {
                                    var fnCall = new JObject
                                    {
                                        ["name"] = block["name"]?.ToString(),
                                        ["args"] = block["input"] ?? new JObject()
                                    };
                                    // Echo back the thoughtSignature captured from the
                                    // original Gemini response. Required for Gemini 3.x —
                                    // missing it fails the next turn with HTTP 400
                                    // "missing thought_signature". See BIBIM-006.
                                    JToken sig = block["_geminiThoughtSignature"];
                                    if (sig != null && sig.Type != JTokenType.Null)
                                        fnCall["thoughtSignature"] = sig;
                                    parts.Add(new JObject { ["functionCall"] = fnCall });
                                }
                                break;

                            case "tool_result":
                                parts.Add(new JObject
                                {
                                    ["functionResponse"] = new JObject
                                    {
                                        ["name"] = block["name"]?.ToString() ?? "tool_result",
                                        ["response"] = new JObject
                                        {
                                            ["content"] = block["content"]?.ToString() ?? ""
                                        }
                                    }
                                });
                                break;
                        }
                    }
                }

                if (parts.Count == 0) continue;

                contents.Add(new JObject
                {
                    ["role"] = geminiRole,
                    ["parts"] = parts
                });
            }

            return contents;
        }

        /// <summary>
        /// Convert Anthropic-shaped tools to Gemini functionDeclarations.
        /// Gemini uses OpenAPI-flavoured JSON Schema, similar to Anthropic's input_schema.
        /// </summary>
        private static JArray TranslateToolsToGemini(JArray anthropicTools)
        {
            var declarations = new JArray();
            if (anthropicTools == null) return declarations;

            foreach (JObject tool in anthropicTools)
            {
                declarations.Add(new JObject
                {
                    ["name"] = tool["name"]?.ToString(),
                    ["description"] = tool["description"]?.ToString(),
                    ["parameters"] = tool["input_schema"] ?? new JObject()
                });
            }
            return declarations;
        }

        /// <summary>
        /// Re-shape a Gemini generateContent response into Anthropic-style.
        /// Maps:
        ///   • parts[].text → text blocks
        ///   • parts[].functionCall → tool_use blocks (synthesised id since Gemini lacks one)
        ///   • finishReason "STOP" / "MAX_TOKENS" / function-call presence → stop_reason
        ///   • usageMetadata → usage
        /// </summary>
        private static JObject TranslateResponseToAnthropicShape(JObject geminiResponse)
        {
            var content = new JArray();
            string stopReason = "end_turn";
            int callCounter = 0;

            var candidates = geminiResponse["candidates"] as JArray;
            if (candidates != null && candidates.Count > 0)
            {
                var firstCandidate = candidates[0] as JObject;
                string finishReason = firstCandidate?["finishReason"]?.ToString();

                var parts = firstCandidate?["content"]?["parts"] as JArray;
                if (parts != null)
                {
                    foreach (JObject part in parts)
                    {
                        if (part["text"] != null)
                        {
                            content.Add(new JObject
                            {
                                ["type"] = "text",
                                ["text"] = part["text"]?.ToString() ?? ""
                            });
                        }
                        else if (part["functionCall"] != null)
                        {
                            var fnCall = part["functionCall"];
                            string syntheticId = $"gemini_call_{Guid.NewGuid():N}".Substring(0, 24);
                            var toolUse = new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = syntheticId,
                                ["name"] = fnCall["name"]?.ToString(),
                                ["input"] = fnCall["args"] ?? new JObject()
                            };
                            // Gemini 3.x thinking models attach a thoughtSignature on the
                            // first functionCall part of each step (sometimes also on
                            // sequential calls). It MUST be echoed back when the assistant
                            // turn is replayed in subsequent requests, otherwise Gemini
                            // returns 400 "missing thought_signature". We stash it on the
                            // tool_use block under a Gemini-private field that other
                            // providers (Anthropic / OpenAI) silently ignore.
                            JToken sigToken = part["thoughtSignature"];
                            if (sigToken != null && sigToken.Type != JTokenType.Null)
                                toolUse["_geminiThoughtSignature"] = sigToken;
                            content.Add(toolUse);
                            stopReason = "tool_use";
                            callCounter++;
                        }
                    }
                }

                // If finishReason is MAX_TOKENS and we didn't see a tool call, surface it
                if (callCounter == 0 && string.Equals(finishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase))
                    stopReason = "max_tokens";
            }

            var geminiUsage = geminiResponse["usageMetadata"] ?? new JObject();
            var usage = new JObject
            {
                ["input_tokens"] = geminiUsage["promptTokenCount"] ?? 0,
                ["output_tokens"] = geminiUsage["candidatesTokenCount"] ?? 0,
                ["cache_read_input_tokens"] = geminiUsage["cachedContentTokenCount"] ?? 0
            };

            return new JObject
            {
                ["content"] = content,
                ["stop_reason"] = stopReason,
                ["usage"] = usage,
                ["model"] = geminiResponse["modelVersion"] ?? ""
            };
        }
    }
}
