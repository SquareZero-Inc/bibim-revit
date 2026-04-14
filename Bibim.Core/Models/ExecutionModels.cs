// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace Bibim.Core
{
    public enum ExecutionRequestKind
    {
        ExecuteCode,
        UndoLastApply
    }

    /// <summary>
    /// Execution request queued from background thread for main-thread execution.
    /// See design doc §2.3 — ExternalEvent main thread synchronization.
    /// </summary>
    public class ExecutionRequest
    {
        public ExecutionRequestKind Kind { get; set; } = ExecutionRequestKind.ExecuteCode;

        /// <summary>Roslyn-compiled assembly containing the generated code.</summary>
        public Assembly CompiledAssembly { get; set; }

        /// <summary>Entry point type name within the compiled assembly.</summary>
        public string EntryTypeName { get; set; } = "BibimGenerated.Program";

        /// <summary>Entry point method name.</summary>
        public string EntryMethodName { get; set; } = "Execute";

        /// <summary>If true, execute in a Transaction that is always rolled back.</summary>
        public bool IsDryRun { get; set; }

        /// <summary>Expected target document title captured during preview.</summary>
        public string ExpectedDocumentTitle { get; set; }

        /// <summary>Expected target document path captured during preview.</summary>
        public string ExpectedDocumentPath { get; set; }

        /// <summary>Callback to return result to the awaiting background thread.</summary>
        public TaskCompletionSource<ExecutionResult> Callback { get; set; }
    }

    /// <summary>
    /// Result of code execution on the Revit main thread.
    /// </summary>
    public class ExecutionResult
    {
        public bool Success { get; set; }
        public string Output { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public int AffectedElementCount { get; set; }
        public long MemoryBefore { get; set; }
        public long MemoryAfter { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPath { get; set; }

        /// <summary>
        /// Revit warnings collected via Application.FailuresProcessing during execution.
        /// Populated only when warnings fire during commit/dryrun.
        /// </summary>
        public List<string> RevitWarnings { get; set; }

        /// <summary>
        /// Intermediate log entries written by generated code via BibimExecutionContext.Log().
        /// </summary>
        public List<string> ExecutionLogs { get; set; }

        public bool HasRevitWarnings => RevitWarnings != null && RevitWarnings.Count > 0;
        public bool HasExecutionLogs => ExecutionLogs != null && ExecutionLogs.Count > 0;
    }

    /// <summary>
    /// Context object injected into generated code as second parameter.
    /// Generated code calls ctx.Log() to emit intermediate progress messages
    /// that appear in the BIBIM panel after execution.
    ///
    /// Signature: public static object Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx)
    /// </summary>
    public class BibimExecutionContext
    {
        private readonly List<string> _logs = new List<string>();

        public void Log(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                _logs.Add(message);
        }

        public IReadOnlyList<string> GetLogs() => _logs.AsReadOnly();
    }

    internal static class ExecutionResultFormatter
    {
        public static string BuildDryRunOutput(object generatedOutput, int affectedElementCount)
        {
            string text = generatedOutput?.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            return $"Dry run succeeded. {affectedElementCount} elements would be affected.";
        }
    }
}
