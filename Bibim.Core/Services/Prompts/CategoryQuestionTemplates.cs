// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Text;

namespace Bibim.Core
{
    /// <summary>
    /// Category-specific question templates that the Task Planner used to inline
    /// in its system prompt (~1,800 tokens). They are now built into a compact
    /// reference block (~600 tokens) that is appended to the planner prompt
    /// without verbose framing — the planner LLM doesn't need the full English
    /// explanations to ask the right questions, just a concise checklist.
    ///
    /// Splitting this out also makes it easy to swap to a fully-templated path
    /// later (planner returns category, C# generates the QuestionItem list)
    /// without touching the planner prompt again.
    /// </summary>
    public static class CategoryQuestionTemplates
    {
        /// <summary>
        /// Compact category checklist appended to the planner system prompt.
        /// The planner uses this to construct the <c>questions</c> JSON output
        /// when a task category needs clarification.
        /// </summary>
        public static string BuildPlannerChecklist()
        {
            var sb = new StringBuilder();
            sb.AppendLine("CATEGORY CHECKLIST — ask these when missing for the matching task category:");
            sb.AppendLine();
            sb.AppendLine("scope (any 'all'/broad batch task): target range (view/level/model); include linked models?");
            sb.AppendLine("conflict (batch on existing items): on duplicate name/tag/value → skip / overwrite / error");
            sb.AppendLine("placement: location (coords/level/host face); family type; host type if ambiguous; rotation");
            sb.AppendLine("move: direction+distance (absolute vs relative); confirm targets");
            sb.AppendLine("copy/array: linear vs radial; count+spacing vs count+length vs spacing only; group or not");
            sb.AppendLine("rotate: axis (X/Y/Z/custom); angle; center (element/origin/point)");
            sb.AppendLine("parameter: project vs shared (file path + group if shared); instance vs type; group; binding categories; on duplicate");
            sb.AppendLine("view (plan/RCP/section/elevation/3D): level (for plans); template; view name");
            sb.AppendLine("sheet: title block family+type (REQUIRED if multiple); sheet number/naming");
            sb.AppendLine("placeViewOnSheet: sheet; viewport position; view scale if different");
            sb.AppendLine("annotation/tag/markup: target view; placement location; tag family if multiple; leader; revision (for RevisionCloud)");
            sb.AppendLine("delete: confirm target; how to handle hosted/dependent elements");
            sb.AppendLine("export PDF: output folder (REQUIRED); which sheets/views; combine vs per-file; color/grayscale; paper size");
            sb.AppendLine("export Excel/CSV: output folder (REQUIRED); categories; columns; one sheet per category vs all");
            sb.AppendLine("export DWG/DXF/IFC: output folder (REQUIRED); which views/sheets; export settings");
            sb.AppendLine("schedule: schedule view vs file export; sort/group; filter");
            sb.AppendLine("rename/renumber: scope; naming pattern (prefix/suffix/format); on duplicate");
            sb.AppendLine("workset: which workset (only if worksets visible in [RESOLVED REVIT CONTEXT])");
            sb.AppendLine("distance/proximity: ref point on element A and B (bbox face/origin/face/connector); direction (closest/perpendicular/H/V)");
            sb.AppendLine("units/coords: input unit (mm/ft/project) if ambiguous; coord ref (Project Base Point vs Survey Point)");
            sb.AppendLine("geometry (loft/sweep/blend): solid vs void; split if > 20 profiles");
            sb.AppendLine("grid/level/refplane: spacing+count (XY separately for 2D); origin; naming; extent length; target level");
            sb.AppendLine("CAD import/link: text labels (TextNote vs CAD-embedded); layer name (case-sensitive)");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Heuristic gate that decides whether to bother calling the planner LLM at all.
    /// Greetings, acknowledgements, and very short messages with no actionable content
    /// can skip planning entirely and route straight to chat — saving ~2,500 input
    /// tokens per skip on the planner prompt.
    /// </summary>
    public static class PlannerGate
    {
        // Single-token / short greetings + acknowledgements in EN and KR.
        // Match is whole-token / whole-message, not substring.
        private static readonly HashSet<string> _greetingsAndAcks = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            // English
            "hi", "hello", "hey", "yo", "ok", "okay", "thanks", "thank you", "thx",
            "ty", "cool", "nice", "great", "got it", "alright", "sure", "yes", "no",
            "yep", "nope", "sounds good", "perfect", "good", "bye", "goodbye",
            // Korean
            "안녕", "안녕하세요", "ㅎㅇ", "하이", "ㅇㅋ", "오케이", "ㅇㅇ", "응", "넵",
            "감사", "감사합니다", "고마워", "고맙습니다", "ㄳ", "ㄱㅅ", "수고",
            "수고하셨습니다", "그래", "알겠어", "알겠습니다", "넹", "굿", "조아", "좋아",
            "ㅋㅋ", "ㅎㅎ", "ㅋ", "ㅎ", "음", "흠"
        };

        // Action verbs that strongly imply a Revit task (EN + KR).
        // If any of these appear we always run the planner regardless of length.
        private static readonly string[] _actionVerbsEn =
        {
            "place", "create", "make", "add", "insert", "draw", "generate",
            "move", "shift", "translate", "copy", "duplicate", "mirror", "array",
            "rotate", "delete", "remove", "modify", "change", "edit", "update",
            "rename", "renumber", "set ", "select", "filter", "find", "list",
            "count", "analyze", "analyse", "check", "verify", "validate",
            "export", "import", "save", "load", "build", "split", "join", "tag",
            "annotate", "mark", "section", "elevation", "schedule", "transfer"
        };

        private static readonly string[] _actionVerbsKr =
        {
            "배치", "생성", "만들", "추가", "삽입", "그리", "넣어", "넣어줘",
            "이동", "옮겨", "복사", "복제", "거울", "배열",
            "회전", "삭제", "지워", "지우", "제거", "수정", "변경", "바꿔", "편집",
            "이름", "번호", "설정", "선택", "필터", "찾", "검색", "목록",
            "개수", "카운트", "세어", "분석", "확인", "검토", "검증",
            "내보내", "내보내기", "출력", "저장", "불러", "로드", "분할", "합쳐", "합치",
            "태그", "주석", "표시", "단면", "입면", "스케줄", "전송"
        };

        /// <summary>
        /// Decide whether the planner LLM call can be safely skipped.
        /// Returns true when the message is a greeting/ack with no actionable
        /// content AND there is no active task that could be referring back to it.
        /// </summary>
        /// <param name="userText">The original (resolved) user message text.</param>
        /// <param name="hasActiveTask">True if a task is currently mid-flow (questions
        /// pending, awaiting confirmation, etc.). Always run the planner in that case
        /// because the user's message may be answering / refining the task.</param>
        public static bool ShouldSkipPlanner(string userText, bool hasActiveTask)
        {
            if (hasActiveTask) return false;
            if (string.IsNullOrWhiteSpace(userText)) return false;

            string trimmed = userText.Trim();

            // 1. Exact-match greeting / ack table
            string normalised = trimmed.TrimEnd('.', '!', '?', '~', ' ');
            if (_greetingsAndAcks.Contains(normalised)) return true;

            // 2. Very short messages with no action verb
            if (trimmed.Length < 12 && !ContainsActionVerb(trimmed)) return true;

            return false;
        }

        private static bool ContainsActionVerb(string text)
        {
            foreach (var v in _actionVerbsEn)
                if (text.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (var v in _actionVerbsKr)
                if (text.IndexOf(v, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
