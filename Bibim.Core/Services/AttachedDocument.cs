// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// A document (.md / .txt / .csv) the user attached to one chat message — e.g. a model-check
    /// check report whose violation table drives a fix task.
    ///
    /// Privacy contract: the body lives in session MEMORY only and is sent to the LLM
    /// provider as part of the request. It is never written to the session file, the debug
    /// log, or the codegen debug artifacts — those get the file name and size
    /// (see <see cref="Redact"/> and <see cref="DisplayMarker"/>).
    /// Pure (no Revit / UI types) so it is unit-tested directly.
    /// </summary>
    public sealed class AttachedDocument
    {
        /// <summary>Body cap (characters). Longer documents are cut and flagged.</summary>
        public const int MaxChars = 200000;

        public const string BlockStart = "<<<DOCUMENT";
        public const string BlockEnd = "DOCUMENT>>>";

        /// <summary>First line of a stored/displayed user message that carried an attachment.</summary>
        public const string MarkerPrefix = "📎 ";

        private static readonly string[] AllowedExtensions = { ".md", ".markdown", ".txt", ".csv" };

        public string Name { get; private set; }
        public string Format { get; private set; }      // markdown | text | csv
        public string Content { get; private set; }     // possibly truncated
        public int OriginalChars { get; private set; }
        public int OmittedChars { get; private set; }
        public long SizeBytes { get; private set; }
        public bool Truncated => OmittedChars > 0;

        /// <summary>Rows parsed from a table with an ElementId / UniqueId column (may be empty).</summary>
        public IReadOnlyList<AttachmentTarget> Targets { get; private set; } = new List<AttachmentTarget>();

        /// <summary>rule id → description, from a summary table (§6 "위반 요약") when present.</summary>
        public IReadOnlyDictionary<string, string> RuleNames { get; private set; } = new Dictionary<string, string>();

        public static AttachedDocument Create(string name, string content, long sizeBytes = 0, int omittedByClient = 0)
        {
            string safeName = SanitizeName(name);
            string body = StripBom(content ?? string.Empty);
            int omitted = Math.Max(0, omittedByClient);
            if (body.Length > MaxChars)
            {
                omitted += body.Length - MaxChars;
                body = body.Substring(0, MaxChars);
            }

            var doc = new AttachedDocument
            {
                Name = safeName,
                Format = FormatFor(safeName),
                Content = body,
                OriginalChars = body.Length + omitted,
                OmittedChars = omitted,
                SizeBytes = sizeBytes > 0 ? sizeBytes : Encoding.UTF8.GetByteCount(body)
            };
            try
            {
                doc.Targets = doc.Format == "csv"
                    ? AttachmentTableParser.ParseCsv(body)
                    : AttachmentTableParser.ParseMarkdown(body);
                if (doc.Format != "csv")
                    doc.RuleNames = AttachmentTableParser.ParseRuleNames(body);
            }
            catch
            {
                // P1 pre-extraction is best-effort: a parse failure must never surface as an
                // error — the full body is still injected and the model reads it directly.
                doc.Targets = new List<AttachmentTarget>();
                doc.RuleNames = new Dictionary<string, string>();
            }
            return doc;
        }

        /// <summary>Bridge payload { name, content, sizeBytes?, omittedChars? } → document (null if absent/invalid).</summary>
        public static AttachedDocument FromPayload(JObject payload)
        {
            if (payload == null) return null;
            string name = payload["name"]?.ToString();
            string content = payload["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(name) || content == null) return null;
            if (!IsAllowedName(name)) return null;
            long size = 0;
            long.TryParse(payload["sizeBytes"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            int omitted = 0;
            int.TryParse(payload["omittedChars"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out omitted);
            return Create(name, content, size, omitted);
        }

        public static bool IsAllowedName(string name)
        {
            string ext = SafeExtension(name);
            return AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The user message as the LLM receives it (spec §4-1): document block first, then the
        /// structured target list (P1, when a table was found), then the user's instruction.
        /// </summary>
        public string ComposeForLlm(string userInstruction)
        {
            bool en = AppLanguage.IsEnglish;
            var sb = new StringBuilder();
            sb.AppendLine(en ? "[Attached document]" : "[첨부 문서]");
            sb.AppendLine((en ? "File name: " : "파일명: ") + Name);
            sb.AppendLine((en ? "Format: " : "형식: ") + Format);
            sb.AppendLine(BlockStart);
            sb.AppendLine(Content);
            if (Truncated)
                sb.AppendLine(en
                    ? $"[The document was long; the last {OmittedChars} characters were omitted]"
                    : $"[문서가 길어 뒤쪽 {OmittedChars}자를 생략했습니다]");
            sb.AppendLine(BlockEnd);

            string targets = BuildTargetList();
            if (targets.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine(targets);
            }

            sb.AppendLine();
            sb.AppendLine(en ? "[User instruction]" : "[사용자 지시]");
            sb.Append(userInstruction ?? string.Empty);
            return sb.ToString();
        }

        /// <summary>
        /// "[구조화된 대상 목록: N건]" + compact table (at most 300 rows), or "" when no
        /// ElementId/UniqueId table was found.
        /// </summary>
        public string BuildTargetList(int maxRows = 300)
        {
            if (Targets == null || Targets.Count == 0) return string.Empty;
            bool en = AppLanguage.IsEnglish;
            var sb = new StringBuilder();
            sb.AppendLine(en
                ? $"[Structured target list: {Targets.Count}]"
                : $"[구조화된 대상 목록: {Targets.Count}건]");
            sb.AppendLine("| rule_id | element_id | unique_id | category | family/type | field | current | expected |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var t in Targets.Take(maxRows))
            {
                sb.AppendLine("| " + string.Join(" | ", new[]
                {
                    Cell(t.RuleId), t.ElementId?.ToString(CultureInfo.InvariantCulture) ?? "", Cell(t.UniqueId),
                    Cell(t.Category), Cell(t.TypeName), Cell(t.Field), Cell(t.CurrentValue), Cell(t.ExpectedValue)
                }) + " |");
            }
            if (Targets.Count > maxRows)
                sb.AppendLine(en
                    ? $"(first {maxRows} of {Targets.Count} rows shown; the document above has all of them)"
                    : $"(전체 {Targets.Count}건 중 앞 {maxRows}건만 표시 — 전체는 위 문서 본문에 있음)");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Deterministic planning digest shown to the user before any code runs (spec §4-2 rule 3):
        /// target count, rules with counts, current → expected values.
        /// </summary>
        public string BuildPlanDigest()
        {
            bool en = AppLanguage.IsEnglish;
            var sb = new StringBuilder();
            sb.AppendLine(en
                ? $"📋 Attached document: {Name}{(Truncated ? " (partly omitted)" : "")}"
                : $"📋 첨부 문서 분석: {Name}{(Truncated ? " (일부 생략)" : "")}");

            if (Targets == null || Targets.Count == 0)
            {
                sb.Append(en
                    ? "- No ElementId/UniqueId table was found; the plan is based on the full document text."
                    : "- ElementId/UniqueId 표를 찾지 못해 문서 본문 전체를 기준으로 계획했습니다.");
                return sb.ToString();
            }

            var byRule = Targets
                .GroupBy(t => string.IsNullOrWhiteSpace(t.RuleId) ? (en ? "(no rule id)" : "(규칙 ID 없음)") : t.RuleId)
                .OrderByDescending(g => g.Count())
                .ToList();
            string rules = string.Join(", ", byRule.Take(8).Select(g =>
            {
                string ruleName = g.Select(t => t.RuleName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                if (ruleName == null && RuleNames != null) RuleNames.TryGetValue(g.Key, out ruleName);
                // 60: a full Korean rule sentence fits; the headline is on camera in the demo.
                string label = string.IsNullOrWhiteSpace(ruleName) ? g.Key : $"{g.Key} {Clip(ruleName, 60)}";
                return byRule.Count > 1 ? $"{label} ({g.Count()}{(en ? "" : "건")})" : label;
            })) + (byRule.Count > 8 ? " …" : "");

            // Headline in the spec's acceptance form: "대상 요소 N건, 규칙 X".
            sb.AppendLine(en
                ? $"**{Targets.Count} target element(s), rule {rules}**"
                : $"**대상 요소 {Targets.Count}건, 규칙 {rules}**");

            var transitions = Targets
                .Where(t => !string.IsNullOrWhiteSpace(t.ExpectedValue) || !string.IsNullOrWhiteSpace(t.CurrentValue))
                .GroupBy(t => (Clip(t.CurrentValue, 40) ?? "") + "\u0001" + (Clip(t.ExpectedValue, 40) ?? ""))
                .OrderByDescending(g => g.Count())
                .Take(5)
                .ToList();
            foreach (var g in transitions)
            {
                var parts = g.Key.Split('\u0001');
                string current = string.IsNullOrWhiteSpace(parts[0]) ? (en ? "(empty)" : "(비어 있음)") : $"'{parts[0]}'";
                string expected = string.IsNullOrWhiteSpace(parts[1]) ? (en ? "(not specified)" : "(명시 없음)") : $"'{parts[1]}'";
                sb.AppendLine(en
                    ? $"- Current {current} → expected {expected} ({g.Count()})"
                    : $"- 현재 {current} → 기대 {expected} ({g.Count()}건)");
            }

            sb.Append(en
                ? "- Only these elements will be changed; anything not in the list is left untouched. Missing ids are skipped and reported."
                : "- 목록에 있는 요소만 변경하며, 목록 밖 요소는 건드리지 않습니다. 모델에 없는 ID는 건너뛰고 결과에 보고합니다.");
            return sb.ToString();
        }

        /// <summary>
        /// Numeric ElementIds listed in the document (empty when the table only has UniqueIds).
        /// </summary>
        public HashSet<long> TargetElementIds() =>
            new HashSet<long>(Targets.Where(t => t.ElementId.HasValue).Select(t => t.ElementId.Value));

        /// <summary>What gets persisted / displayed instead of the body: "📎 name" + instruction.</summary>
        public string DisplayMarker(string userInstruction) =>
            MarkerPrefix + Name + "\n" + (userInstruction ?? string.Empty);

        /// <summary>Short log/diagnostic description — never the body.</summary>
        public string Describe() =>
            $"{Name} ({Format}, {OriginalChars} chars{(Truncated ? $", {OmittedChars} omitted" : "")}, {Targets.Count} targets)";

        // ───────────────────────────── redaction ─────────────────────────────

        private static readonly Regex BlockRegex = new Regex(
            Regex.Escape(BlockStart) + @"[\s\S]*?" + Regex.Escape(BlockEnd),
            RegexOptions.Compiled);

        /// <summary>
        /// Replace every document block with a size note. Applied to anything written to disk
        /// (codegen debug artifacts) and to chat history copies that must not re-send the body.
        /// </summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(BlockStart, StringComparison.Ordinal) < 0)
                return text;
            return BlockRegex.Replace(text, m =>
                $"{BlockStart}\n[attached document body not stored — {m.Length} chars]\n{BlockEnd}");
        }

        public static bool ContainsDocument(string text) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(BlockStart, StringComparison.Ordinal) >= 0;

        private static readonly string[] InstructionLabels = { "[사용자 지시]", "[User instruction]" };

        /// <summary>
        /// The user's own instruction from a composed message (text unchanged when it carries
        /// no document) — keyword heuristics must not fire on words inside the document.
        /// </summary>
        public static string InstructionOf(string text)
        {
            if (!ContainsDocument(text)) return text;
            int end = text.LastIndexOf(BlockEnd, StringComparison.Ordinal);
            int best = -1, labelLen = 0;
            foreach (var label in InstructionLabels)
            {
                int i = text.LastIndexOf(label, StringComparison.Ordinal);
                if (i > end && i > best) { best = i; labelLen = label.Length; }
            }
            return best < 0 ? Redact(text) : text.Substring(best + labelLen).Trim();
        }

        // ───────────────────────────── helpers ─────────────────────────────

        private static string FormatFor(string name)
        {
            string ext = SafeExtension(name);
            if (ext == ".csv") return "csv";
            if (ext == ".txt") return "text";
            return "markdown";
        }

        private static string SafeExtension(string name)
        {
            try { return (Path.GetExtension(name ?? "") ?? "").ToLowerInvariant(); }
            catch { return ""; }
        }

        private static string SanitizeName(string name)
        {
            string n = (name ?? "document.md").Replace("\r", " ").Replace("\n", " ").Trim();
            try { n = Path.GetFileName(n); } catch { /* keep as-is */ }
            if (n.Length > 120) n = n.Substring(0, 120);
            return n.Length == 0 ? "document.md" : n;
        }

        private static string StripBom(string s) =>
            s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;

        private static string Cell(string s) => (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ").Trim();

        private static string Clip(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }

    /// <summary>One violation row extracted from an attached report.</summary>
    public sealed class AttachmentTarget
    {
        public string RuleId { get; set; }
        public string RuleName { get; set; }
        public long? ElementId { get; set; }
        public string UniqueId { get; set; }
        public string Category { get; set; }
        public string TypeName { get; set; }
        public string Field { get; set; }
        public string CurrentValue { get; set; }
        public string ExpectedValue { get; set; }
    }

    /// <summary>
    /// Finds the first table whose header has an ElementId or UniqueId column and turns its
    /// rows into <see cref="AttachmentTarget"/>s. Understands both a Korean report layout
    /// (규칙 ID | ElementId | UniqueId | 카테고리 | 패밀리/타입 | 현재 값 | 기대 값) and the
    /// common English export layout (outcome | severity | rule_id | rule | category | type |
    /// element_id | field | observed | …). Rows marked "indeterminate" are not targets.
    /// </summary>
    public static class AttachmentTableParser
    {
        private static readonly Dictionary<string, string> HeaderAliases = BuildAliases();

        private static Dictionary<string, string> BuildAliases()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(string key, params string[] names) { foreach (var n in names) map[Norm(n)] = key; }
            // No bare "id": a numbered rule table (| id | rule |) must not read as element ids.
            Add("element_id", "elementid", "element_id", "element id", "요소id", "요소 id", "요소아이디");
            Add("unique_id", "uniqueid", "unique_id", "unique id", "고유id", "고유 id");
            Add("rule_id", "규칙id", "규칙 id", "rule_id", "ruleid", "rule id", "규칙번호", "check_id");
            Add("rule", "규칙", "rule", "규칙 설명", "규칙설명", "rule_name", "description");
            Add("category", "카테고리", "category", "범주");
            Add("type", "패밀리/타입", "패밀리 / 타입", "type", "family/type", "family", "타입", "패밀리");
            Add("field", "field", "필드", "파라미터", "parameter");
            Add("current", "현재 값", "현재값", "observed", "current", "current_value", "현재");
            Add("expected", "기대 값", "기대값", "expected", "expected_value", "기대", "목표 값", "목표값");
            Add("outcome", "outcome", "판정", "결과");
            return map;
        }

        private static string Norm(string s) =>
            Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"[\s`*_]+", "");

        public static List<AttachmentTarget> ParseMarkdown(string text)
        {
            var result = new List<AttachmentTarget>();
            if (string.IsNullOrEmpty(text)) return result;
            var lines = text.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (!IsTableRow(lines[i]) || !IsSeparatorRow(lines[i + 1])) continue;
                var columns = MapHeader(SplitRow(lines[i]));
                if (!columns.ContainsKey("element_id") && !columns.ContainsKey("unique_id")) continue;

                for (int r = i + 2; r < lines.Length && IsTableRow(lines[r]); r++)
                {
                    var cells = SplitRow(lines[r]);
                    var target = ToTarget(columns, cells);
                    if (target != null) result.Add(target);
                }
                if (result.Count > 0) return result;   // first matching table wins
            }
            return result;
        }

        /// <summary>
        /// rule id → description from tables that have rule id + description columns but no
        /// element ids (the §6 "위반 요약" table). Best-effort; empty when none.
        /// </summary>
        public static Dictionary<string, string> ParseRuleNames(string text)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return names;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (!IsTableRow(lines[i]) || !IsSeparatorRow(lines[i + 1])) continue;
                var columns = MapHeader(SplitRow(lines[i]));
                if (!columns.TryGetValue("rule_id", out int idCol) || !columns.TryGetValue("rule", out int nameCol)) continue;
                if (columns.ContainsKey("element_id") || columns.ContainsKey("unique_id")) continue;
                for (int r = i + 2; r < lines.Length && IsTableRow(lines[r]); r++)
                {
                    var cells = SplitRow(lines[r]);
                    if (idCol >= cells.Count || nameCol >= cells.Count) continue;
                    string id = cells[idCol].Trim(), name = cells[nameCol].Trim();
                    if (id.Length > 0 && name.Length > 0 && !names.ContainsKey(id)) names[id] = name;
                }
            }
            return names;
        }

        public static List<AttachmentTarget> ParseCsv(string text)
        {
            var result = new List<AttachmentTarget>();
            if (string.IsNullOrEmpty(text)) return result;
            var rows = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            if (rows.Count < 2) return result;
            char sep = rows[0].Count(c => c == ';') > rows[0].Count(c => c == ',') ? ';' : ',';
            var columns = MapHeader(SplitCsv(rows[0], sep));
            if (!columns.ContainsKey("element_id") && !columns.ContainsKey("unique_id")) return result;
            foreach (var row in rows.Skip(1))
            {
                var target = ToTarget(columns, SplitCsv(row, sep));
                if (target != null) result.Add(target);
            }
            return result;
        }

        private static Dictionary<string, int> MapHeader(List<string> header)
        {
            var columns = new Dictionary<string, int>();
            for (int c = 0; c < header.Count; c++)
            {
                if (HeaderAliases.TryGetValue(Norm(header[c]), out string key) && !columns.ContainsKey(key))
                    columns[key] = c;
            }
            return columns;
        }

        private static AttachmentTarget ToTarget(Dictionary<string, int> columns, List<string> cells)
        {
            string Get(string key) =>
                columns.TryGetValue(key, out int idx) && idx < cells.Count ? cells[idx].Trim() : null;

            string outcome = Get("outcome");
            if (!string.IsNullOrEmpty(outcome) && outcome.IndexOf("indeterminate", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            long? elementId = null;
            string rawId = Get("element_id");
            if (!string.IsNullOrEmpty(rawId))
            {
                string digits = rawId.Trim().Trim('`');
                if (long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed > 0)
                    elementId = parsed;
            }
            string uniqueId = Get("unique_id")?.Trim('`');
            if (!elementId.HasValue && string.IsNullOrWhiteSpace(uniqueId)) return null;

            return new AttachmentTarget
            {
                ElementId = elementId,
                UniqueId = string.IsNullOrWhiteSpace(uniqueId) ? null : uniqueId,
                RuleId = Get("rule_id"),
                RuleName = Get("rule"),
                Category = Get("category"),
                TypeName = Get("type"),
                Field = Get("field"),
                CurrentValue = Get("current"),
                ExpectedValue = Get("expected")
            };
        }

        private static bool IsTableRow(string line)
        {
            string t = (line ?? "").Trim();
            return t.StartsWith("|", StringComparison.Ordinal) && t.Length > 1;
        }

        private static bool IsSeparatorRow(string line) =>
            IsTableRow(line) && Regex.IsMatch(line.Trim(), @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$");

        /// <summary>Split a markdown row on unescaped pipes; "\|" stays inside the cell.</summary>
        internal static List<string> SplitRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith("|")) t = t.Substring(1);
            if (t.EndsWith("|") && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
            var cells = new List<string>();
            var cur = new StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|')
                {
                    cur.Append('|');
                    i++;
                }
                else if (t[i] == '|')
                {
                    cells.Add(cur.ToString().Trim());
                    cur.Clear();
                }
                else cur.Append(t[i]);
            }
            cells.Add(cur.ToString().Trim());
            return cells;
        }

        private static List<string> SplitCsv(string line, char sep)
        {
            var cells = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else cur.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == sep) { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            cells.Add(cur.ToString());
            return cells;
        }
    }

    /// <summary>Compare what a run changed with the document's target ids.</summary>
    public static class AttachmentScope
    {
        public sealed class Result
        {
            public int Targets { get; set; }
            public int ChangedTargets { get; set; }
            public int ChangedOutside { get; set; }
            public List<long> OutsideSample { get; set; } = new List<long>();
            /// <summary>The run changed more elements than the reported id list holds.</summary>
            public bool Partial { get; set; }
        }

        /// <param name="affectedTotal">Measured change count; when larger than the id list
        /// (the execution handler caps reported ids) the result is marked partial.</param>
        /// <returns>null when the document has no numeric ElementIds to compare against.</returns>
        public static Result Compare(ICollection<long> targetIds, IEnumerable<long> affectedIds, int affectedTotal = -1)
        {
            if (targetIds == null || targetIds.Count == 0) return null;
            var affected = new HashSet<long>(affectedIds ?? Enumerable.Empty<long>());
            var outside = affected.Where(id => !targetIds.Contains(id)).ToList();
            return new Result
            {
                Targets = targetIds.Count,
                ChangedTargets = affected.Count(id => targetIds.Contains(id)),
                ChangedOutside = outside.Count,
                OutsideSample = outside.Take(10).ToList(),
                Partial = affectedTotal > affected.Count
            };
        }

        /// <summary>One localized line for preview / verification messages.</summary>
        public static string Describe(Result r)
        {
            if (r == null) return null;
            bool en = AppLanguage.IsEnglish;
            string line = en
                ? $"Document scope: {r.ChangedTargets} of {r.Targets} listed element(s) changed, {r.ChangedOutside} outside the list"
                : $"문서 대상 대조: 목록 {r.Targets}건 중 {r.ChangedTargets}건 변경, 목록 밖 변경 {r.ChangedOutside}건";
            if (r.ChangedOutside > 0)
                line = "⚠ " + line + (en ? " (ids: " : " (ID: ") + string.Join(", ", r.OutsideSample) +
                       (r.ChangedOutside > r.OutsideSample.Count ? ", …" : "") + ")";
            if (r.Partial)
                line += en ? " — partial: more changes than the reported id list" : " — 일부만 대조(변경 수가 보고된 ID 목록보다 많음)";
            return line;
        }
    }
}
