// Copyright (c) 2026 SquareZero Inc. - Licensed under Apache 2.0. See LICENSE in the repo root.
namespace Bibim.Core
{
    /// <summary>
    /// Centralized constants for BIBIM AI.
    /// </summary>
    internal static class BibimConstants
    {
        // ── LLM context window ─────────────────────────────────────────────
        /// <summary>Sliding window: max chat turns passed to the LLM per request.</summary>
        public const int ChatHistoryMaxTurns = 20;

        /// <summary>Context window for the lightweight planner LLM instance.</summary>
        public const int PlannerContextWindow = 8;

        // ── External URLs ──────────────────────────────────────────────────
        public const string GitHubRevitRepo   = "https://github.com/SquareZero-Inc/bibim-revit";
        public const string GitHubDynamoRepo  = "https://github.com/SquareZero-Inc/bibim-dynamo";
        public const string GitHubIssuesUrl   = "https://github.com/SquareZero-Inc/bibim-revit/issues/new/choose";
    }
}
