// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bibim.Core
{
    /// <summary>
    /// Keyword search over the user's Code Library (snippets saved from previously generated
    /// and applied code), exposed to the codegen loop as the <c>search_code_library</c> tool so
    /// the model can adapt proven code instead of starting from scratch. Read-only: whatever
    /// the model reuses still goes through compile, analyzers, dry-run and user Apply.
    ///
    /// Matching is substring-based on title + summary, which also works for Korean
    /// (agglutinated words contain the noun: "문번호" contains "문"). Pure — unit-tested.
    /// </summary>
    public static class CodeLibrarySearch
    {
        public const int DefaultTopK = 3;
        private const int MaxCodeChars = 2500;

        public static List<CodeSnippet> Search(IEnumerable<CodeSnippet> snippets, string query,
            string revitVersion = null, int topK = DefaultTopK)
        {
            var tokens = Tokenize(query);
            if (snippets == null || tokens.Count == 0) return new List<CodeSnippet>();

            return snippets
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Code))
                .Select(s => new { Snippet = s, Score = Score(s, tokens, revitVersion) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Snippet.CreatedAt)
                .Take(Math.Max(1, topK))
                .Select(x => x.Snippet)
                .ToList();
        }

        /// <summary>Tool output text for the model.</summary>
        public static string Format(IList<CodeSnippet> matches, string query)
        {
            if (matches == null || matches.Count == 0)
                return $"[search_code_library] No saved snippet matches '{query}'. Write the code from scratch.";

            var sb = new StringBuilder();
            sb.AppendLine($"[search_code_library] {matches.Count} saved snippet(s) match '{query}'. " +
                          "These were generated earlier and saved by the user. Reuse or adapt one ONLY if it fits " +
                          "this task; still verify with run_roslyn_check. Do not assume it was correct.");
            for (int i = 0; i < matches.Count; i++)
            {
                var s = matches[i];
                sb.AppendLine();
                sb.AppendLine($"--- [{i + 1}] {s.Title} (Revit {s.RevitVersion ?? "?"}, {s.TaskKind ?? "?"}, saved {s.CreatedAt:yyyy-MM-dd}) ---");
                if (!string.IsNullOrWhiteSpace(s.Summary))
                    sb.AppendLine("Summary: " + Clip(s.Summary.Replace("\r", " ").Replace("\n", " "), 300));
                string code = s.Code ?? "";
                bool truncated = code.Length > MaxCodeChars;
                sb.AppendLine("```csharp");
                sb.AppendLine(truncated ? code.Substring(0, MaxCodeChars) : code);
                sb.AppendLine("```");
                if (truncated) sb.AppendLine($"(code truncated to {MaxCodeChars} chars)");
            }
            return sb.ToString().TrimEnd();
        }

        internal static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return tokens;
            var current = new StringBuilder();
            void Flush()
            {
                if (current.Length == 0) return;
                string t = current.ToString().ToLowerInvariant();
                current.Clear();
                // Latin tokens need 2+ chars; a single Hangul syllable is a real noun (문, 벽, 층).
                bool hangul = t.Any(c => c >= '가' && c <= '힣');
                if ((hangul || t.Length >= 2) && !tokens.Contains(t)) tokens.Add(t);
            }
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) current.Append(c);
                else Flush();
            }
            Flush();
            return tokens;
        }

        private static double Score(CodeSnippet s, List<string> tokens, string revitVersion)
        {
            string haystack = ((s.Title ?? "") + " " + (s.Summary ?? "")).ToLowerInvariant();
            // Korean spacing varies ("문 번호" vs "문번호") — also match against a space-free copy.
            string compact = new string(haystack.Where(c => !char.IsWhiteSpace(c)).ToArray());
            double score = 0;
            foreach (var t in tokens)
                if (haystack.IndexOf(t, StringComparison.Ordinal) >= 0 ||
                    compact.IndexOf(t, StringComparison.Ordinal) >= 0)
                    score += t.Length >= 3 ? 1.0 : 0.6;
            if (score > 0 && !string.IsNullOrEmpty(revitVersion) &&
                string.Equals(s.RevitVersion, revitVersion, StringComparison.OrdinalIgnoreCase))
                score += 0.5;
            return score;
        }

        private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
