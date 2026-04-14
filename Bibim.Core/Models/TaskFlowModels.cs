// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
#if NET48
using Newtonsoft.Json;
#else
using System.Text.Json.Serialization;
#endif

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
#if NET48
        [JsonProperty("id")]
#else
        [JsonPropertyName("id")]
#endif
        public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

#if NET48
        [JsonProperty("text")]
#else
        [JsonPropertyName("text")]
#endif
        public string Text { get; set; }

        /// <summary>
        /// "single" = radio buttons (pick one), "multi" = checkboxes (pick many)
        /// </summary>
#if NET48
        [JsonProperty("selectionType")]
#else
        [JsonPropertyName("selectionType")]
#endif
        public string SelectionType { get; set; } = "single";

#if NET48
        [JsonProperty("options")]
#else
        [JsonPropertyName("options")]
#endif
        public List<string> Options { get; set; } = new List<string>();

        /// <summary>
        /// The user's selected answer(s). Populated after the user responds.
        /// </summary>
#if NET48
        [JsonProperty("answer")]
#else
        [JsonPropertyName("answer")]
#endif
        public string Answer { get; set; }

        /// <summary>
        /// Whether this question was skipped by the user.
        /// </summary>
#if NET48
        [JsonProperty("skipped")]
#else
        [JsonPropertyName("skipped")]
#endif
        public bool Skipped { get; set; }
    }

    public class TaskReviewSummary
    {
#if NET48
        [JsonProperty("safeCount")]
#else
        [JsonPropertyName("safeCount")]
#endif
        public int SafeCount { get; set; }

#if NET48
        [JsonProperty("versionSpecificCount")]
#else
        [JsonPropertyName("versionSpecificCount")]
#endif
        public int VersionSpecificCount { get; set; }

#if NET48
        [JsonProperty("deprecatedCount")]
#else
        [JsonPropertyName("deprecatedCount")]
#endif
        public int DeprecatedCount { get; set; }

#if NET48
        [JsonProperty("affectedElementCount")]
#else
        [JsonPropertyName("affectedElementCount")]
#endif
        public int AffectedElementCount { get; set; }

#if NET48
        [JsonProperty("previewSuccess")]
#else
        [JsonPropertyName("previewSuccess")]
#endif
        public bool PreviewSuccess { get; set; }

#if NET48
        [JsonProperty("previewError")]
#else
        [JsonPropertyName("previewError")]
#endif
        public string PreviewError { get; set; }

#if NET48
        [JsonProperty("executionSummary")]
#else
        [JsonPropertyName("executionSummary")]
#endif
        public string ExecutionSummary { get; set; }

#if NET48
        [JsonProperty("analyzerDiagnostics")]
#else
        [JsonPropertyName("analyzerDiagnostics")]
#endif
        public List<TaskDiagnosticSummary> AnalyzerDiagnostics { get; set; } = new List<TaskDiagnosticSummary>();
    }

    public class TaskDiagnosticSummary
    {
#if NET48
        [JsonProperty("id")]
#else
        [JsonPropertyName("id")]
#endif
        public string Id { get; set; }

#if NET48
        [JsonProperty("message")]
#else
        [JsonPropertyName("message")]
#endif
        public string Message { get; set; }

#if NET48
        [JsonProperty("severity")]
#else
        [JsonPropertyName("severity")]
#endif
        public string Severity { get; set; }

#if NET48
        [JsonProperty("line")]
#else
        [JsonPropertyName("line")]
#endif
        public int Line { get; set; }
    }

    public class TaskState
    {
#if NET48
        [JsonProperty("taskId")]
#else
        [JsonPropertyName("taskId")]
#endif
        public string TaskId { get; set; } = Guid.NewGuid().ToString();

#if NET48
        [JsonProperty("title")]
#else
        [JsonPropertyName("title")]
#endif
        public string Title { get; set; }

#if NET48
        [JsonProperty("summary")]
#else
        [JsonPropertyName("summary")]
#endif
        public string Summary { get; set; }

#if NET48
        [JsonProperty("kind")]
#else
        [JsonPropertyName("kind")]
#endif
        public string Kind { get; set; } = TaskKinds.Write;

#if NET48
        [JsonProperty("stage")]
#else
        [JsonPropertyName("stage")]
#endif
        public string Stage { get; set; } = TaskStages.NeedsDetails;

#if NET48
        [JsonProperty("steps")]
#else
        [JsonPropertyName("steps")]
#endif
        public List<string> Steps { get; set; } = new List<string>();

#if NET48
        [JsonProperty("questions")]
#else
        [JsonPropertyName("questions")]
#endif
        public List<QuestionItem> Questions { get; set; } = new List<QuestionItem>();

#if NET48
        [JsonProperty("requiresApply")]
#else
        [JsonPropertyName("requiresApply")]
#endif
        public bool RequiresApply { get; set; }

#if NET48
        [JsonProperty("wasApplied")]
#else
        [JsonPropertyName("wasApplied")]
#endif
        public bool WasApplied { get; set; }

        /// <summary>
        /// Set to the actual execResult.Success value after code execution.
        /// Defaults to true for non-execution completed tasks (text responses, etc.).
        /// Used by DetectResultError to avoid false-positive error UI from regex on output text.
        /// </summary>
#if NET48
        [JsonProperty("executionSuccess")]
#else
        [JsonPropertyName("executionSuccess")]
#endif
        public bool ExecutionSuccess { get; set; } = true;

#if NET48
        [JsonProperty("autoOpen")]
#else
        [JsonPropertyName("autoOpen")]
#endif
        public bool AutoOpen { get; set; }

#if NET48
        [JsonProperty("sourceUserMessage")]
#else
        [JsonPropertyName("sourceUserMessage")]
#endif
        public string SourceUserMessage { get; set; }

#if NET48
        [JsonProperty("collectedInputs")]
#else
        [JsonPropertyName("collectedInputs")]
#endif
        public List<string> CollectedInputs { get; set; } = new List<string>();

#if NET48
        [JsonProperty("generatedCode")]
#else
        [JsonPropertyName("generatedCode")]
#endif
        public string GeneratedCode { get; set; }

#if NET48
        [JsonProperty("targetDocumentTitle")]
#else
        [JsonPropertyName("targetDocumentTitle")]
#endif
        public string TargetDocumentTitle { get; set; }

#if NET48
        [JsonProperty("targetDocumentPath")]
#else
        [JsonPropertyName("targetDocumentPath")]
#endif
        public string TargetDocumentPath { get; set; }

#if NET48
        [JsonProperty("resultSummary")]
#else
        [JsonPropertyName("resultSummary")]
#endif
        public string ResultSummary { get; set; }

#if NET48
        [JsonProperty("review")]
#else
        [JsonPropertyName("review")]
#endif
        public TaskReviewSummary Review { get; set; }

#if NET48
        [JsonProperty("createdAt")]
#else
        [JsonPropertyName("createdAt")]
#endif
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

#if NET48
        [JsonProperty("updatedAt")]
#else
        [JsonPropertyName("updatedAt")]
#endif
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class TaskPlanResponse
    {
#if NET48
        [JsonProperty("mode")]
#else
        [JsonPropertyName("mode")]
#endif
        public string Mode { get; set; }

#if NET48
        [JsonProperty("taskKind")]
#else
        [JsonPropertyName("taskKind")]
#endif
        public string TaskKind { get; set; }

#if NET48
        [JsonProperty("taskRelation")]
#else
        [JsonPropertyName("taskRelation")]
#endif
        public string TaskRelation { get; set; }

#if NET48
        [JsonProperty("title")]
#else
        [JsonPropertyName("title")]
#endif
        public string Title { get; set; }

#if NET48
        [JsonProperty("summary")]
#else
        [JsonPropertyName("summary")]
#endif
        public string Summary { get; set; }

#if NET48
        [JsonProperty("steps")]
#else
        [JsonPropertyName("steps")]
#endif
        public List<string> Steps { get; set; } = new List<string>();

#if NET48
        [JsonProperty("questions")]
#else
        [JsonPropertyName("questions")]
#endif
        public List<QuestionItem> Questions { get; set; } = new List<QuestionItem>();

#if NET48
        [JsonProperty("assistantMessage")]
#else
        [JsonPropertyName("assistantMessage")]
#endif
        public string AssistantMessage { get; set; }

#if NET48
        [JsonProperty("shouldAutoRun")]
#else
        [JsonPropertyName("shouldAutoRun")]
#endif
        public bool ShouldAutoRun { get; set; }
    }
}
