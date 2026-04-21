// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bibim.Core
{
    /// <summary>
    /// Pure C# BM25 search engine. No NuGet dependencies.
    ///
    /// Used by LocalRevitRagService to search the in-memory Revit API index.
    /// BM25 parameters: k1=1.5, b=0.75 (standard defaults).
    ///
    /// Strengths: exact API name matching (PDFExportOptions, FilteredElementCollector, etc.)
    /// Limitations: natural-language queries with no keyword overlap (covered by semantic search, Phase 2).
    /// </summary>
    internal class BM25Engine
    {
        // BM25 tuning parameters
        private const double K1 = 1.5;
        private const double B = 0.75;

        private readonly List<RagChunk> _chunks;

        // token → list of (chunkIndex, termFrequency)
        private readonly Dictionary<string, List<(int idx, int tf)>> _invertedIndex;

        // per-chunk token count
        private readonly int[] _chunkLengths;
        private readonly double _avgChunkLength;

        // IDF cache: token → idf score
        private readonly Dictionary<string, double> _idfCache;

        public int ChunkCount => _chunks.Count;

        public BM25Engine(List<RagChunk> chunks)
        {
            _chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
            _invertedIndex = new Dictionary<string, List<(int, int)>>(StringComparer.OrdinalIgnoreCase);
            _idfCache = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _chunkLengths = new int[chunks.Count];

            BuildIndex();

            long total = 0;
            for (int i = 0; i < _chunkLengths.Length; i++)
                total += _chunkLengths[i];
            _avgChunkLength = chunks.Count > 0 ? (double)total / chunks.Count : 1.0;
        }

        /// <summary>
        /// Search for the top-k chunks most relevant to the query.
        /// Returns results sorted by BM25 score descending.
        /// </summary>
        public List<RagChunk> Search(string query, int topK = 5)
        {
            if (string.IsNullOrWhiteSpace(query) || _chunks.Count == 0)
                return new List<RagChunk>();

            var queryTokens = Tokenize(query);
            if (queryTokens.Count == 0)
                return new List<RagChunk>();

            var scores = new double[_chunks.Count];

            foreach (string token in queryTokens.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_invertedIndex.TryGetValue(token, out var postings))
                    continue;

                double idf = GetIdf(token, postings.Count);

                foreach (var (idx, tf) in postings)
                {
                    double dl = _chunkLengths[idx];
                    double tfNorm = (tf * (K1 + 1.0))
                                 / (tf + K1 * (1.0 - B + B * dl / _avgChunkLength));
                    scores[idx] += idf * tfNorm;
                }
            }

            // Collect non-zero scoring chunks, sort descending
            var results = new List<(int idx, double score)>();
            for (int i = 0; i < scores.Length; i++)
            {
                if (scores[i] > 0)
                    results.Add((i, scores[i]));
            }

            results.Sort((a, b) => b.score.CompareTo(a.score));

            return results
                .Take(topK)
                .Select(r => _chunks[r.idx])
                .ToList();
        }

        // ─────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────

        private void BuildIndex()
        {
            for (int i = 0; i < _chunks.Count; i++)
            {
                var tokens = Tokenize(_chunks[i].IndexText);
                _chunkLengths[i] = tokens.Count;

                // Count term frequency per chunk
                var tfMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (string token in tokens)
                {
                    if (!tfMap.TryGetValue(token, out int count))
                        tfMap[token] = 1;
                    else
                        tfMap[token] = count + 1;
                }

                foreach (var kv in tfMap)
                {
                    if (!_invertedIndex.TryGetValue(kv.Key, out var list))
                    {
                        list = new List<(int, int)>();
                        _invertedIndex[kv.Key] = list;
                    }
                    list.Add((i, kv.Value));
                }
            }
        }

        private double GetIdf(string token, int docFreq)
        {
            if (_idfCache.TryGetValue(token, out double cached))
                return cached;

            int n = _chunks.Count;
            // BM25 IDF formula: log((n - df + 0.5) / (df + 0.5) + 1)
            double idf = Math.Log((n - docFreq + 0.5) / (docFreq + 0.5) + 1.0);
            _idfCache[token] = idf;
            return idf;
        }

        /// <summary>
        /// Tokenize text: split on non-alphanumeric boundaries, lowercase,
        /// also split CamelCase tokens for better API name matching.
        /// e.g. "PDFExportOptions" → ["PDFExportOptions", "PDF", "Export", "Options"]
        /// </summary>
        internal static List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var raw = Regex.Split(text, @"[^a-zA-Z0-9_]+");
            var result = new List<string>(raw.Length * 2);

            foreach (string token in raw)
            {
                if (token.Length < 2) continue;
                result.Add(token);

                // Also emit CamelCase sub-tokens for compound names
                var sub = SplitCamelCase(token);
                if (sub.Count > 1)
                    result.AddRange(sub);
            }

            return result;
        }

        private static List<string> SplitCamelCase(string token)
        {
            // Split on transitions: lowercase→uppercase, or uppercase run→uppercase+lowercase
            // e.g. "PDFExportOptions" → ["PDF", "Export", "Options"]
            var parts = Regex.Split(token, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])");
            var result = new List<string>();
            foreach (string p in parts)
            {
                if (p.Length >= 2)
                    result.Add(p);
            }
            return result;
        }
    }

    /// <summary>
    /// A single searchable unit in the Revit API index.
    /// One chunk = one class (or a domain-grouped subset for giant classes).
    /// </summary>
    internal class RagChunk
    {
        /// <summary>Class name, e.g. "Document"</summary>
        public string ClassName { get; set; }

        /// <summary>Full namespace, e.g. "Autodesk.Revit.DB"</summary>
        public string Namespace { get; set; }

        /// <summary>
        /// The human-readable text that is sent to Claude as context.
        /// Contains class summary + member signatures + descriptions.
        /// </summary>
        public string DisplayText { get; set; }

        /// <summary>
        /// Text used for BM25 indexing (DisplayText + extra tags for better matching).
        /// </summary>
        public string IndexText { get; set; }
    }
}
