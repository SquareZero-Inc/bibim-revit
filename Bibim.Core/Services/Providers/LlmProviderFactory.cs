// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Net.Http;

namespace Bibim.Core
{
    /// <summary>
    /// Resolves a model id to its provider name and constructs the matching ILlmProvider.
    ///
    /// Supported models in v1.1.0:
    ///   anthropic → claude-sonnet-4-6, claude-opus-4-7
    ///   openai    → gpt-5.5
    ///   gemini    → gemini-3.1-pro-preview
    /// </summary>
    public static class LlmProviderFactory
    {
        /// <summary>
        /// Returns the provider name for a given model id, or null if unknown.
        /// </summary>
        public static string ResolveProviderForModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return null;
            string m = modelId.Trim().ToLowerInvariant();

            if (m.StartsWith("claude-"))                  return "anthropic";
            if (m.StartsWith("gpt-") || m.StartsWith("o3") || m.StartsWith("o4")) return "openai";
            if (m.StartsWith("gemini-"))                  return "gemini";
            return null;
        }

        /// <summary>
        /// Construct a provider instance for the given model + key.
        /// Caller supplies a shared HttpClient (we never create per-request clients).
        /// </summary>
        public static ILlmProvider Create(string modelId, string apiKey, HttpClient httpClient)
        {
            string provider = ResolveProviderForModel(modelId);
            if (provider == null)
                throw new ArgumentException($"Unknown model id: {modelId}", nameof(modelId));

            switch (provider)
            {
                case "anthropic": return new AnthropicProvider(apiKey, modelId, httpClient);
                case "openai":    return new OpenAIProvider(apiKey, modelId, httpClient);
                case "gemini":    return new GeminiProvider(apiKey, modelId, httpClient);
                default:
                    throw new ArgumentException($"Unsupported provider: {provider}");
            }
        }
    }
}
