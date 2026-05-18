// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Bibim.Core
{
    public static class TaskStages
    {
        public const string NeedsDetails = "needs_details";
        public const string Review = "review";
        public const string Working = "working";
        public const string PreviewReady = "preview_ready";
        public const string Completed = "completed";
        public const string Cancelled = "cancelled";
    }

    public static class TaskKinds
    {
        public const string Read = "read";
        public const string Write = "write";
    }

    /// <summary>
    /// Structured question with selectable options for the Question Card UI.
    /// Supports single-select, multi-select, and free-text input.
    /// </summary>
    public class QuestionItem
    {
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        [JsonProperty("text")]
        public string Text { get; set; }

        /// <summary>
        /// "single" = radio buttons (pick one), "multi" = checkboxes (pick many)
        /// </summary>
        [JsonProperty("selectionType")]
        public string SelectionType { get; set; } = "single";

        [JsonProperty("options")]
        public List<string> Options { get; set; } = new List<string>();

        /// <summary>
        /// The user's selected answer(s). Populated after the user responds.
        /// </summary>
        [JsonProperty("answer")]
        public string Answer { get; set; }

        /// <summary>
        /// Whether this question was skipped by the user.
        /// </summary>
        [JsonProperty("skipped")]
        public bool Skipped { get; set; }
    }

    public class TaskReviewSummary
    {
        [JsonProperty("safeCount")]
        public int SafeCount { get; set; }

        [JsonProperty("versionSpecificCount")]
        public int VersionSpecificCount { get; set; }

        [JsonProperty("deprecatedCount")]
        public int DeprecatedCount { get; set; }

        [JsonProperty("affectedElementCount")]
        public int AffectedElementCount { get; set; }

        [JsonProperty("previewSuccess")]
        public bool PreviewSuccess { get; set; }

        [JsonProperty("previewError")]
        public string PreviewError { get; set; }

        [JsonProperty("executionSummary")]
        public string ExecutionSummary { get; set; }

        [JsonProperty("analyzerDiagnostics")]
        public List<TaskDiagnosticSummary> AnalyzerDiagnostics { get; set; } = new List<TaskDiagnosticSummary>();
    }

    public class TaskDiagnosticSummary
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("line")]
        public int Line { get; set; }
    }

    public class TaskState
    {
        [JsonProperty("taskId")]
        public string TaskId { get; set; } = Guid.NewGuid().ToString();

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; } = TaskKinds.Write;

        [JsonProperty("stage")]
        public string Stage { get; set; } = TaskStages.NeedsDetails;

        [JsonProperty("steps")]
        public List<string> Steps { get; set; } = new List<string>();

        [JsonProperty("questions")]
        public List<QuestionItem> Questions { get; set; } = new List<QuestionItem>();

        [JsonProperty("requiresApply")]
        public bool RequiresApply { get; set; }

        [JsonProperty("wasApplied")]
        public bool WasApplied { get; set; }

        /// <summary>
        /// Set to the actual execResult.Success value after code execution.
        /// Defaults to true for non-execution completed tasks (text responses, etc.).
        /// Used by DetectResultError to avoid false-positive error UI from regex on output text.
        /// </summary>
        [JsonProperty("executionSuccess")]
        public bool ExecutionSuccess { get; set; } = true;

        [JsonProperty("autoOpen")]
        public bool AutoOpen { get; set; }

        [JsonProperty("sourceUserMessage")]
        public string SourceUserMessage { get; set; }

        [JsonProperty("collectedInputs")]
        public List<string> CollectedInputs { get; set; } = new List<string>();

        [JsonProperty("generatedCode")]
        public string GeneratedCode { get; set; }

        [JsonProperty("targetDocumentTitle")]
        public string TargetDocumentTitle { get; set; }

        [JsonProperty("targetDocumentPath")]
        public string TargetDocumentPath { get; set; }

        [JsonProperty("resultSummary")]
        public string ResultSummary { get; set; }

        [JsonProperty("review")]
        public TaskReviewSummary Review { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("updatedAt")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TaskPlanResponse
    {
        [JsonProperty("mode")]
        public string Mode { get; set; }

        [JsonProperty("taskKind")]
        public string TaskKind { get; set; }

        [JsonProperty("taskRelation")]
        public string TaskRelation { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("steps")]
        public List<string> Steps { get; set; } = new List<string>();

        [JsonProperty("questions")]
        public List<QuestionItem> Questions { get; set; } = new List<QuestionItem>();

        [JsonProperty("assistantMessage")]
        public string AssistantMessage { get; set; }

        [JsonProperty("shouldAutoRun")]
        public bool ShouldAutoRun { get; set; }
    }
}
