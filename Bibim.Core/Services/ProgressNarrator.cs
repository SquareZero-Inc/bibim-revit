// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Text;

namespace Bibim.Core
{
    /// <summary>
    /// Turns the streamed text / reasoning-summary deltas of one tool-loop turn into a
    /// single short line for the progress banner, so a multi-minute code generation shows
    /// what the model is doing instead of a static "Generating code..." label.
    ///
    /// Rules: reasoning → "Reasoning: &lt;latest sentence&gt;"; prose → latest line;
    /// inside a ```code fence → "Writing code (N lines)" (raw code never reaches the banner).
    /// Pure (no Revit / UI dependencies) so it is unit-tested directly.
    /// </summary>
    public sealed class ProgressNarrator
    {
        public const int MaxSnippetChars = 72;

        private readonly StringBuilder _text = new StringBuilder();
        private readonly StringBuilder _thinking = new StringBuilder();
        private string _lastKind;

        /// <summary>Forget the previous turn's output.</summary>
        public void Reset()
        {
            _text.Clear();
            _thinking.Clear();
            _lastKind = null;
        }

        /// <summary>Feed one delta ("text" or "thinking"); returns the current line or null.</summary>
        public string Feed(string kind, string delta)
        {
            if (!string.IsNullOrEmpty(delta))
            {
                if (kind == "thinking")
                {
                    // A new reasoning burst after prose starts a fresh summary line.
                    if (_lastKind != "thinking") _thinking.Clear();
                    _thinking.Append(delta);
                }
                else
                {
                    _text.Append(delta);
                }
                _lastKind = kind == "thinking" ? "thinking" : "text";
            }
            return Current();
        }

        /// <summary>The line the banner should show right now (null when nothing useful yet).</summary>
        public string Current()
        {
            if (_lastKind == "thinking")
            {
                string sentence = LastSentence(_thinking.ToString());
                return sentence == null ? null : AppLanguage.Pick("Reasoning: ", "생각 중: ") + sentence;
            }

            string text = _text.ToString();
            if (text.Length == 0) return null;

            int fenceCount = CountOccurrences(text, "```");
            if (fenceCount % 2 == 1)
            {
                int fenceStart = text.LastIndexOf("```", StringComparison.Ordinal);
                int lines = 0;
                for (int i = fenceStart; i < text.Length; i++)
                    if (text[i] == '\n') lines++;
                return AppLanguage.Pick($"Writing code ({lines} lines)", $"코드 작성 중 ({lines}줄)");
            }

            // Outside code: show the latest prose line (skip a just-closed fence line).
            string line = LastNonEmptyLine(text);
            if (line == null || line.StartsWith("```", StringComparison.Ordinal)) return null;
            return Clip(line);
        }

        private static string LastSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string t = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            // Sentence end = '.', '!' or '?' followed by space; keep the latest complete-ish one.
            int cut = -1;
            for (int i = t.Length - 2; i > 0; i--)
            {
                char c = t[i];
                if ((c == '.' || c == '!' || c == '?') && t[i + 1] == ' ')
                {
                    // If the tail after this boundary is tiny, prefer the previous sentence.
                    if (t.Length - (i + 2) >= 12) { cut = i + 2; break; }
                }
            }
            string sentence = cut > 0 ? t.Substring(cut) : t;
            return Clip(sentence.Trim());
        }

        private static string LastNonEmptyLine(string text)
        {
            string[] lines = text.Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string l = lines[i].Trim();
                if (l.Length > 0) return l.TrimStart('#', '-', '*', ' ');
            }
            return null;
        }

        /// <summary>Keep the most recent words: long lines are cut from the front.</summary>
        private static string Clip(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            if (s.Length <= MaxSnippetChars) return s;
            return "…" + s.Substring(s.Length - (MaxSnippetChars - 1)).TrimStart();
        }

        private static int CountOccurrences(string text, string token)
        {
            int count = 0, idx = 0;
            while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += token.Length;
            }
            return count;
        }
    }
}
