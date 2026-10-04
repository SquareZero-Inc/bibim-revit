// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace Bibim.Core
{
    /// <summary>
    /// Typed read tools (v1.2.0): exact counts and element listings answered directly from
    /// the model, so "how many doors on L2?" takes seconds instead of a generate → compile
    /// → dry-run cycle. MAIN THREAD ONLY (same rule as the rest of this class).
    /// Output is plain text for the LLM; truncation is always stated explicitly so the
    /// model never computes totals from a partial list.
    /// </summary>
    public partial class RevitContextProvider
    {
        public const int MaxListRows = 100;
        private const int MaxTypeBreakdown = 25;
        private const int MaxParamValueChars = 60;

        public string CountElements(string category, string scope, string levelName)
        {
            var doc = GetDocument();
            if (doc == null) return "[Tool Error] No active document.";

            try
            {
                if (!TryResolveCategoryId(doc, category, out var categoryId, out string categoryLabel, out string categoryError))
                    return "[Tool Error] " + categoryError;
                if (!TryCollect(doc, categoryId, scope, levelName, out var elements, out string scopeLabel, out string collectError))
                    return "[Tool Error] " + collectError;

                var byType = elements
                    .GroupBy(e => TypeLabel(doc, e))
                    .OrderByDescending(g => g.Count())
                    .ToList();

                var sb = new StringBuilder();
                sb.AppendLine($"[count_elements] {categoryLabel} | {scopeLabel} → {elements.Count} element(s) (exact, complete count)");
                if (byType.Count > 0)
                {
                    sb.AppendLine("By type:");
                    foreach (var g in byType.Take(MaxTypeBreakdown))
                        sb.AppendLine($"  - {g.Key}: {g.Count()}");
                    if (byType.Count > MaxTypeBreakdown)
                        sb.AppendLine($"  … {byType.Count - MaxTypeBreakdown} more type(s) not listed (the total above is still exact)");
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                Logger.LogError("RevitContextProvider.CountElements", ex);
                return $"[Tool Error] count_elements failed: {ex.Message}";
            }
        }

        public string ListElements(string category, string scope, string levelName,
            IList<string> parameterNames, int limit)
        {
            var doc = GetDocument();
            if (doc == null) return "[Tool Error] No active document.";
            if (limit <= 0) limit = 50;
            if (limit > MaxListRows) limit = MaxListRows;
            var paramNames = (parameterNames ?? new List<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            try
            {
                if (!TryResolveCategoryId(doc, category, out var categoryId, out string categoryLabel, out string categoryError))
                    return "[Tool Error] " + categoryError;
                if (!TryCollect(doc, categoryId, scope, levelName, out var elements, out string scopeLabel, out string collectError))
                    return "[Tool Error] " + collectError;

                int total = elements.Count;
                var rows = elements.Take(limit).ToList();
                bool truncated = rows.Count < total;

                var sb = new StringBuilder();
                sb.Append($"[list_elements] {categoryLabel} | {scopeLabel} → showing {rows.Count} of {total}");
                sb.AppendLine(truncated
                    ? " (TRUNCATED — do NOT compute totals or sums from this list; write code instead)"
                    : " (complete)");
                sb.AppendLine("Columns: Id | Type | Level" + (paramNames.Count > 0 ? " | " + string.Join(" | ", paramNames) : ""));

                foreach (var e in rows)
                {
                    var cells = new List<string>
                    {
                        e.Id.ToString(),
                        TypeLabel(doc, e),
                        LevelLabel(doc, e)
                    };
                    foreach (var name in paramNames)
                        cells.Add(ReadParameterForDisplay(doc, e, name));
                    sb.AppendLine("- " + string.Join(" | ", cells));
                }
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                Logger.LogError("RevitContextProvider.ListElements", ex);
                return $"[Tool Error] list_elements failed: {ex.Message}";
            }
        }

        // ───────────────────────────── helpers ─────────────────────────────

        private bool TryResolveCategoryId(Document doc, string name, out ElementId categoryId,
            out string label, out string error)
        {
            categoryId = null;
            label = name;
            error = null;
            string n = (name ?? "").Trim();
            if (n.Length == 0)
            {
                error = "'category' is required (English category name such as Walls, Doors, Rooms).";
                return false;
            }

            // 1. English display names (localization-independent).
            if (_categoryBicMap.TryGetValue(n, out BuiltInCategory mapped))
                categoryId = new ElementId(mapped);

            // 2. BuiltInCategory enum names ("OST_Walls", "Walls" → OST_Walls).
            if (categoryId == null)
            {
                string enumName = n.StartsWith("OST_", StringComparison.OrdinalIgnoreCase)
                    ? n
                    : "OST_" + n.Replace(" ", "");
                if (Enum.TryParse(enumName, true, out BuiltInCategory bic) &&
                    Category.GetCategory(doc, bic) != null)
                    categoryId = new ElementId(bic);
            }

            // 3. Localized names as shown in this Revit UI language.
            if (categoryId == null)
            {
                foreach (Category c in doc.Settings.Categories)
                {
                    if (c != null && string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))
                    {
                        categoryId = c.Id;
                        break;
                    }
                }
            }

            if (categoryId == null)
            {
                error = $"Unknown category '{n}'. Use an English name such as " +
                        string.Join(", ", _categoryBicMap.Keys.Take(12)) + ", or a BuiltInCategory like OST_Walls.";
                return false;
            }

            label = Category.GetCategory(doc, categoryId)?.Name ?? n;
            return true;
        }

        private bool TryCollect(Document doc, ElementId categoryId, string scope, string levelName,
            out List<Element> elements, out string scopeLabel, out string error)
        {
            elements = new List<Element>();
            error = null;
            string s = (scope ?? "model").Trim().ToLowerInvariant();

            FilteredElementCollector collector;
            switch (s)
            {
                case "active_view":
                case "view":
                    if (doc.ActiveView == null)
                    {
                        scopeLabel = "active view";
                        error = "There is no active view.";
                        return false;
                    }
                    collector = new FilteredElementCollector(doc, doc.ActiveView.Id);
                    scopeLabel = $"active view '{doc.ActiveView.Name}'";
                    break;

                case "selection":
                    var selected = GetUIDocument()?.Selection?.GetElementIds();
                    if (selected == null || selected.Count == 0)
                    {
                        scopeLabel = "selection";
                        error = "No elements are selected in Revit.";
                        return false;
                    }
                    collector = new FilteredElementCollector(doc, selected);
                    scopeLabel = $"current selection ({selected.Count} selected)";
                    break;

                default:
                    collector = new FilteredElementCollector(doc);
                    scopeLabel = "whole model";
                    break;
            }

            using (collector)
            {
                collector.OfCategoryId(categoryId).WhereElementIsNotElementType();

                if (!string.IsNullOrWhiteSpace(levelName))
                {
                    var level = new FilteredElementCollector(doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .FirstOrDefault(l => string.Equals(l.Name, levelName.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (level == null)
                    {
                        var names = new FilteredElementCollector(doc).OfClass(typeof(Level))
                            .Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name).Take(40);
                        error = $"Level '{levelName}' not found. Levels in this model: {string.Join(", ", names)}";
                        return false;
                    }
                    // Element.LevelId = the element's base / reference level.
                    collector.WherePasses(new ElementLevelFilter(level.Id));
                    scopeLabel += $", level '{level.Name}'";
                }

                elements = collector.ToElements().ToList();
            }
            return true;
        }

        private static string TypeLabel(Document doc, Element e)
        {
            try
            {
                var typeId = e.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId &&
                    doc.GetElement(typeId) is ElementType type)
                {
                    return string.IsNullOrEmpty(type.FamilyName) ? type.Name : $"{type.FamilyName}: {type.Name}";
                }
                return e.Name ?? "(unnamed)";
            }
            catch
            {
                return "(unknown type)";
            }
        }

        private static string LevelLabel(Document doc, Element e)
        {
            try
            {
                var id = e.LevelId;
                if (id == null || id == ElementId.InvalidElementId) return "-";
                return (doc.GetElement(id) as Level)?.Name ?? "-";
            }
            catch
            {
                return "-";
            }
        }

        /// <summary>Instance parameter first, then the type's; values in display units.</summary>
        private static string ReadParameterForDisplay(Document doc, Element e, string name)
        {
            try
            {
                Parameter p = e.LookupParameter(name);
                if (p == null)
                {
                    var typeId = e.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                        p = doc.GetElement(typeId)?.LookupParameter(name);
                }
                if (p == null) return $"{name}=(no such parameter)";
                if (!p.HasValue) return $"{name}=(empty)";

                string value;
                switch (p.StorageType)
                {
                    case StorageType.String:
                        value = p.AsString() ?? "";
                        break;
                    case StorageType.ElementId:
                        var refId = p.AsElementId();
                        value = (refId != null && refId != ElementId.InvalidElementId ? doc.GetElement(refId)?.Name : null)
                                ?? p.AsValueString() ?? refId?.ToString() ?? "";
                        break;
                    default:
                        // Doubles/integers: project-unit formatted text ("3500 mm", "Yes").
                        value = p.AsValueString() ?? (p.StorageType == StorageType.Double
                            ? p.AsDouble().ToString("0.###")
                            : p.AsInteger().ToString());
                        break;
                }
                if (value.Length > MaxParamValueChars) value = value.Substring(0, MaxParamValueChars) + "…";
                return $"{name}={value}";
            }
            catch
            {
                return $"{name}=(error)";
            }
        }
    }
}
