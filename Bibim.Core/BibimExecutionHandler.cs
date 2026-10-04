// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace Bibim.Core
{
    /// <summary>
    /// IExternalEventHandler that processes the execution queue on Revit's main thread.
    /// 
    /// Flow (design doc §2.3):
    ///   [Background] LLM response → Roslyn compile → Enqueue(request) → ExternalEvent.Raise()
    ///   [Main Thread] Revit Idle → Execute() called → Transaction → Run code → Return result
    ///   [Background]  await TaskCompletionSource → UI update
    /// </summary>
    public class BibimExecutionHandler : IExternalEventHandler
    {
        private readonly ConcurrentQueue<ExecutionRequest> _queue = new ConcurrentQueue<ExecutionRequest>();

        // Track modified elements during transaction via DocumentChanged event
        // (union + per-kind buckets for verification and the audit log).
        private HashSet<ElementId> _modifiedElementIds;
        private HashSet<ElementId> _addedIds;
        private HashSet<ElementId> _changedIds;
        private HashSet<ElementId> _deletedIds;
        private const int MaxReportedElementIds = 500;

        // Collect Revit warnings fired during FailuresProcessing event
        private List<string> _collectedWarnings;

        /// <summary>
        /// Static flag indicating whether the current execution is a dry-run.
        /// Generated code can check BibimExecutionHandler.IsDryRun to adjust
        /// file I/O behavior (e.g. append _BIBIM_TEST to output file names).
        /// </summary>
        public static bool IsDryRun { get; internal set; }

        /// <summary>
        /// Enqueue an execution request from a background thread.
        /// Call BibimApp.ExecutionEvent.Raise() after enqueuing.
        /// </summary>
        public void Enqueue(ExecutionRequest request)
        {
            _queue.Enqueue(request);
        }

        /// <summary>
        /// Called by Revit on the main thread when ExternalEvent is raised.
        /// </summary>
        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out var request))
            {
                var result = new ExecutionResult();
                result.MemoryBefore = GC.GetTotalMemory(false);

                try
                {
                    var doc = app.ActiveUIDocument?.Document;
                    PopulateDocumentInfo(result, doc);

                    if (request.Kind == ExecutionRequestKind.UndoLastApply)
                    {
                        RunUndo(app, request, result);
                        request.Callback?.TrySetResult(result);
                        continue;
                    }

                    if (doc == null)
                    {
                        result.Success = false;
                        result.ErrorMessage = "No active document.";
                        request.Callback?.TrySetResult(result);
                        continue;
                    }

                    if (!TryValidateExpectedDocument(doc, request, out var mismatchMessage))
                    {
                        result.Success = false;
                        result.ErrorMessage = mismatchMessage;
                        request.Callback?.TrySetResult(result);
                        continue;
                    }

                    if (request.IsDryRun)
                        RunDryRun(doc, app, request, result);
                    else
                        RunCommit(doc, app, request, result);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Exception = ex;
                    result.ErrorMessage = ex.Message;
                    Logger.LogError("BibimExecutionHandler", ex);
                }
                finally
                {
                    // COM Object Leak defense (design doc §2.3)
                    PerformComCleanup();
                    result.MemoryAfter = GC.GetTotalMemory(false);

                    long delta = result.MemoryAfter - result.MemoryBefore;
                    if (delta > 50 * 1024 * 1024) // >50MB growth warning
                    {
                        Logger.Log("BibimExecutionHandler",
                            $"[MEM_WARNING] Memory grew by {delta / 1024 / 1024}MB during execution");
                    }

                    request.Callback?.TrySetResult(result);
                }
            }
        }

        public string GetName() => "BIBIM Code Execution Handler";

        private void RunUndo(UIApplication app, ExecutionRequest request, ExecutionResult result)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                PopulateDocumentInfo(result, doc);

                if (doc == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "No active document.";
                    return;
                }

                if (!TryValidateExpectedDocument(doc, request, out var mismatchMessage))
                {
                    result.Success = false;
                    result.ErrorMessage = mismatchMessage;
                    return;
                }

                var undoId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
                if (undoId == null)
                {
                    result.Success = false;
                    result.ErrorMessage = "Undo command is unavailable.";
                    return;
                }

                app.PostCommand(undoId);
                result.Success = true;
                result.Output = "Undo posted.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Exception = ex;
                result.ErrorMessage = ex.Message;
            }
        }

        /// <summary>
        /// Dry run uses TransactionGroup to wrap generated code execution.
        /// 
        /// Generated code manages its own Transactions internally (per prompt rules).
        /// Code that doesn't need a Transaction (e.g. ActiveView change) runs outside any.
        /// TransactionGroup rollback undoes all committed transactions cleanly.
        /// DocumentChanged tracks affected elements from any inner commits.
        /// </summary>
        private void RunDryRun(Document doc, UIApplication app,
            ExecutionRequest request, ExecutionResult result)
        {
            IsDryRun = true;
            ResetChangeTracking();
            _collectedWarnings = new List<string>();
            doc.Application.DocumentChanged += OnDocumentChanged;
            doc.Application.FailuresProcessing += OnFailuresProcessing;

            // Snapshot the user's selection BEFORE the generated code runs. Generated
            // code often ends by selecting its result elements; the group rollback then
            // deletes those elements and Revit clears the selection — so without this
            // snapshot, selection-based commits fail with "no elements selected".
            var preRunSelection = CaptureSelection(app);
            result.SelectionBeforeRun = preRunSelection;

            try
            {
                using (var txGroup = new TransactionGroup(doc, "BIBIM DryRun Group"))
                {
                    txGroup.Start();
                    try
                    {
                        var ctx = new BibimExecutionContext();
                        var output = InvokeGeneratedCode(request, app, ctx);

                        CopyChangeTracking(result);
                        result.Success = true;
                        result.Output = ExecutionResultFormatter.BuildDryRunOutput(
                            output,
                            result.AffectedElementCount);

                        var logs = ctx.GetLogs();
                        if (logs.Count > 0)
                            result.ExecutionLogs = new List<string>(logs);
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie && tie.InnerException != null
                            ? tie.InnerException : ex;
                        result.Success = false;
                        result.Exception = inner;
                        result.ErrorMessage = $"[DryRun Error] {inner.Message}";
                    }

                    // RollBack the group — undoes all committed transactions,
                    // restoring the document to its original state
                    txGroup.RollBack();
                }
            }
            finally
            {
                doc.Application.DocumentChanged -= OnDocumentChanged;
                doc.Application.FailuresProcessing -= OnFailuresProcessing;
                IsDryRun = false;
            }

            // If the rollback wiped the user's selection, restore it so (a) the user
            // doesn't have to reselect before commit and (b) a self-correction re-run
            // of the dry-run still sees the original element set.
            RestoreSelectionIfEmpty(app, doc, preRunSelection, "post-dryrun");

            if (_collectedWarnings.Count > 0)
                result.RevitWarnings = new List<string>(_collectedWarnings);

            Logger.Log("BibimExecutionHandler",
                $"[DryRun] Affected: {result.AffectedElementCount} (commit+group-rollback pattern)");
        }

        /// <summary>
        /// Commit run — generated code manages its own Transactions internally.
        /// No outer Transaction wrapper so UI operations (ActiveView, Selection, etc.) work.
        /// DocumentChanged still tracks affected elements from any inner commits.
        /// </summary>
        private void RunCommit(Document doc, UIApplication app,
            ExecutionRequest request, ExecutionResult result)
        {
            IsDryRun = false;

            // Defensive restore: if preview (or anything since) emptied the selection,
            // put back the ids captured at preview start so selection-based generated
            // code targets the same set the user previewed. A deliberate new selection
            // (non-empty) is always respected.
            RestoreSelectionIfEmpty(app, doc, request.SelectionSnapshot, "pre-commit");

            ResetChangeTracking();
            _collectedWarnings = new List<string>();
            doc.Application.DocumentChanged += OnDocumentChanged;
            doc.Application.FailuresProcessing += OnFailuresProcessing;

            using (var txGroup = new TransactionGroup(doc, "BIBIM Apply Group"))
            {
                txGroup.Start();
                try
                {
                    var ctx = new BibimExecutionContext();
                    var output = InvokeGeneratedCode(request, app, ctx);
                    result.Success = true;
                    CopyChangeTracking(result);
                    result.Output = output?.ToString() ?? "Execution completed.";
                    txGroup.Assimilate();

                    var logs = ctx.GetLogs();
                    if (logs.Count > 0)
                        result.ExecutionLogs = new List<string>(logs);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException : ex;
                    result.Success = false;
                    result.Exception = inner;
                    result.ErrorMessage = inner.Message;

                    try
                    {
                        txGroup.RollBack();
                    }
                    catch (Exception rollbackEx)
                    {
                        Logger.Log("BibimExecutionHandler",
                            $"[CommitRollback] Warning: {rollbackEx.Message}");
                    }
                }
                finally
                {
                    doc.Application.DocumentChanged -= OnDocumentChanged;
                    doc.Application.FailuresProcessing -= OnFailuresProcessing;
                }
            }

            if (_collectedWarnings.Count > 0)
                result.RevitWarnings = new List<string>(_collectedWarnings);
        }

        private void ResetChangeTracking()
        {
            _modifiedElementIds = new HashSet<ElementId>();
            _addedIds = new HashSet<ElementId>();
            _changedIds = new HashSet<ElementId>();
            _deletedIds = new HashSet<ElementId>();
        }

        private void CopyChangeTracking(ExecutionResult result)
        {
            result.AffectedElementCount = _modifiedElementIds?.Count ?? 0;
            result.AddedCount = _addedIds?.Count ?? 0;
            result.ModifiedCount = _changedIds?.Count ?? 0;
            result.DeletedCount = _deletedIds?.Count ?? 0;
            result.AffectedElementIds = _modifiedElementIds == null
                ? new List<long>()
                : _modifiedElementIds.Take(MaxReportedElementIds).Select(ToLong).ToList();
        }

        /// <summary>ElementId → integer across API generations (IntegerValue was
        /// replaced by the 64-bit Value in Revit 2024).</summary>
        private static long ToLong(ElementId id)
        {
#if REVIT_2022 || REVIT_2023
            return id.IntegerValue;
#else
            return id.Value;
#endif
        }

        /// <summary>Snapshot of the current selection (empty list when nothing selected).</summary>
        private static List<ElementId> CaptureSelection(UIApplication app)
        {
            try
            {
                var ids = app.ActiveUIDocument?.Selection?.GetElementIds();
                return ids != null ? ids.ToList() : new List<ElementId>();
            }
            catch (Exception ex)
            {
                Logger.Log("BibimExecutionHandler", $"[Selection] capture failed: {ex.Message}");
                return new List<ElementId>();
            }
        }

        /// <summary>
        /// Restore <paramref name="snapshot"/> as the selection — only when the live
        /// selection is currently empty (never stomps a deliberate user selection) and
        /// only ids that still resolve to live elements.
        /// </summary>
        private static void RestoreSelectionIfEmpty(UIApplication app, Document doc,
            IList<ElementId> snapshot, string stage)
        {
            if (snapshot == null || snapshot.Count == 0) return;
            try
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null || uidoc.Document != doc) return;
                if (uidoc.Selection.GetElementIds().Count > 0) return;

                var alive = snapshot.Where(id => doc.GetElement(id) != null).ToList();
                if (alive.Count == 0) return;

                uidoc.Selection.SetElementIds(alive);
                Logger.Log("BibimExecutionHandler",
                    $"[Selection] restored {alive.Count}/{snapshot.Count} ids ({stage})");
            }
            catch (Exception ex)
            {
                Logger.Log("BibimExecutionHandler", $"[Selection] restore failed ({stage}): {ex.Message}");
            }
        }

        /// <summary>
        /// DocumentChanged event handler — captures all element IDs modified during the transaction.
        /// </summary>
        private void OnDocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            try
            {
                foreach (var id in e.GetAddedElementIds())
                {
                    _modifiedElementIds?.Add(id);
                    _addedIds?.Add(id);
                }
                foreach (var id in e.GetModifiedElementIds())
                {
                    _modifiedElementIds?.Add(id);
                    _changedIds?.Add(id);
                }
                foreach (var id in e.GetDeletedElementIds())
                {
                    _modifiedElementIds?.Add(id);
                    _deletedIds?.Add(id);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("BibimExecutionHandler", $"[DocumentChanged] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Invoke the entry point of the Roslyn-compiled assembly.
        /// Generated code signature: public static object Execute(UIApplication app, Bibim.Core.BibimExecutionContext ctx)
        /// Falls back to (UIApplication) signature for legacy compiled assemblies.
        /// </summary>
        private object InvokeGeneratedCode(ExecutionRequest request, UIApplication app, BibimExecutionContext ctx)
        {
            // Primary: look up the expected entry type (BibimGenerated.Program)
            var type = request.CompiledAssembly.GetType(request.EntryTypeName);

            // Fallback: LLM sometimes emits a top-level class (e.g. BibimScript) that has
            // an Execute method but is not wrapped in BibimGenerated.Program — scan the
            // assembly for any type that exposes a compatible static Execute method.
            if (type == null)
            {
                var twoParamTypes = new[] { typeof(Autodesk.Revit.UI.UIApplication), typeof(BibimExecutionContext) };
                var oneParamTypes = new[] { typeof(Autodesk.Revit.UI.UIApplication) };

                type = request.CompiledAssembly.GetTypes().FirstOrDefault(t =>
                    t.GetMethod(request.EntryMethodName, BindingFlags.Public | BindingFlags.Static, null, twoParamTypes, null) != null ||
                    t.GetMethod(request.EntryMethodName, BindingFlags.Public | BindingFlags.Static, null, oneParamTypes, null) != null);
            }

            if (type == null)
                throw new InvalidOperationException(
                    $"Entry type '{request.EntryTypeName}' not found in compiled assembly.");

            // Try two-parameter signature first (UIApplication + BibimExecutionContext)
            var method = type.GetMethod(request.EntryMethodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Autodesk.Revit.UI.UIApplication), typeof(BibimExecutionContext) },
                null);

            if (method != null)
                return method.Invoke(null, new object[] { app, ctx });

            // Fallback: legacy single-parameter signature (UIApplication only)
            method = type.GetMethod(request.EntryMethodName,
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Autodesk.Revit.UI.UIApplication) },
                null);

            if (method == null)
                throw new InvalidOperationException(
                    $"Entry method '{request.EntryMethodName}' not found on type '{type.FullName}'.");

            return method.Invoke(null, new object[] { app });
        }

        /// <summary>
        /// Application.FailuresProcessing handler — collects Revit warnings/errors
        /// that fire during generated code execution. Auto-dismisses warnings to prevent
        /// modal dialogs blocking automation. Errors are left for default handling.
        /// </summary>
        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs args)
        {
            try
            {
                var accessor = args.GetFailuresAccessor();
                foreach (var msg in accessor.GetFailureMessages())
                {
                    string description = msg.GetDescriptionText();
                    if (!string.IsNullOrWhiteSpace(description))
                        _collectedWarnings?.Add(description);
                }

                // Auto-dismiss warnings (severity < Error) to prevent modal blocking
                accessor.DeleteAllWarnings();
            }
            catch (Exception ex)
            {
                Logger.Log("BibimExecutionHandler", $"[FailuresProcessing] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// COM Object Leak defense (design doc §2.3).
        /// Force GC to release any lingering COM references before sandbox unload.
        /// </summary>
        private void PerformComCleanup()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex)
            {
                Logger.Log("BibimExecutionHandler",
                    $"[COM_CLEANUP] Warning during cleanup: {ex.Message}");
            }
        }

        private static void PopulateDocumentInfo(ExecutionResult result, Document doc)
        {
            if (result == null || doc == null)
                return;

            result.DocumentTitle = doc.Title;
            result.DocumentPath = doc.PathName;
        }

        private static bool TryValidateExpectedDocument(
            Document doc,
            ExecutionRequest request,
            out string errorMessage)
        {
            errorMessage = null;

            if (doc == null)
            {
                errorMessage = "No active document.";
                return false;
            }

            if (request == null)
                return true;

            bool hasExpectedPath = !string.IsNullOrWhiteSpace(request.ExpectedDocumentPath);
            bool hasExpectedTitle = !string.IsNullOrWhiteSpace(request.ExpectedDocumentTitle);
            if (!hasExpectedPath && !hasExpectedTitle)
                return true;

            if (hasExpectedPath &&
                !string.Equals(doc.PathName ?? string.Empty,
                    request.ExpectedDocumentPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = BuildDocumentMismatchMessage(request);
                return false;
            }

            if (!hasExpectedPath && hasExpectedTitle &&
                !string.Equals(doc.Title ?? string.Empty,
                    request.ExpectedDocumentTitle,
                    StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = BuildDocumentMismatchMessage(request);
                return false;
            }

            return true;
        }

        private static string BuildDocumentMismatchMessage(ExecutionRequest request)
        {
            string target = !string.IsNullOrWhiteSpace(request?.ExpectedDocumentPath)
                ? request.ExpectedDocumentPath
                : request?.ExpectedDocumentTitle;
            if (string.IsNullOrWhiteSpace(target))
                return "The active document does not match the preview document.";

            return $"The active document does not match the preview document: {target}";
        }
    }
}
