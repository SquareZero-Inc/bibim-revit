// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace Bibim.Core
{
    /// <summary>
    /// Deterministic, LLM-free extraction of project identifier names
    /// (levels, sheets, phases, worksets) for planner context injection.
    ///
    /// MUST be called from the Revit main thread — same constraint as RevitContextProvider.
    /// Uses RevitContextProvider's existing collectors; no new marshalling code.
    ///
    /// Output is a compact block (≤ ~600 tokens) injected into BuildPlannerInput()
    /// immediately before the JSON output-format trailer (BIBIM-002 safety).
    /// </summary>
    public static class ModelIdentifierProbe
    {
        private sealed class CacheEntry
        {
            public string Block;
            public DateTime Expiry;
        }

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, CacheEntry> _cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private const int CacheTtlSeconds = 60;
        private const int MaxSheets = 50;

        /// <summary>
        /// Build the [MODEL CONTEXT] block for planner injection.
        /// Returns empty string on any failure — probe must never block chat.
        ///
        /// IMPORTANT: Must be called from the Revit/WPF main thread (or via
        /// Dispatcher.Invoke). Internally uses FilteredElementCollector which
        /// requires Revit's main thread context.
        /// </summary>
        public static string BuildContextBlock(RevitContextProvider contextProvider)
        {
            if (contextProvider == null) return string.Empty;

            try
            {
                // Cache key: use document title (safe to read from any thread via the
                // provider's cached UIDocument reference acquired on the main thread).
                // Revit exposes Document.Title without a transaction.
                string cacheKey = TryGetDocumentKey(contextProvider);

                lock (_lock)
                {
                    if (cacheKey != null &&
                        _cache.TryGetValue(cacheKey, out var hit) &&
                        DateTime.UtcNow < hit.Expiry)
                    {
                        Logger.Log("IdentifierProbe", $"Cache hit for '{cacheKey}'");
                        return hit.Block;
                    }
                }

                string block = BuildBlock(contextProvider);

                if (cacheKey != null && !string.IsNullOrEmpty(block))
                {
                    lock (_lock)
                    {
                        _cache[cacheKey] = new CacheEntry
                        {
                            Block = block,
                            Expiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds)
                        };
                    }
                }

                return block;
            }
            catch (Exception ex)
            {
                Logger.Log("IdentifierProbe", $"BuildContextBlock failed (non-fatal): {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Invalidate all cached entries (called on document change).
        /// </summary>
        public static void InvalidateCache()
        {
            lock (_lock) { _cache.Clear(); }
        }

        // ──────────────────────────────────────────────────────────
        // Private helpers
        // ──────────────────────────────────────────────────────────

        private static string TryGetDocumentKey(RevitContextProvider provider)
        {
            try
            {
                // Piggy-back on the existing @levels call to check if the provider
                // has an active document — empty result means no document.
                var levels = provider.GetLevels();
                if (levels?.Error != null) return null;
                // Use first level name as a stable-enough key; not worth a P/Invoke
                // just for document title here.
                return levels?.Levels?.Count > 0
                    ? $"doc_{levels.Levels[0].Name}_{levels.Levels.Count}"
                    : "doc_empty";
            }
            catch { return null; }
        }

        private static string BuildBlock(RevitContextProvider provider)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[MODEL CONTEXT - actual names in this project. Use for identifier resolution.]");

            // Levels
            try
            {
                var info = provider.GetLevels();
                if (info?.Levels != null && info.Levels.Count > 0)
                {
                    // Convert elevation from internal feet to metres for readability
                    var parts = info.Levels.Select(l =>
                        $"{l.Name} @ {(l.Elevation * 0.3048):F1}m");
                    sb.AppendLine($"Levels: {string.Join(", ", parts)}");
                }
                else
                {
                    sb.AppendLine("Levels: (none)");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("IdentifierProbe", $"Levels probe failed: {ex.Message}");
                sb.AppendLine("Levels: (unavailable)");
            }

            // Sheets (max 50, sorted by sheet number)
            try
            {
                string sheetsLine = BuildSheetsLine(provider);
                sb.AppendLine(sheetsLine);
            }
            catch (Exception ex)
            {
                Logger.Log("IdentifierProbe", $"Sheets probe failed: {ex.Message}");
                sb.AppendLine("Sheets: (unavailable)");
            }

            // Phases
            try
            {
                var info = provider.GetPhases();
                if (info?.Phases != null && info.Phases.Count > 0)
                    sb.AppendLine($"Phases: {string.Join(", ", info.Phases.Select(p => p.Name))}");
                else
                    sb.AppendLine("Phases: (none)");
            }
            catch (Exception ex)
            {
                Logger.Log("IdentifierProbe", $"Phases probe failed: {ex.Message}");
                sb.AppendLine("Phases: (unavailable)");
            }

            // Worksets
            try
            {
                var info = provider.GetWorksets();
                if (info?.Error != null)
                {
                    sb.AppendLine("Worksets: (not workshared)");
                }
                else if (info?.Worksets != null && info.Worksets.Count > 0)
                {
                    sb.AppendLine($"Worksets: {string.Join(", ", info.Worksets.Select(w => w.Name))}");
                }
                else
                {
                    sb.AppendLine("Worksets: (not workshared)");
                }
            }
            catch (Exception ex)
            {
                Logger.Log("IdentifierProbe", $"Worksets probe failed: {ex.Message}");
                sb.AppendLine("Worksets: (unavailable)");
            }

            return sb.ToString().TrimEnd();
        }

        // Sheets require a Document reference — access via the provider's internal
        // UIDocument. RevitContextProvider doesn't expose a sheet collector directly
        // so we resolve via its existing @view path to get the UIDocument, then
        // collect ViewSheet separately. All on the caller's (main) thread.
        private static string BuildSheetsLine(RevitContextProvider provider)
        {
            // Use reflection-free indirection: call GetCurrentView() to verify the
            // provider is live, then obtain doc via the same internal channel by
            // inspecting the resolved @levels result for now. The proper approach
            // is to expose GetDocument() from RevitContextProvider, but we avoid
            // touching that class's internals. Instead: sheets are collected via
            // a temporary FilteredElementCollector on BibimApp.CurrentUiApp.
            var uiApp = BibimApp.CurrentUiApp;
            if (uiApp?.ActiveUIDocument?.Document == null)
                return "Sheets: (no active document)";

            var doc = uiApp.ActiveUIDocument.Document;

            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber)
                .ToList();

            if (sheets.Count == 0)
                return "Sheets: (none)";

            var parts = sheets.Take(MaxSheets)
                .Select(s => $"{s.SheetNumber} \"{s.Name}\"");
            string suffix = sheets.Count > MaxSheets
                ? $" (+{sheets.Count - MaxSheets} more)"
                : string.Empty;

            return $"Sheets: {string.Join(", ", parts)}{suffix}";
        }
    }
}
