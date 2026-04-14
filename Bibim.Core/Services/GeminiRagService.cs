// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// RAG retrieval service using Gemini fileSearch API.
    /// Ported from v2 GeminiService.FetchRelevantApiDocsAsync.
    /// 
    /// Flow:
    ///   1. Extract Revit API keywords from user query / spec
    ///   2. Call Gemini API with fileSearch tool pointing to version-specific store
    ///   3. Return structured API documentation for injection into Claude prompt
    /// </summary>
    public class GeminiRagService
    {
        private const string GeminiApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly TimeSpan RagTimeout = TimeSpan.FromSeconds(20);

        // Common Revit API class names for keyword extraction
        private static readonly string[] RevitApiPatterns = {
            "FilteredElementCollector", "Element", "Wall", "Floor", "Ceiling", "Door", "Window",
            "Room", "FamilyInstance", "FamilySymbol", "Parameter", "BuiltInParameter", "BuiltInCategory",
            "Transaction", "Document", "ElementId", "XYZ", "Line", "Curve", "CurveLoop", "Solid",
            "View", "ViewPlan", "ViewSection", "ViewSheet", "Schedule", "Level", "Grid",
            "Material", "Category", "Workset", "Connector", "Pipe", "Duct",
            "UnitUtils", "UnitTypeId", "Selection", "Reference", "BoundingBoxXYZ", "Transform"
        };

        /// <summary>
        /// Fetch relevant Revit API documentation from the version-specific RAG store.
        /// Returns the API context text, or empty string if unavailable.
        /// </summary>
        public static async Task<RagFetchResult> FetchAsync(
            string queryText,
            string revitVersion,
            CancellationToken ct = default)
        {
            var config = ConfigService.GetRagConfig();
            string geminiApiKey = config?.GeminiApiKey;
            string geminiModel = config?.GeminiModel;

            if (string.IsNullOrEmpty(geminiApiKey) || geminiApiKey == "GEMINI_API_KEY_HERE")
            {
                // Try environment variable
                geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            }

            if (string.IsNullOrEmpty(geminiApiKey))
                return new RagFetchResult { Status = "no_api_key" };

            if (string.IsNullOrEmpty(geminiModel))
                geminiModel = "gemini-2.0-flash";

            string ragStore = ConfigService.GetRagStoreForVersion(revitVersion);
            if (string.IsNullOrEmpty(ragStore))
                return new RagFetchResult { Status = "no_store" };

            return await FetchRelevantApiDocsAsync(
                geminiApiKey, queryText, ragStore, revitVersion, geminiModel);
        }

        private static async Task<RagFetchResult> FetchRelevantApiDocsAsync(
            string apiKey, string queryText, string ragStoreName,
            string revitVersion, string model)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string requestUrl = $"{GeminiApiBaseUrl}{model}:generateContent?key={apiKey}";

                string queryPrompt = BuildRagQueryPrompt(revitVersion, queryText);

                var requestBody = new JObject
                {
                    ["contents"] = new JArray
                    {
                        new JObject
                        {
                            ["role"] = "user",
                            ["parts"] = new JArray { new JObject { ["text"] = queryPrompt } }
                        }
                    },
                    ["tools"] = new JArray
                    {
                        new JObject
                        {
                            ["fileSearch"] = new JObject
                            {
                                ["fileSearchStoreNames"] = new JArray { ragStoreName }
                            }
                        }
                    },
                    ["generationConfig"] = new JObject
                    {
                        ["temperature"] = 0.0,
                        ["maxOutputTokens"] = 2048
                    }
                };

                var jsonContent = new StringContent(
                    requestBody.ToString(Newtonsoft.Json.Formatting.None),
                    Encoding.UTF8, "application/json");

                using (var ragCts = new CancellationTokenSource(RagTimeout))
                {
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, requestUrl))
                        {
                            request.Content = jsonContent;
                            var response = await _httpClient.SendAsync(request, ragCts.Token);

                            if (!response.IsSuccessStatusCode)
                            {
                                sw.Stop();
                                Logger.Log("GeminiRag", $"HTTP {response.StatusCode} ({sw.ElapsedMilliseconds}ms)");
                                return new RagFetchResult
                                {
                                    Status = "http_error",
                                    ErrorSummary = $"HTTP {response.StatusCode}",
                                    ElapsedMs = sw.ElapsedMilliseconds
                                };
                            }

                            string responseString = await response.Content.ReadAsStringAsync();
                            var result = ParseGeminiResponse(responseString);
                            sw.Stop();
                            result.ElapsedMs = sw.ElapsedMilliseconds;
                            Logger.Log("GeminiRag", $"Status={result.Status} elapsed={sw.ElapsedMilliseconds}ms");
                            return result;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        sw.Stop();
                        Logger.Log("GeminiRag", $"Status=timeout elapsed={sw.ElapsedMilliseconds}ms (limit={RagTimeout.TotalSeconds}s)");
                        return new RagFetchResult
                        {
                            Status = "timeout",
                            ErrorSummary = $"RAG query timed out ({(int)RagTimeout.TotalSeconds}s)",
                            ElapsedMs = sw.ElapsedMilliseconds
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.Log("GeminiRag", $"Exception: {ex.GetType().Name}: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
                return new RagFetchResult
                {
                    Status = "exception",
                    ErrorSummary = $"{ex.GetType().Name}: {ex.Message}",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
        }

        private static RagFetchResult ParseGeminiResponse(string responseString)
        {
            try
            {
                var responseObj = JObject.Parse(responseString);
                var candidates = responseObj["candidates"];
                if (candidates != null && candidates.HasValues)
                {
                    var content = candidates[0]["content"];
                    var parts = content?["parts"];
                    if (parts != null && parts.HasValues)
                    {
                        var textBuilder = new StringBuilder();
                        foreach (var part in parts)
                        {
                            if (part["text"] != null)
                                textBuilder.Append(part["text"].ToString());
                        }
                        string result = textBuilder.ToString().Trim();

                        if (result.Contains("NO_RELEVANT_API_FOUND"))
                        {
                            Logger.Log("GeminiRag", "RAG result: no_match");
                            return new RagFetchResult { Status = "no_match" };
                        }

                        Logger.Log("GeminiRag", $"RAG result: hit, {result.Length} chars");
                        return new RagFetchResult { ContextText = result, Status = "hit" };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("GeminiRag", $"Parse error: {ex.Message}");
            }

            return new RagFetchResult { Status = "no_match", ErrorSummary = "Empty response" };
        }

        /// <summary>
        /// Extract Revit API keywords from query text for focused RAG search.
        /// </summary>
        public static string ExtractRagKeywords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string lower = text.ToLowerInvariant();

            foreach (var pattern in RevitApiPatterns)
            {
                if (lower.Contains(pattern.ToLowerInvariant()))
                    keywords.Add(pattern);
            }

            // Also extract PascalCase words that look like API types
            foreach (Match m in Regex.Matches(text, @"\b([A-Z][a-z]+(?:[A-Z][a-z]+)+)\b"))
            {
                string word = m.Groups[1].Value;
                if (word.Length >= 4 && word.Length <= 40)
                    keywords.Add(word);
            }

            if (keywords.Count == 0)
                return text.Length > 500 ? text.Substring(0, 500) : text;

            return "Revit API keywords for lookup: " + string.Join(", ", keywords);
        }

        private static string BuildRagQueryPrompt(string revitVersion, string queryText)
        {
            return $@"You are a Revit {revitVersion} API documentation expert.

Based on the following request, identify and retrieve the relevant Revit API documentation.

[REQUEST]
{queryText}

[YOUR TASK]
1. Identify which Revit API classes, methods, and properties are needed
2. Use RAG (File Search) to find the exact API documentation for Revit {revitVersion}
3. Return a concise summary of the relevant API information including:
   - Class names and their namespaces
   - Method signatures with parameter types
   - Property names and their return types
   - Any version-specific notes for Revit {revitVersion}

[OUTPUT FORMAT]
Return the API documentation in this format:
```
=== RELEVANT REVIT {revitVersion} API DOCUMENTATION ===

[Class: ClassName]
- Namespace: Autodesk.Revit.DB
- Method: MethodName(param1Type, param2Type) -> returnType
- Property: PropertyName -> Type

[VERSION NOTES for Revit {revitVersion}]
- Any breaking changes or deprecated APIs
- Recommended alternatives
```

[CONTEXT]
- The target code is C# running inside a Revit Add-in (IExternalEventHandler)
- Entry point: public static object Execute(UIApplication uiApp)
- Focus on: class names, method names, parameter types, return types

If no relevant API is found, return ""NO_RELEVANT_API_FOUND"".";
        }
    }

    /// <summary>
    /// Result of a RAG fetch operation.
    /// </summary>
    public class RagFetchResult
    {
        public string ContextText { get; set; } = "";
        public string Status { get; set; } = "unknown";
        public string ErrorSummary { get; set; }
        public long ElapsedMs { get; set; }
        public bool HasContext => !string.IsNullOrWhiteSpace(ContextText);
    }
}
