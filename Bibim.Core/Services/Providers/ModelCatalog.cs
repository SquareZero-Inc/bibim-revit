// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Per-model request capabilities. Every provider adapter reads these instead of
    /// hard-coding model-id checks, so adding a model is one row in <see cref="ModelCatalog"/>.
    /// Values for the Claude rows were verified against the live API on 2026-09-28.
    /// </summary>
    public sealed class ModelInfo
    {
        public string Id { get; }
        public string Label { get; }

        /// <summary>"anthropic" / "openai" / "local".</summary>
        public string Provider { get; }

        /// <summary>
        /// Omitting the <c>thinking</c> parameter runs adaptive thinking (Sonnet 5, Opus 5,
        /// Opus 5.5, Fable 5.1). These models also accept <c>thinking.display</c>, which the
        /// tool loop uses to stream reasoning summaries into the progress banner. Older
        /// models (Sonnet 4.6, Opus 4.7) run without thinking when it is omitted.
        /// </summary>
        public bool ThinkingOnByDefault { get; }

        /// <summary>
        /// Preserved thinking: a thinking block is only valid in the exact conversation that
        /// produced it, so editing or deleting earlier turns invalidates it (400 on enforced
        /// accounts, silently dropped otherwise). The tool loop must stay append-only —
        /// no pruning of earlier retry rounds. (Opus 5.5, Fable 5.1.)
        /// </summary>
        public bool AppendOnlyHistory { get; }

        /// <summary>
        /// Model runs safety classifiers that can decline with <c>stop_reason: "refusal"</c>.
        /// Requests opt into <c>fallbacks: "default"</c> so a false positive is re-run on
        /// Anthropic's recommended fallback model server-side instead of failing the task.
        /// </summary>
        public bool ServerSideFallback { get; }

        /// <summary>Accepts an effort / reasoning-effort control.</summary>
        public bool SupportsEffort { get; }

        /// <summary>Native JSON-schema constrained output (planner).</summary>
        public bool SupportsStructuredOutput { get; }

        /// <summary>Hard output ceiling advertised by the provider.</summary>
        public int MaxOutputTokens { get; }

        /// <summary>Previous-generation model kept selectable for existing users.</summary>
        public bool Legacy { get; }

        public ModelInfo(string id, string label, string provider,
            bool thinkingOnByDefault = false, bool appendOnlyHistory = false,
            bool serverSideFallback = false, bool supportsEffort = true,
            bool supportsStructuredOutput = true, int maxOutputTokens = 128000,
            bool legacy = false)
        {
            Id = id;
            Label = label;
            Provider = provider;
            ThinkingOnByDefault = thinkingOnByDefault;
            AppendOnlyHistory = appendOnlyHistory;
            ServerSideFallback = serverSideFallback;
            SupportsEffort = supportsEffort;
            SupportsStructuredOutput = supportsStructuredOutput;
            MaxOutputTokens = maxOutputTokens;
            Legacy = legacy;
        }
    }

    /// <summary>
    /// Single source of truth for the selectable models (v1.2.0+). Order = display order.
    /// Mirror any change in <c>frontend/src/components/SettingsPanel.tsx</c> (MODELS), which
    /// carries the UI-only fields (cost estimate, speed glyph, note).
    ///
    /// Gemini was removed as a provider in v1.2.0 (the only Pro model was a preview that
    /// Google began pulling from AI Studio in 2026-09). Stored gemini-* selections are
    /// migrated to <see cref="DefaultModelId"/> by <see cref="MigrateModelId"/>.
    /// </summary>
    public static class ModelCatalog
    {
        /// <summary>Default for fresh installs and for migrated/unknown selections.</summary>
        public const string DefaultModelId = "claude-sonnet-5";

        public const string LocalModelId = "local";

        public static readonly IReadOnlyList<ModelInfo> Models = new List<ModelInfo>
        {
            // ── Anthropic ──────────────────────────────────────────────
            new ModelInfo("claude-sonnet-5",   "Claude Sonnet 5",   "anthropic", thinkingOnByDefault: true),
            new ModelInfo("claude-opus-5-5",   "Claude Opus 5.5",   "anthropic", thinkingOnByDefault: true,
                          appendOnlyHistory: true, serverSideFallback: true),
            new ModelInfo("claude-opus-5",     "Claude Opus 5",     "anthropic", thinkingOnByDefault: true,
                          serverSideFallback: true),
            new ModelInfo("claude-fable-5-1",  "Claude Fable 5.1",  "anthropic", thinkingOnByDefault: true,
                          appendOnlyHistory: true, serverSideFallback: true),
            new ModelInfo("claude-sonnet-4-6", "Claude Sonnet 4.6", "anthropic", legacy: true),
            new ModelInfo("claude-opus-4-7",   "Claude Opus 4.7",   "anthropic", legacy: true),

            // ── OpenAI (Responses API) ─────────────────────────────────
            new ModelInfo("gpt-6-sol",         "GPT-6 Sol",         "openai"),
            new ModelInfo("gpt-6-luna",        "GPT-6 Luna",        "openai"),
            new ModelInfo("gpt-5.6-sol",       "GPT-5.6 Sol",       "openai"),
            new ModelInfo("gpt-5.6-terra",     "GPT-5.6 Terra",     "openai"),
            new ModelInfo("gpt-5.5",           "GPT-5.5",           "openai", legacy: true),

            // ── Self-hosted (OpenAI-compatible Chat Completions) ───────
            // Single entry; the server-side model name is resolved at runtime by
            // LocalProvider (config override or /v1/models discovery).
            new ModelInfo(LocalModelId, "Local LLM (Self-hosted)", "local",
                          supportsEffort: false, supportsStructuredOutput: false, maxOutputTokens: 8192),
        };

        private static readonly Dictionary<string, ModelInfo> _byId =
            Models.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        /// <summary>Catalog entry for a model id, or null when unknown.</summary>
        public static ModelInfo Find(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;
            _byId.TryGetValue(modelId.Trim(), out var info);
            return info;
        }

        public static bool IsKnown(string modelId) => Find(modelId) != null;

        /// <summary>
        /// Provider for a model id by prefix — also covers ids outside the catalog
        /// (env-var overrides, manual config edits, legacy local vendor ids).
        /// Returns null when the id cannot be routed.
        /// </summary>
        public static string ResolveProvider(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;
            var known = Find(modelId);
            if (known != null) return known.Provider;

            string m = modelId.Trim().ToLowerInvariant();
            if (m.StartsWith("claude-")) return "anthropic";
            if (m.StartsWith("gpt-") || m.StartsWith("o3") || m.StartsWith("o4")) return "openai";
            if (m == LocalModelId) return "local";

            // Back-compat: pre-1.1.x configs stored vendor-prefixed OpenRouter ids for the
            // self-hosted picker. ConfigService migrates them to "local"; routing still
            // recognises them for non-migrated files (env var, restored backup).
            if (m.StartsWith("google/gemma-") || m.StartsWith("meta-llama/") ||
                m.StartsWith("mistralai/") || m.StartsWith("qwen/") ||
                m.StartsWith("nvidia/") || m.StartsWith("kwaipilot/"))
                return "local";

            return null;
        }

        /// <summary>
        /// Capabilities for any model id. Unknown ids get a conservative profile inferred
        /// from the id prefix (routing still works for ids set by env var / manual edit).
        /// </summary>
        public static ModelInfo Describe(string modelId)
        {
            var known = Find(modelId);
            if (known != null) return known;

            string provider = ResolveProvider(modelId) ?? "anthropic";
            return new ModelInfo(modelId ?? "", modelId ?? "", provider,
                supportsEffort: provider != "local",
                supportsStructuredOutput: provider != "local",
                maxOutputTokens: provider == "local" ? 8192 : 64000);
        }

        /// <summary>
        /// Rewrite a stored model id that is no longer offered. Returns the input unchanged
        /// when no migration applies.
        /// </summary>
        public static string MigrateModelId(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return DefaultModelId;
            string m = modelId.Trim();
            // Gemini provider removed in v1.2.0.
            if (m.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)) return DefaultModelId;
            return m;
        }
    }
}
