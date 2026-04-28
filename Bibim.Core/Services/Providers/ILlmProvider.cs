// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Provider abstraction for LLM API calls.
    ///
    /// Canonical message format is Anthropic-shaped JArray:
    ///   [ { role: "user"|"assistant", content: string|JArray } ]
    /// Each provider adapter translates to/from the provider-native format.
    ///
    /// Tools are passed as Anthropic-shaped JArray:
    ///   [ { name, description, input_schema } ]
    ///
    /// Responses are returned in Anthropic-shaped JObject:
    ///   { content: [...], stop_reason, usage }
    /// This keeps the orchestrator (LlmOrchestrationService) provider-agnostic
    /// without forcing a heavy refactor of its tool-loop logic.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>"anthropic" / "openai" / "gemini"</summary>
        string ProviderName { get; }

        /// <summary>Concrete model id, e.g. "claude-sonnet-4-6" / "gpt-5.5" / "gemini-3.1-pro-preview".</summary>
        string ModelId { get; }

        /// <summary>
        /// Send a non-streaming request, optionally with tools. Used by the agent tool loop.
        /// Returns an Anthropic-shaped response JObject so the orchestrator stays
        /// uniform across providers.
        /// </summary>
        /// <param name="jsonMode">When true, request the model produce JSON-only
        /// output. Maps to provider-native flags: OpenAI <c>response_format</c>,
        /// Gemini <c>responseMimeType: application/json</c>. Anthropic ignores
        /// this — Claude follows JSON-only prompt instructions reliably without
        /// a native flag. Used by the Task Planner.</param>
        Task<JObject> SendNonStreamingAsync(
            JArray messages,
            string systemPrompt,
            JArray tools,
            CancellationToken ct,
            int maxTokens,
            bool jsonMode = false);

        /// <summary>
        /// Send a streaming chat request (no tools). The provider invokes
        /// <paramref name="onTextDelta"/> for each chunk of streamed text.
        /// </summary>
        Task<StreamResult> SendStreamingAsync(
            JArray messages,
            string systemPrompt,
            Action<string> onTextDelta,
            CancellationToken ct,
            int maxTokens);
    }

    /// <summary>
    /// Result of a streaming call: full assembled text plus token usage.
    /// </summary>
    public class StreamResult
    {
        public string FullText { get; set; } = "";
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int CachedInputTokens { get; set; }
        public int CacheCreationInputTokens { get; set; }
    }
}
