// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// LLM Orchestration Service — replaces legacy GeminiService.
    /// Design doc 2.2 — Anthropic SDK Agent Loop with Tool Use.
    /// 
    /// Uses official Anthropic C# SDK (NuGet: Anthropic v12+) for:
    ///   - Streaming responses (CreateStreaming)
    ///   - Agent Loop (compile, fix, recompile)
    ///   - Token usage capture from Message.Usage
    /// </summary>
    public class LlmOrchestrationService
    {
        private const string ClaudeApiUrl = "https://api.anthropic.com/v1/messages";
        private static readonly HttpClient _httpClient = CreateHttpClient();

        private readonly AnthropicClient _client;
        private readonly RoslynCompilerService _compiler;
        private readonly string _model;
        private readonly string _apiKey;

        /// <summary>
        /// Fired during streaming to push partial text to the UI.
        /// </summary>
        public event Action<string> OnStreamingDelta;

        /// <summary>
        /// Fired when streaming status changes (for UI progress display).
        /// </summary>
        public event Action<string> OnStatusUpdate;

        /// <summary>
        /// Fired when token usage is captured.
        /// </summary>
        public event Action<TokenUsageInfo> OnTokenUsage;

        public LlmOrchestrationService(string apiKey, RoslynCompilerService compiler, string model = null)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("API key is required.", nameof(apiKey));

            _apiKey = apiKey;
            _client = new AnthropicClient() { ApiKey = apiKey };
            _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
            _model = model ?? ConfigService.GetRagConfig().ClaudeModel;
        }

        /// <summary>
        /// 2-tier API key resolution: config → env var.
        /// No hardcoded fallback keys for security.
        /// </summary>
        public static string ResolveApiKey()
        {
            try
            {
                // 1. Config file
                var config = ConfigService.GetRagConfig();
                if (config != null && !string.IsNullOrEmpty(config.ClaudeApiKey) &&
                    config.ClaudeApiKey != "CLAUDE_API_KEY_HERE")
                {
                    Logger.Log("Anthropic", "API key from config file");
                    return config.ClaudeApiKey;
                }

                // 2. Environment variable
                string envKey = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
                if (!string.IsNullOrEmpty(envKey))
                {
                    Logger.Log("Anthropic", "API key from env var");
                    return envKey;
                }

                Logger.Log("Anthropic", "No API key found in config or env var");
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError("ResolveApiKey", ex);
                return null;
            }
        }

        /// <summary>
        /// Send a chat message with streaming response.
        /// Uses CreateStreaming for real-time token delivery to UI,
        /// then captures token usage via a non-streaming Create call
        /// is NOT needed — usage is extracted from streaming events
        /// via reflection on the MessageStreamEvent objects.
        /// </summary>
        public async Task<LlmResponse> SendMessageAsync(
            List<ChatMessage> history,
            string systemPrompt,
            CancellationToken ct = default,
            int maxTokens = 8192)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            var response = new LlmResponse { RequestId = requestId };

            try
            {
                OnStatusUpdate?.Invoke("Generating response...");

#if NET48
                Logger.Log("LlmOrchestration",
                    "net48 target detected, using raw SSE transport instead of Anthropic SDK streaming");
                await SendMessageWithRawHttpAsync(history, systemPrompt, requestId, sw, response, ct, maxTokens);
#else
                try
                {
                    await SendMessageWithSdkAsync(history, systemPrompt, requestId, sw, response, ct, maxTokens);
                }
                catch (Exception ex) when (ShouldFallbackToRawHttp(ex))
                {
                    Logger.Log("LlmOrchestration",
                        $"SDK unavailable, switching to raw SSE fallback: {ex.GetType().Name} - {ex.Message}");
                    await SendMessageWithRawHttpAsync(history, systemPrompt, requestId, sw, response, ct, maxTokens);
                }
#endif

                OnStatusUpdate?.Invoke(null);
            }
            catch (OperationCanceledException)
            {
                response.Success = false;
                response.ErrorMessage = "Request cancelled.";
                throw;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.ErrorMessage = ex.Message;
                response.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                Logger.LogError("LlmOrchestration", ex);
            }

            return response;
        }

        private async Task SendMessageWithSdkAsync(
            List<ChatMessage> history,
            string systemPrompt,
            string requestId,
            Stopwatch sw,
            LlmResponse response,
            CancellationToken ct,
            int maxTokens = 8192)
        {
            // Build message list per official Anthropic C# SDK
            var messages = new List<MessageParam>();
            foreach (var m in history)
            {
                messages.Add(new MessageParam
                {
                    Role = m.IsUser ? Role.User : Role.Assistant,
                    Content = m.Text
                });
            }

            var parameters = new MessageCreateParams
            {
                Model = _model,
                MaxTokens = maxTokens,
                System = systemPrompt,
                Messages = messages
            };

            var fullText = new StringBuilder();
            int inputTokens = 0, outputTokens = 0;
            var allEvents = new List<object>();

            await foreach (var evt in _client.Messages.CreateStreaming(parameters).WithCancellation(ct))
            {
                allEvents.Add(evt);

                // Extract text delta via reflection — evt.ToString() returns a type name, not content.
                // ContentBlockDeltaEvent.Delta.Text is the actual streamed text.
                string deltaText = TryExtractDeltaText(evt);
                if (!string.IsNullOrEmpty(deltaText))
                {
                    fullText.Append(deltaText);
                    OnStreamingDelta?.Invoke(deltaText);
                }
            }

            ExtractUsageFromEvents(allEvents, out inputTokens, out outputTokens);
            FinalizeSuccessfulResponse(response, requestId, sw, fullText.ToString(), inputTokens, outputTokens);
        }

        private async Task SendMessageWithRawHttpAsync(
            List<ChatMessage> history,
            string systemPrompt,
            string requestId,
            Stopwatch sw,
            LlmResponse response,
            CancellationToken ct,
            int maxTokens = 8192)
        {
            var messages = new JArray();
            foreach (var message in history)
            {
                messages.Add(new JObject
                {
                    ["role"] = message.IsUser ? "user" : "assistant",
                    ["content"] = message.Text ?? string.Empty
                });
            }

            var requestBody = new JObject
            {
                ["model"] = _model,
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
                ["messages"] = messages
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, ClaudeApiUrl))
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
                            $"Anthropic fallback failed: {(int)httpResponse.StatusCode} {httpResponse.StatusCode} - {error}");
                    }

                    using (var stream = await httpResponse.Content.ReadAsStreamAsync())
                    using (var reader = new StreamReader(stream))
                    {
                        var fullText = new StringBuilder();
                        int inputTokens = 0;
                        int outputTokens = 0;
                        string currentEvent = null;
                        var currentData = new StringBuilder();

                        string line;
                        while ((line = await reader.ReadLineAsync()) != null)
                        {
                            if (line.Length == 0)
                            {
                                ProcessSseEvent(
                                    currentEvent,
                                    currentData.ToString(),
                                    fullText,
                                    ref inputTokens,
                                    ref outputTokens);
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
                                if (currentData.Length > 0)
                                    currentData.AppendLine();
                                currentData.Append(line.Substring("data:".Length).TrimStart());
                            }
                        }

                        ProcessSseEvent(
                            currentEvent,
                            currentData.ToString(),
                            fullText,
                            ref inputTokens,
                            ref outputTokens);

                        FinalizeSuccessfulResponse(
                            response,
                            requestId,
                            sw,
                            fullText.ToString(),
                            inputTokens,
                            outputTokens);
                    }
                }
            }
        }

        private void ProcessSseEvent(
            string eventName,
            string data,
            StringBuilder fullText,
            ref int inputTokens,
            ref int outputTokens)
        {
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
                return;

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
                                OnStreamingDelta?.Invoke(deltaText);
                            }
                        }
                        break;

                    case "message_start":
                        inputTokens = payload["message"]?["usage"]?["input_tokens"]?.Value<int>() ?? inputTokens;
                        outputTokens = payload["message"]?["usage"]?["output_tokens"]?.Value<int>() ?? outputTokens;
                        break;

                    case "message_delta":
                        outputTokens = payload["usage"]?["output_tokens"]?.Value<int>() ?? outputTokens;
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("LlmOrchestration", $"SSE parse skipped: {ex.Message}");
            }
        }

        private void FinalizeSuccessfulResponse(
            LlmResponse response,
            string requestId,
            Stopwatch sw,
            string text,
            int inputTokens,
            int outputTokens)
        {
            response.Text = text;
            response.InputTokens = inputTokens;
            response.OutputTokens = outputTokens;
            response.Success = true;

            var usageInfo = new TokenUsageInfo
            {
                RequestId = requestId,
                Model = _model,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                ElapsedMs = sw.ElapsedMilliseconds
            };
            OnTokenUsage?.Invoke(usageInfo);

            Logger.Log("LlmOrchestration",
                $"rid={requestId} in={inputTokens} out={outputTokens} ms={sw.ElapsedMilliseconds}");
        }

        private static bool ShouldFallbackToRawHttp(Exception ex)
        {
            while (ex != null)
            {
                if (ex is MissingMethodException)
                {
                    return true;
                }

                if (ex is FileLoadException ||
                    ex is FileNotFoundException ||
                    ex is TypeLoadException)
                {
                    if (ex.Message?.IndexOf("System.Text.Json", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ex.Message?.IndexOf("Anthropic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ex.Message?.IndexOf("IAsyncEnumerable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ex.Message?.IndexOf("IAsyncEnumerator", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ex.Message?.IndexOf("MoveNextAsync", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                ex = ex.InnerException;
            }

            return false;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(5)
            };
            return client;
        }

        /// <summary>
        /// Extract text delta from a SDK streaming event object via reflection.
        /// ContentBlockDeltaEvent exposes Delta.Text for text_delta type events.
        /// Returns null if this event carries no text content.
        /// </summary>
        private static string TryExtractDeltaText(object evt)
        {
            if (evt == null) return null;
            try
            {
                var deltaProp = evt.GetType().GetProperty("Delta");
                if (deltaProp == null) return null;
                var delta = deltaProp.GetValue(evt);
                if (delta == null) return null;
                var textProp = delta.GetType().GetProperty("Text");
                if (textProp == null) return null;
                return textProp.GetValue(delta) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract token usage from streaming event objects using reflection.
        /// Handles the official SDK's MessageStreamEvent structure where:
        ///   - Some events have a Message property with Usage.InputTokens
        ///   - Some events have a direct Usage property with OutputTokens
        /// Falls back gracefully to 0 if properties are not found.
        /// </summary>
        private void ExtractUsageFromEvents(List<object> events, out int inputTokens, out int outputTokens)
        {
            inputTokens = 0;
            outputTokens = 0;

            foreach (var evt in events)
            {
                if (evt == null) continue;
                var evtType = evt.GetType();

                try
                {
                    // Try to get Usage.InputTokens from Message property (message_start)
                    var messageProp = evtType.GetProperty("Message");
                    if (messageProp != null)
                    {
                        var message = messageProp.GetValue(evt);
                        if (message != null)
                        {
                            var usageProp = message.GetType().GetProperty("Usage");
                            if (usageProp != null)
                            {
                                var usage = usageProp.GetValue(message);
                                if (usage != null)
                                {
                                    var inProp = usage.GetType().GetProperty("InputTokens");
                                    if (inProp != null)
                                    {
                                        int val = Convert.ToInt32(inProp.GetValue(usage));
                                        if (val > 0) inputTokens = val;
                                    }
                                }
                            }
                        }
                    }

                    // Try to get Usage.OutputTokens from direct Usage property (message_delta)
                    var directUsageProp = evtType.GetProperty("Usage");
                    if (directUsageProp != null)
                    {
                        var usage = directUsageProp.GetValue(evt);
                        if (usage != null)
                        {
                            var outProp = usage.GetType().GetProperty("OutputTokens");
                            if (outProp != null)
                            {
                                int val = Convert.ToInt32(outProp.GetValue(usage));
                                if (val > 0) outputTokens = val;
                            }
                        }
                    }
                }
                catch
                {
                    // Reflection failure is non-fatal — just skip this event
                }
            }
        }

        /// <summary>
        /// Non-streaming single-turn call for lightweight requests (e.g. Task Planner).
        /// Cheaper and faster than SSE streaming for short JSON-only responses.
        /// </summary>
        public async Task<LlmResponse> SendMessageNonStreamingAsync(
            List<ChatMessage> history,
            string systemPrompt,
            int maxTokens = 1024,
            CancellationToken ct = default)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var sw = Stopwatch.StartNew();
            var response = new LlmResponse { RequestId = requestId };

            try
            {
                var messages = new JArray();
                foreach (var m in history)
                {
                    messages.Add(new JObject
                    {
                        ["role"] = m.IsUser ? "user" : "assistant",
                        ["content"] = m.Text ?? string.Empty
                    });
                }

                var requestBody = new JObject
                {
                    ["model"] = _model,
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
                    ["stream"] = false,
                    ["messages"] = messages
                };

                using (var request = new HttpRequestMessage(HttpMethod.Post, ClaudeApiUrl))
                {
                    request.Content = new StringContent(
                        requestBody.ToString(Formatting.None),
                        Encoding.UTF8,
                        "application/json");
                    request.Headers.Add("x-api-key", _apiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                    request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

                    using (var httpResponse = await _httpClient.SendAsync(request, ct))
                    {
                        string body = await httpResponse.Content.ReadAsStringAsync();
                        if (!httpResponse.IsSuccessStatusCode)
                            throw new HttpRequestException(
                                $"Anthropic non-streaming {(int)httpResponse.StatusCode}: {body}");

                        var payload = JObject.Parse(body);
                        var content = payload["content"] as JArray ?? new JArray();
                        string text = ExtractTextFromContent(content);
                        int inputTokens = payload["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                        int outputTokens = payload["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                        FinalizeSuccessfulResponse(response, requestId, sw, text, inputTokens, outputTokens);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                response.Success = false;
                response.ErrorMessage = "Request cancelled.";
                throw;
            }
            catch (Exception ex)
            {
                response.Success = false;
                response.ErrorMessage = ex.Message;
                Logger.LogError("LlmOrchestration.NonStreaming", ex);
            }

            return response;
        }

        private string ExtractCSharpCode(string responseText)
        {
            if (string.IsNullOrEmpty(responseText)) return null;

            string[] markers = { "```csharp", "```cs", "```C#" };
            foreach (var marker in markers)
            {
                int start = responseText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start = responseText.IndexOf('\n', start);
                if (start < 0) continue;
                start++;
                int end = responseText.IndexOf("```", start, StringComparison.Ordinal);
                if (end < 0) end = responseText.Length;
                return responseText.Substring(start, end - start).Trim();
            }
            return null;
        }

        private string BuildCompileErrorFeedback(CompilationResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[COMPILE_ERROR] The generated C# code failed Roslyn compilation. Fix the errors below and regenerate ONLY the corrected code.");
            sb.AppendLine();
            sb.AppendLine(result.ErrorSummary);
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- Fix ONLY the compilation errors listed above");
            sb.AppendLine("- Do NOT change the logic or add new features");
            sb.AppendLine("- Return ONLY a ```csharp``` block containing statements for the body of Execute(UIApplication uiApp)");
            sb.AppendLine("- Do NOT include using directives, namespace, class, method signature, or explanation");
            sb.AppendLine("- NEVER re-declare app, doc, or uidoc — they already exist in scope");
            return sb.ToString();
        }

        /// <summary>
        /// Agent loop using Claude Tool Use API (non-streaming).
        /// Claude drives the loop: calls tools (search_revit_api, run_roslyn_check, etc.)
        /// until it produces a final text response with compiled code.
        ///
        /// Replaces the manual HandleStreamingResponseAsync + GenerateAndCompileAsync chain.
        /// </summary>
        public async Task<CodeGenerationResult> GenerateWithToolsAsync(
            List<ChatMessage> history,
            string systemPrompt,
            JArray toolDefinitions,
            Func<string, string, CancellationToken, Task<string>> toolExecutor,
            int maxTurns = 10,
            string debugDirectory = null,
            CancellationToken ct = default)
        {
            var requestId = Guid.NewGuid().ToString("N").Substring(0, 8);
            var result = new CodeGenerationResult
            {
                RequestId = requestId,
                DebugArtifactDirectory = debugDirectory
            };

            // Build the initial messages array for the Anthropic API
            var messages = new JArray();
            foreach (var m in history)
            {
                messages.Add(new JObject
                {
                    ["role"] = m.IsUser ? "user" : "assistant",
                    ["content"] = m.Text ?? string.Empty
                });
            }

            try
            {
                for (int turn = 0; turn < maxTurns; turn++)
                {
                    ct.ThrowIfCancellationRequested();

                    OnStatusUpdate?.Invoke(turn == 0 ? "Generating code..." : "Thinking...");
                    Logger.Log("LlmOrchestration", $"rid={requestId} tool-turn={turn}");

                    // POST non-streaming request
                    JObject response;
                    try
                    {
                        response = await PostNonStreamingAsync(
                            messages, systemPrompt, toolDefinitions, ct);
                    }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"API request failed: {ex.Message}";
                        result.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                        Logger.LogError("LlmOrchestration.GenerateWithToolsAsync", ex);
                        return result;
                    }

                    // Capture token usage
                    int inTok = response["usage"]?["input_tokens"]?.Value<int>() ?? 0;
                    int outTok = response["usage"]?["output_tokens"]?.Value<int>() ?? 0;
                    result.TotalInputTokens += inTok;
                    result.TotalOutputTokens += outTok;
                    OnTokenUsage?.Invoke(new TokenUsageInfo
                    {
                        RequestId = requestId,
                        Model = _model,
                        InputTokens = inTok,
                        OutputTokens = outTok,
                        ElapsedMs = 0
                    });

                    CodegenDebugRecorder.WriteJson(debugDirectory,
                        $"tool_turn_{turn:00}_response.json", response);

                    string stopReason = response["stop_reason"]?.ToString();
                    var content = response["content"] as JArray ?? new JArray();

                    // ── end_turn / max_tokens: Claude finished or was truncated ──
                    if (stopReason == "end_turn" || stopReason == "max_tokens")
                    {
                        if (stopReason == "max_tokens")
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} turn={turn} response truncated by max_tokens");

                        string finalText = ExtractTextFromContent(content);
                        string code = ExtractCSharpCode(finalText);

                        result.RawResponse = finalText;

                        if (string.IsNullOrWhiteSpace(code))
                        {
                            // max_tokens truncation mid-generation — request continuation
                            if (stopReason == "max_tokens" && turn < maxTurns - 1)
                            {
                                messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                                messages.Add(new JObject
                                {
                                    ["role"] = "user",
                                    ["content"] = "[CONTINUATION_REQUIRED] Your response was cut off. " +
                                        "Continue EXACTLY where you left off. Do NOT repeat code already generated."
                                });
                                continue;
                            }

                            // Non-code response (e.g. clarification)
                            result.Success = true;
                            result.IsCodeResponse = false;
                            OnStatusUpdate?.Invoke(null);
                            return result;
                        }

                        result.GeneratedCode = code;
                        result.IsCodeResponse = true;
                        result.CompileAttempts = turn + 1;

                        // Final Roslyn compile
                        OnStatusUpdate?.Invoke("Compiling...");
                        var compileResult = _compiler.Compile(code);
                        result.CompilationResult = compileResult;

                        if (compileResult.Success)
                        {
                            result.Success = true;
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} tool-loop done turns={turn + 1} compile=OK");
                            OnStatusUpdate?.Invoke(null);
                            return result;
                        }

                        // Compile failed — feed error back and retry if turns remain
                        if (turn < maxTurns - 1)
                        {
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} turn={turn} compile failed, feeding error back");
                            messages.Add(new JObject { ["role"] = "assistant", ["content"] = content });
                            messages.Add(new JObject
                            {
                                ["role"] = "user",
                                ["content"] = BuildCompileErrorFeedback(compileResult)
                            });
                            continue;
                        }

                        result.Success = false;
                        result.ErrorMessage = $"Final compilation failed:\n{compileResult.ErrorSummary}";
                        Logger.Log("LlmOrchestration",
                            $"rid={requestId} tool-loop exhausted turns={turn + 1} compile=FAIL");
                        OnStatusUpdate?.Invoke(null);
                        return result;
                    }

                    // ── tool_use: execute each tool, feed results back ──
                    if (stopReason == "tool_use")
                    {
                        // Add assistant's tool_use message to history
                        messages.Add(new JObject
                        {
                            ["role"] = "assistant",
                            ["content"] = content
                        });

                        var toolResults = new JArray();
                        foreach (JObject block in content)
                        {
                            if (block["type"]?.ToString() != "tool_use") continue;

                            string toolId = block["id"]?.ToString();
                            string toolName = block["name"]?.ToString();
                            string toolInput = block["input"]?.ToString(Formatting.None) ?? "{}";

                            // Guard: malformed tool_use block — API requires non-null tool_use_id
                            if (string.IsNullOrEmpty(toolId) || string.IsNullOrEmpty(toolName))
                            {
                                Logger.Log("LlmOrchestration",
                                    $"rid={requestId} malformed tool_use block: id={toolId ?? "null"}, name={toolName ?? "null"} — skipping");
                                continue;
                            }

                            OnStatusUpdate?.Invoke($"Using {toolName}...");
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} tool={toolName}");

                            string toolOutput;
                            try
                            {
                                toolOutput = await toolExecutor(toolName, toolInput, ct);
                            }
                            catch (Exception ex)
                            {
                                toolOutput = $"[Tool Error] {ex.Message}";
                                Logger.LogError($"LlmOrchestration.tool.{toolName}", ex);
                            }

                            CodegenDebugRecorder.WriteText(debugDirectory,
                                $"tool_turn_{turn:00}_{toolName}_result.txt", toolOutput);

                            toolResults.Add(new JObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = toolId,
                                ["content"] = toolOutput
                            });
                        }

                        // Guard: no valid tool blocks found (e.g. all were malformed)
                        if (toolResults.Count == 0)
                        {
                            Logger.Log("LlmOrchestration",
                                $"rid={requestId} turn={turn} stop_reason=tool_use but no valid tool blocks found");
                            result.Success = false;
                            result.ErrorMessage = "Claude returned tool_use stop reason but no valid tool blocks were found.";
                            OnStatusUpdate?.Invoke(null);
                            return result;
                        }

                        // Add tool results as user message
                        messages.Add(new JObject
                        {
                            ["role"] = "user",
                            ["content"] = toolResults
                        });

                        continue;
                    }

                    // Unexpected stop reason
                    result.Success = false;
                    result.ErrorMessage = $"Unexpected stop_reason: {stopReason}";
                    OnStatusUpdate?.Invoke(null);
                    return result;
                }

                result.Success = false;
                result.ErrorMessage = $"Tool loop exceeded max_turns ({maxTurns}).";
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.ErrorMessage = "Cancelled.";
                throw;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.IsContextLengthExceeded = IsContextLengthError(ex.Message);
                Logger.LogError("LlmOrchestration.GenerateWithToolsAsync", ex);
            }

            OnStatusUpdate?.Invoke(null);
            return result;
        }

        private static bool IsContextLengthError(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("prompt is too long", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("context_length_exceeded", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Non-streaming POST to /v1/messages. Used by the tool use loop.
        /// Works on both net48 and net8 targets.
        /// </summary>
        private async Task<JObject> PostNonStreamingAsync(
            JArray messages,
            string systemPrompt,
            JArray tools,
            CancellationToken ct,
            int maxTokens = 8192)
        {
            var requestBody = new JObject
            {
                ["model"] = _model,
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
                ["messages"] = messages,
                ["tools"] = tools
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, ClaudeApiUrl))
            {
                request.Content = new StringContent(
                    requestBody.ToString(Formatting.None),
                    Encoding.UTF8,
                    "application/json");
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

                using (var httpResponse = await _httpClient.SendAsync(request, ct))
                {
                    string body = await httpResponse.Content.ReadAsStringAsync();
                    if (!httpResponse.IsSuccessStatusCode)
                        throw new HttpRequestException(
                            $"Anthropic API {(int)httpResponse.StatusCode}: {body}");
                    return JObject.Parse(body);
                }
            }
        }

        private static string ExtractTextFromContent(JArray content)
        {
            var sb = new StringBuilder();
            if (content == null) return string.Empty;
            foreach (JObject block in content)
            {
                if (block["type"]?.ToString() == "text")
                    sb.Append(block["text"]?.ToString());
            }
            return sb.ToString();
        }
    }

    public class LlmResponse
    {
        public string RequestId { get; set; }
        public bool Success { get; set; }
        public string Text { get; set; }
        public string ErrorMessage { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public bool IsContextLengthExceeded { get; set; }
    }

    public class CodeGenerationResult
    {
        public string RequestId { get; set; }
        public bool Success { get; set; }
        public bool IsCodeResponse { get; set; }
        public string RawResponse { get; set; }
        public string GeneratedCode { get; set; }
        public string ErrorMessage { get; set; }
        public CompilationResult CompilationResult { get; set; }
        public int CompileAttempts { get; set; }
        public int TotalInputTokens { get; set; }
        public int TotalOutputTokens { get; set; }
        public string DebugArtifactDirectory { get; set; }
        public bool IsContextLengthExceeded { get; set; }
    }

    public class TokenUsageInfo
    {
        public string RequestId { get; set; }
        public string Model { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public long ElapsedMs { get; set; }
    }
}
