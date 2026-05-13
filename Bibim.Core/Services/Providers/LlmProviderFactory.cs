// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Net.Http;

namespace Bibim.Core
{
    /// <summary>
    /// Resolves a model id to its provider name and constructs the matching ILlmProvider.
    ///
    /// Supported models in v1.1.x:
    ///   anthropic → claude-sonnet-4-6, claude-opus-4-7
    ///   openai    → gpt-5.5
    ///   gemini    → gemini-3.1-pro-preview
    ///   local     → google/gemma-4-26b-a4b-it, meta-llama/llama-3.3-70b-instruct,
    ///               mistralai/codestral-2508
    ///
    /// "local" maps to a self-hosted OpenAI-compatible Chat Completions server
    /// (Ollama / LM Studio / vLLM / llama.cpp). Caller supplies the server URL
    /// via the optional <c>baseUrl</c> parameter on <see cref="Create"/>.
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

            // Local / open-source models — match by vendor prefix (OpenRouter convention).
            // Anything containing a "/" with these vendor prefixes is treated as a
            // self-hosted model that targets an OpenAI-compatible Chat Completions endpoint.
            if (m.StartsWith("google/gemma-") ||
                m.StartsWith("meta-llama/") ||
                m.StartsWith("mistralai/") ||
                m.StartsWith("qwen/") ||
                m.StartsWith("nvidia/") ||
                m.StartsWith("kwaipilot/"))
                return "local";

            return null;
        }

        /// <summary>
        /// Construct a provider instance for the given model + key.
        /// Caller supplies a shared HttpClient (we never create per-request clients).
        /// For provider="local", <paramref name="baseUrl"/> is required (caller resolves
        /// via <c>ConfigService.GetRagConfig().LocalServerUrl</c>).
        /// </summary>
        public static ILlmProvider Create(
            string modelId,
            string apiKey,
            HttpClient httpClient,
            string baseUrl = null,
            string serverModelName = null)
        {
            string provider = ResolveProviderForModel(modelId);
            if (provider == null)
                throw new ArgumentException($"Unknown model id: {modelId}", nameof(modelId));

            switch (provider)
            {
                case "anthropic": return new AnthropicProvider(apiKey, modelId, httpClient);
                case "openai":    return new OpenAIProvider(apiKey, modelId, httpClient);
                case "gemini":    return new GeminiProvider(apiKey, modelId, httpClient);
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
