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
    ///   local     → "local" (single canonical id). Actual server-side model name
    ///                is resolved by LocalProvider at runtime from
    ///                ConfigService.LocalModelName OR a lazy /v1/models probe.
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

            // v1.1.x+ canonical local id — single entry in the main model picker.
            // The actual server-side model is resolved by LocalProvider at runtime
            // (config override OR /v1/models auto-discovery).
            if (m == "local")                             return "local";

            // Back-compat — older configs stored vendor-prefixed OpenRouter ids
            // (google/gemma-..., meta-llama/..., mistralai/..., qwen/..., nvidia/...,
            // kwaipilot/...) directly as claude_model. ConfigService migrates these
            // to "local" on next launch with a .bak backup, but the routing still
            // recognises them so a non-migrated file (env-var override, manual
            // edit, restored from backup) keeps working.
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
