// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bibim.Core
{
    /// <summary>
    /// Builds a tiny, fixed-size synthetic "earlier session context" message from
    /// chat turns that have aged out of the sliding window. No LLM call is made —
    /// we use task titles from the session context plus the first short fragment
    /// of each dropped user message.
    ///
    /// Goal: ~150 tokens of recap so the model knows the session has prior
    /// context, without paying the full per-turn cost of every dropped message.
    /// </summary>
    public static class HistorySummariser
    {
        // Aim for a fixed cap so this stays compact.
        private const int MaxSummaryChars = 600;
        private const int MaxUserSnippetChars = 80;
        private const int MaxBullets = 6;

        public static string Summarise(IReadOnlyList<ChatMessage> dropped, SessionContext sessionContext)
        {
            if (dropped == null || dropped.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.Append("[Earlier session context — ");
            sb.Append(dropped.Count);
            sb.AppendLine(" earlier turns; latest turns continue below]");

            // 1) Recent task titles (most informative recap).
            var taskBullets = new List<string>();
            if (sessionContext?.Tasks != null && sessionContext.Tasks.Count > 0)
            {
                foreach (var t in sessionContext.Tasks
                    .Where(t => !string.IsNullOrWhiteSpace(t?.Title))
                    .Reverse()
                    .Take(MaxBullets))
                {
                    string status = t.Stage ?? "?";
                    taskBullets.Add($"- task ({status}): {Clip(t.Title, 80)}");
                }
            }
            foreach (var b in taskBullets) sb.AppendLine(b);

            // 2) First user message in the dropped window — anchors topic.
            var firstUser = dropped.FirstOrDefault(m => m.IsUser);
            if (firstUser != null && !string.IsNullOrWhiteSpace(firstUser.Text))
                sb.AppendLine($"- session opened with: \"{Clip(firstUser.Text, MaxUserSnippetChars)}\"");

            // 3) Last user message in the dropped window — anchors recency.
            var lastUser = dropped.LastOrDefault(m => m.IsUser);
            if (lastUser != null && lastUser != firstUser && !string.IsNullOrWhiteSpace(lastUser.Text))
                sb.AppendLine($"- last user message before window: \"{Clip(lastUser.Text, MaxUserSnippetChars)}\"");

            string result = sb.ToString();
            return result.Length <= MaxSummaryChars
                ? result
                : result.Substring(0, MaxSummaryChars) + "...]";
        }

        private static string Clip(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }
    }
}
