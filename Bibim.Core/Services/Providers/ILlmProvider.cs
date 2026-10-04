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
    ///   { content: [...], stop_reason, usage, model }
    /// This keeps the orchestrator (LlmOrchestrationService) provider-agnostic
    /// without forcing a heavy refactor of its tool-loop logic.
    /// </summary>
    public interface ILlmProvider
    {
        /// <summary>"anthropic" / "openai" / "local"</summary>
        string ProviderName { get; }

        /// <summary>Concrete model id, e.g. "claude-sonnet-5" / "gpt-6-sol".</summary>
        string ModelId { get; }

        /// <summary>
        /// Send one request (optionally with tools) and return the COMPLETE assistant
        /// message in Anthropic shape. Providers may stream on the wire — Anthropic does
        /// whenever <see cref="LlmRequestOptions.OnProgress"/> is set or the output
        /// ceiling is large — but the caller always receives the assembled message.
        /// Used by the agent tool loop and the Task Planner.
        /// </summary>
        Task<JObject> CreateMessageAsync(
            JArray messages,
            string systemPrompt,
            JArray tools,
            CancellationToken ct,
            int maxTokens,
            LlmRequestOptions options = null);

        /// <summary>
        /// Send a streaming chat request (no tools). The provider invokes
        /// <paramref name="onTextDelta"/> for each chunk of streamed text.
        /// </summary>
        Task<StreamResult> SendStreamingAsync(
            JArray messages,
            string systemPrompt,
            Action<string> onTextDelta,
            CancellationToken ct,
            int maxTokens,
            LlmRequestOptions options = null);
    }

    /// <summary>
    /// Per-request knobs, mapped by each adapter to its native parameters. Every field is
    /// optional; a provider ignores what its model does not support (see
    /// <see cref="ModelCatalog"/> capabilities).
    /// </summary>
    public sealed class LlmRequestOptions
    {
        /// <summary>
        /// "low" / "medium" / "high". Anthropic: <c>output_config.effort</c>.
        /// OpenAI: <c>reasoning.effort</c>. Local: ignored.
        /// </summary>
        public string Effort { get; set; }

        /// <summary>
        /// JSON schema the response text must satisfy. Anthropic:
        /// <c>output_config.format</c>; OpenAI: <c>text.format</c> json_schema (strict).
        /// Local servers fall back to <see cref="JsonMode"/>.
        /// </summary>
        public JObject ResponseSchema { get; set; }

        /// <summary>Schema name (OpenAI requires one).</summary>
        public string ResponseSchemaName { get; set; }

        /// <summary>Ask for JSON-only output where no schema mode exists (local servers).</summary>
        public bool JsonMode { get; set; }

        /// <summary>
        /// Live progress text while the model works: streamed prose and reasoning
        /// summaries. (kind, text) — kind is "text" or "thinking". Setting this makes the
        /// Anthropic adapter stream the request and request summarized thinking on models
        /// that think by default.
        /// </summary>
        public Action<string, string> OnProgress { get; set; }
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

        /// <summary>Provider stop reason when known ("end_turn", "max_tokens", "refusal").</summary>
        public string StopReason { get; set; }
    }
}
