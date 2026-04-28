// Copyright (c) 2026 SquareZero Inc. - Licensed under Apache 2.0. See LICENSE in the repo root.
namespace Bibim.Core
{
    /// <summary>
    /// Centralized constants for BIBIM AI.
    /// </summary>
    internal static class BibimConstants
    {
        // ── LLM context window ─────────────────────────────────────────────
        /// <summary>Sliding window: max chat turns passed to the LLM per request.
        /// Anything beyond this is collapsed into a synthetic "[Earlier session
        /// context...]" message via <see cref="HistorySummariser"/>.</summary>
        public const int ChatHistoryMaxTurns = 10;

        /// <summary>Context window for the lightweight planner LLM instance.</summary>
        public const int PlannerContextWindow = 6;

        /// <summary>Per-turn character cap when serialising history into the planner
        /// input. Long code blocks and verbose outputs are clipped so a single noisy
        /// turn doesn't blow the planner prompt.</summary>
        public const int PlannerHistoryTurnMaxChars = 240;

        /// <summary>Cap on additional-context blob (collected user answers, etc.)
        /// appended to a codegen task prompt. Beyond this it's truncated with a
        /// "..." marker.</summary>
        public const int AdditionalContextMaxChars = 2000;

        // ── External URLs ──────────────────────────────────────────────────
        public const string GitHubRevitRepo   = "https://github.com/SquareZero-Inc/bibim-revit";
        public const string GitHubDynamoRepo  = "https://github.com/SquareZero-Inc/bibim-dynamo";
        public const string GitHubIssuesUrl   = "https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose";
    }
}
