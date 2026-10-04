// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Net.Http;

namespace Bibim.Core
{
    /// <summary>
    /// Resolves a model id to its provider name and constructs the matching ILlmProvider.
    /// The model list and per-model capabilities live in <see cref="ModelCatalog"/>.
    ///
    /// Providers (v1.2.0):
    ///   anthropic → claude-*   (Messages API)
    ///   openai    → gpt-*      (Responses API)
    ///   local     → "local"    self-hosted OpenAI-compatible Chat Completions server
    ///                           (Ollama / LM Studio / vLLM / llama.cpp); server-side model
    ///                           name resolved at runtime by LocalProvider.
    /// Gemini was removed in v1.2.0 (stored selections migrate to the default model).
    /// </summary>
    public static class LlmProviderFactory
    {
        /// <summary>
        /// Returns the provider name for a given model id, or null if unknown.
        /// </summary>
        public static string ResolveProviderForModel(string modelId) => ModelCatalog.ResolveProvider(modelId);

        /// <summary>
        /// Construct a provider instance for the given model + key.
        /// Caller supplies a shared HttpClient (we never create per-request clients).
        /// For provider="local", <paramref name="baseUrl"/> is required (caller resolves
        /// via <c>ConfigService.GetRagConfig().LocalServerUrl</c>). For "anthropic",
        /// <paramref name="baseUrl"/> optionally overrides the Messages endpoint (self-hosted gateway).
        /// </summary>
        public static ILlmProvider Create(
            string modelId,
            string apiKey,
            HttpClient httpClient,
            string baseUrl = null,
            string serverModelName = null,
            string prefixCacheTtl = "1h")
        {
            string provider = ResolveProviderForModel(modelId);
            if (provider == null)
                throw new ArgumentException($"Unknown model id: {modelId}", nameof(modelId));

            switch (provider)
            {
                case "anthropic": return new AnthropicProvider(apiKey, modelId, httpClient, baseUrl, prefixCacheTtl);
                case "openai":    return new OpenAIProvider(apiKey, modelId, httpClient);
                case "local":
                    if (string.IsNullOrWhiteSpace(baseUrl))
                        throw new ArgumentException(
                            "Local LLM server URL is required. Configure it in Settings → Local LLM.",
                            nameof(baseUrl));
                    return new LocalProvider(apiKey, modelId, httpClient, baseUrl, serverModelName);
                default:
                    throw new ArgumentException($"Unsupported provider: {provider}");
            }
        }
    }
}
