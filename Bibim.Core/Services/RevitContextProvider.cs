// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bibim.Core
{
    /// <summary>
    /// @Context system — provides real-time Revit model information to LLM.
    /// Design doc §3.6.
    /// 
    /// Supported contexts:
    ///   @view       — Active view info (name, type, scale, template)
    ///   @selection  — Currently selected elements (category, type, parameters)
    ///   @family     — Family/category info (types, instances, parameters)
    ///   @parameters — Parameter list for a category
    ///   @levels     — Project levels
    ///   @worksets   — Workset list
    ///   @phases     — Phase list
    /// 
    /// IMPORTANT: All methods must be called from the Revit main thread
    /// (via ExternalEvent or during IExternalCommand.Execute).
    /// </summary>
    public class RevitContextProvider
    {
        private UIApplication _uiApp;

        // English category name → BuiltInCategory (localization-independent lookup for get_element_parameters)
        private static readonly Dictionary<string, BuiltInCategory> _categoryBicMap =
            new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
            {
                { "Structural Columns",      BuiltInCategory.OST_StructuralColumns },
                { "Structural Framing",      BuiltInCategory.OST_StructuralFraming },
                { "Structural Foundations",  BuiltInCategory.OST_StructuralFoundation },
                { "Structural Stiffeners",   BuiltInCategory.OST_StructuralStiffener },
                { "Walls",                   BuiltInCategory.OST_Walls },
                { "Floors",                  BuiltInCategory.OST_Floors },
                { "Ceilings",                BuiltInCategory.OST_Ceilings },
                { "Roofs",                   BuiltInCategory.OST_Roofs },
                { "Doors",                   BuiltInCategory.OST_Doors },
                { "Windows",                 BuiltInCategory.OST_Windows },
                { "Rooms",                   BuiltInCategory.OST_Rooms },
                { "Areas",                   BuiltInCategory.OST_Areas },
                { "Grids",                   BuiltInCategory.OST_Grids },
                { "Levels",                  BuiltInCategory.OST_Levels },
                { "Columns",                 BuiltInCategory.OST_Columns },
                { "Beams",                   BuiltInCategory.OST_StructuralFraming },
                { "Stairs",                  BuiltInCategory.OST_Stairs },
                { "Railings",                BuiltInCategory.OST_StairsRailing },
                { "Ramps",                   BuiltInCategory.OST_Ramps },
                { "Curtain Panels",          BuiltInCategory.OST_CurtainWallPanels },
                { "Curtain Wall Mullions",   BuiltInCategory.OST_CurtainWallMullions },
                { "Generic Models",          BuiltInCategory.OST_GenericModel },
                { "Specialty Equipment",     BuiltInCategory.OST_SpecialityEquipment },
                { "Furniture",               BuiltInCategory.OST_Furniture },
                { "Casework",                BuiltInCategory.OST_Casework },
                { "Planting",                BuiltInCategory.OST_Planting },
                { "Site",                    BuiltInCategory.OST_Site },
                { "Topography",              BuiltInCategory.OST_Topography },
                { "Parking",                 BuiltInCategory.OST_Parking },
                { "Lighting Fixtures",       BuiltInCategory.OST_LightingFixtures },
                { "Electrical Fixtures",     BuiltInCategory.OST_ElectricalFixtures },
                { "Electrical Equipment",    BuiltInCategory.OST_ElectricalEquipment },
                { "Mechanical Equipment",    BuiltInCategory.OST_MechanicalEquipment },
                { "Plumbing Fixtures",       BuiltInCategory.OST_PlumbingFixtures },
                { "Pipe Accessories",        BuiltInCategory.OST_PipeAccessory },
                { "Duct Accessories",        BuiltInCategory.OST_DuctAccessory },
                { "Pipes",                   BuiltInCategory.OST_PipeCurves },
                { "Ducts",                   BuiltInCategory.OST_DuctCurves },
                { "Conduits",                BuiltInCategory.OST_Conduit },
                { "Cable Trays",             BuiltInCategory.OST_CableTray },
                { "Flex Pipes",              BuiltInCategory.OST_FlexPipeCurves },
                { "Flex Ducts",              BuiltInCategory.OST_FlexDuctCurves },
                { "MEP Spaces",              BuiltInCategory.OST_MEPSpaces },
                { "Air Terminals",           BuiltInCategory.OST_DuctTerminal },
                { "Sprinklers",              BuiltInCategory.OST_Sprinklers },
                { "Fire Alarm Devices",      BuiltInCategory.OST_FireAlarmDevices },
                { "Communication Devices",   BuiltInCategory.OST_CommunicationDevices },
                { "Security Devices",        BuiltInCategory.OST_SecurityDevices },
                { "Sheets",                  BuiltInCategory.OST_Sheets },
            };

        /// <summary>
        /// Update the UIApplication reference. Called from BibimExecutionHandler
        /// on the Revit main thread.
        /// </summary>
        public void SetApplication(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        private Document GetDocument()
        {
            return _uiApp?.ActiveUIDocument?.Document;
        }

        private UIDocument GetUIDocument()
        {
            return _uiApp?.ActiveUIDocument;
        }

        /// <summary>
        /// @view — Get active view information.
        /// </summary>
        public ViewContextInfo GetCurrentView()
        {
            var doc = GetDocument();
            if (doc == null) return new ViewContextInfo { Error = "No active document." };

            var view = doc.ActiveView;
            if (view == null) return new ViewContextInfo { Error = "No active view." };

            var info = new ViewContextInfo
            {
                ViewName = view.Name,
                ViewType = view.ViewType.ToString(),
                Scale = view.Scale,
                DetailLevel = view.DetailLevel.ToString(),
                ViewTemplateId = view.ViewTemplateId?.ToString()
            };

            // Get visible categories
            try
            {
                var categories = doc.Settings.Categories;
                var visibleCats = new List<string>();
                foreach (Category cat in categories)
                {
                    if (cat.get_Visible(view))
                        visibleCats.Add(cat.Name);
                }
                info.VisibleCategories = visibleCats;
            }
            catch (Exception ex)
            {
                Logger.Log("RevitContextProvider", $"Could not get visible categories: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// @selection — Get currently selected elements info.
        /// </summary>
        public SelectionContextInfo GetSelectedElements()
        {
            var uidoc = GetUIDocument();
            var doc = GetDocument();
            if (uidoc == null || doc == null)
                return new SelectionContextInfo { Error = "No active document." };

            var selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds == null || selectedIds.Count == 0)
                return new SelectionContextInfo { Error = "No elements selected.", Elements = new List<ElementSummary>() };

            var info = new SelectionContextInfo
            {
                TotalCount = selectedIds.Count,
                Elements = new List<ElementSummary>()
            };

            foreach (var id in selectedIds.Take(50)) // Cap at 50 to avoid token bloat
            {
                var elem = doc.GetElement(id);
                if (elem == null) continue;

                var summary = new ElementSummary
                {
                    ElementId = id.ToString(),
                    Category = elem.Category?.Name ?? "Unknown",
                    TypeName = elem.Name,
                    FamilyName = (elem is FamilyInstance fi) ? fi.Symbol?.Family?.Name : null
                };

                // Get key parameters (first 10)
                try
                {
                    var paramList = new List<ParameterSummary>();
                    foreach (Parameter param in elem.Parameters)
                    {
                        if (paramList.Count >= 10) break;
                        if (!param.HasValue) continue;

                        paramList.Add(new ParameterSummary
                        {
                            Name = param.Definition.Name,
                            StorageType = param.StorageType.ToString(),
                            Value = GetParameterValueString(param),
                            IsReadOnly = param.IsReadOnly
                        });
                    }
                    summary.Parameters = paramList;
                }
                catch { /* Skip parameter errors */ }

                // Get location info
                try
                {
                    if (elem.Location is LocationPoint lp)
                        summary.Location = $"Point({lp.Point.X:F2}, {lp.Point.Y:F2}, {lp.Point.Z:F2})";
                    else if (elem.Location is LocationCurve lc)
                        summary.Location = $"Curve(Length={lc.Curve.Length:F2})";
                }
                catch { /* Skip location errors */ }

                info.Elements.Add(summary);
            }

            return info;
        }

        /// <summary>
        /// @family — Get family/category information.
        /// </summary>
        public FamilyContextInfo GetFamilyInfo(string categoryName)
        {
            var doc = GetDocument();
            if (doc == null) return new FamilyContextInfo { Error = "No active document." };

            var info = new FamilyContextInfo
            {
                CategoryFilter = categoryName,
                Families = new List<FamilySummary>()
            };

            try
            {
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family));

                foreach (Family family in collector)
                {
                    if (!string.IsNullOrEmpty(categoryName) &&
                        family.FamilyCategory != null &&
                        !family.FamilyCategory.Name.Contains(categoryName))
                        continue;

                    var familySummary = new FamilySummary
                    {
                        FamilyName = family.Name,
                        CategoryName = family.FamilyCategory?.Name ?? "Unknown",
                        TypeNames = new List<string>()
                    };

                    // Get family symbol (type) names
                    foreach (var symbolId in family.GetFamilySymbolIds())
                    {
                        var symbol = doc.GetElement(symbolId) as FamilySymbol;
                        if (symbol != null)
                            familySummary.TypeNames.Add(symbol.Name);
                    }

                    info.Families.Add(familySummary);

                    if (info.Families.Count >= 100) break; // Cap
                }

                collector.Dispose();
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
                Logger.LogError("RevitContextProvider.GetFamilyInfo", ex);
            }

            return info;
        }

        /// <summary>
        /// @parameters — Get parameter list for a category.
        /// Samples one element of the category to extract parameter definitions.
        /// </summary>
        public ParameterListInfo GetParameterList(string categoryName)
        {
            var doc = GetDocument();
            if (doc == null) return new ParameterListInfo { Error = "No active document." };

            var info = new ParameterListInfo
            {
                CategoryName = categoryName,
                InstanceParameters = new List<ParameterSummary>(),
                TypeParameters = new List<ParameterSummary>()
            };

            try
            {
                Element sampleElement = null;

                // First try: BuiltInCategory lookup — localization-independent, fast.
                // Handles the case where Revit is Korean/Japanese-localized but Claude sends English category names.
                if (_categoryBicMap.TryGetValue(categoryName, out BuiltInCategory bic))
                {
                    sampleElement = new FilteredElementCollector(doc)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType()
                        .FirstElement();
                }

                // Second try: localized string name matching (custom categories or unmapped ones)
                var availableCategories = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                if (sampleElement == null)
                {
                    var collector = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType();

                    foreach (var elem in collector)
                    {
                        if (elem.Category == null) continue;
                        availableCategories.Add(elem.Category.Name);
                        if (sampleElement == null &&
                            elem.Category.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
                        {
                            sampleElement = elem;
                            // Don't break — continue to collect available categories for error message
                        }
                    }
                    collector.Dispose();
                }

                if (sampleElement == null)
                {
                    var catList = availableCategories.Count > 0
                        ? string.Join(", ", availableCategories.Take(30))
                        : "(no elements in model)";
                    info.Error = $"No elements found in category '{categoryName}'. Available categories: {catList}";
                    return info;
                }

                // Instance parameters
                foreach (Parameter param in sampleElement.Parameters)
                {
                    info.InstanceParameters.Add(new ParameterSummary
                    {
                        Name = param.Definition.Name,
                        StorageType = param.StorageType.ToString(),
                        Value = param.HasValue ? GetParameterValueString(param) : "(empty)",
                        IsReadOnly = param.IsReadOnly
                    });
                }

                // Type parameters
                var typeId = sampleElement.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                {
                    var typeElem = doc.GetElement(typeId);
                    if (typeElem != null)
                    {
                        foreach (Parameter param in typeElem.Parameters)
                        {
                            info.TypeParameters.Add(new ParameterSummary
                            {
                                Name = param.Definition.Name,
                                StorageType = param.StorageType.ToString(),
                                Value = param.HasValue ? GetParameterValueString(param) : "(empty)",
                                IsReadOnly = param.IsReadOnly
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
                Logger.LogError("RevitContextProvider.GetParameterList", ex);
            }

            return info;
        }

        /// <summary>
        /// @levels — Get project levels.
        /// </summary>
        public LevelListInfo GetLevels()
        {
            var doc = GetDocument();
            if (doc == null) return new LevelListInfo { Error = "No active document." };

            var info = new LevelListInfo { Levels = new List<LevelSummary>() };

            try
            {
                var collector = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level));

                foreach (Level level in collector)
                {
                    info.Levels.Add(new LevelSummary
                    {
                        Name = level.Name,
                        Elevation = level.Elevation,
                        ElementId = level.Id.ToString()
                    });
                }

                collector.Dispose();
                info.Levels = info.Levels.OrderBy(l => l.Elevation).ToList();
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        /// <summary>
        /// @worksets — Get workset list (worksharing projects only).
        /// </summary>
        public WorksetListInfo GetWorksets()
        {
            var doc = GetDocument();
            if (doc == null) return new WorksetListInfo { Error = "No active document." };

            var info = new WorksetListInfo { Worksets = new List<WorksetSummary>() };

            if (!doc.IsWorkshared)
            {
                info.Error = "Project is not workshared.";
                return info;
            }

            try
            {
                var worksets = new FilteredWorksetCollector(doc)
                    .OfKind(WorksetKind.UserWorkset);

                foreach (var ws in worksets)
                {
                    info.Worksets.Add(new WorksetSummary
                    {
                        Name = ws.Name,
                        IsOpen = ws.IsOpen,
                        Owner = ws.Owner
                    });
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        /// <summary>
        /// @phases — Get project phases.
        /// </summary>
        public PhaseListInfo GetPhases()
        {
            var doc = GetDocument();
            if (doc == null) return new PhaseListInfo { Error = "No active document." };

            var info = new PhaseListInfo { Phases = new List<PhaseSummary>() };

            try
            {
                var phases = doc.Phases;
                foreach (Phase phase in phases)
                {
                    info.Phases.Add(new PhaseSummary
                    {
                        Name = phase.Name,
                        ElementId = phase.Id.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }

            return info;
        }

        /// <summary>
        /// Resolve a @context tag to its data, serialized as a string for LLM consumption.
        /// </summary>
        public string ResolveContextTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return string.Empty;

            tag = tag.Trim().ToLowerInvariant();
            if (tag.StartsWith("@")) tag = tag.Substring(1);

            var sb = new StringBuilder();

            if (tag == "view")
            {
                var view = GetCurrentView();
                if (view.Error != null) return $"[Context Error] {view.Error}";
                sb.AppendLine($"[Active View] {view.ViewName}");
                sb.AppendLine($"  Type: {view.ViewType}, Scale: 1:{view.Scale}, Detail: {view.DetailLevel}");
                if (view.VisibleCategories != null && view.VisibleCategories.Count > 0)
                    sb.AppendLine($"  Visible categories: {string.Join(", ", view.VisibleCategories.Take(20))}");
            }
            else if (tag == "selection")
            {
                var sel = GetSelectedElements();
                if (sel.Error != null && (sel.Elements == null || sel.Elements.Count == 0))
                    return $"[Context Error] {sel.Error}";
                sb.AppendLine($"[Selected Elements] {sel.TotalCount} element(s)");
                foreach (var elem in sel.Elements)
                {
                    sb.AppendLine($"  - {elem.Category} | {elem.TypeName} (Id: {elem.ElementId})");
                    if (elem.FamilyName != null) sb.AppendLine($"    Family: {elem.FamilyName}");
                    if (elem.Location != null) sb.AppendLine($"    Location: {elem.Location}");
                    if (elem.Parameters != null)
                    {
                        foreach (var p in elem.Parameters.Take(5))
                            sb.AppendLine($"    Param: {p.Name} = {p.Value} ({p.StorageType})");
                    }
                }
            }
            else if (tag.StartsWith("family:") || tag.StartsWith("family"))
            {
                string catName = tag.Contains(":") ? tag.Substring(tag.IndexOf(':') + 1).Trim() : "";
                var fam = GetFamilyInfo(catName);
                if (fam.Error != null) return $"[Context Error] {fam.Error}";
                sb.AppendLine($"[Families] Filter: '{catName}', Found: {fam.Families.Count}");
                foreach (var f in fam.Families.Take(30))
                {
                    sb.AppendLine($"  - {f.FamilyName} ({f.CategoryName})");
                    if (f.TypeNames.Count > 0)
                        sb.AppendLine($"    Types: {string.Join(", ", f.TypeNames.Take(10))}");
                }
            }
            else if (tag.StartsWith("parameters:") || tag.StartsWith("parameters"))
            {
                string catName = tag.Contains(":") ? tag.Substring(tag.IndexOf(':') + 1).Trim() : "";
                var plist = GetParameterList(catName);
                if (plist.Error != null) return $"[Context Error] {plist.Error}";
                sb.AppendLine($"[Parameters for '{catName}']");
                sb.AppendLine("  Instance Parameters:");
                foreach (var p in plist.InstanceParameters.Take(30))
                    sb.AppendLine($"    {p.Name} ({p.StorageType}) = {p.Value} {(p.IsReadOnly ? "[RO]" : "")}");
                sb.AppendLine("  Type Parameters:");
                foreach (var p in plist.TypeParameters.Take(30))
                    sb.AppendLine($"    {p.Name} ({p.StorageType}) = {p.Value} {(p.IsReadOnly ? "[RO]" : "")}");
            }
            else if (tag == "levels")
            {
                var levels = GetLevels();
                if (levels.Error != null) return $"[Context Error] {levels.Error}";
                sb.AppendLine($"[Project Levels] {levels.Levels.Count} level(s)");
                foreach (var l in levels.Levels)
                    sb.AppendLine($"  - {l.Name} (Elevation: {l.Elevation:F2}, Id: {l.ElementId})");
            }
            else if (tag == "worksets")
            {
                var ws = GetWorksets();
                if (ws.Error != null) return $"[Context Error] {ws.Error}";
                sb.AppendLine($"[Worksets] {ws.Worksets.Count} workset(s)");
                foreach (var w in ws.Worksets)
                    sb.AppendLine($"  - {w.Name} (Open: {w.IsOpen}, Owner: {w.Owner})");
            }
            else if (tag == "phases")
            {
                var ph = GetPhases();
                if (ph.Error != null) return $"[Context Error] {ph.Error}";
                sb.AppendLine($"[Phases] {ph.Phases.Count} phase(s)");
                foreach (var p in ph.Phases)
                    sb.AppendLine($"  - {p.Name} (Id: {p.ElementId})");
            }
            else
            {
                return $"[Context Error] Unknown context tag: @{tag}";
            }

            return sb.ToString().TrimEnd();
        }

        private string GetParameterValueString(Parameter param)
        {
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String: return param.AsString() ?? "(null)";
                    case StorageType.Integer: return param.AsInteger().ToString();
                    case StorageType.Double: return param.AsDouble().ToString("F4");
                    case StorageType.ElementId: return param.AsElementId()?.ToString() ?? "(null)";
                    default: return param.AsValueString() ?? "(unknown)";
                }
            }
            catch
            {
                return "(error)";
            }
        }
    }

    #region Context Models

    public class ViewContextInfo
    {
        public string Error { get; set; }
        public string ViewName { get; set; }
        public string ViewType { get; set; }
        public int Scale { get; set; }
        public string DetailLevel { get; set; }
        public string ViewTemplateId { get; set; }
        public List<string> VisibleCategories { get; set; }
    }

    public class SelectionContextInfo
    {
        public string Error { get; set; }
        public int TotalCount { get; set; }
        public List<ElementSummary> Elements { get; set; }
    }

    public class ElementSummary
    {
        public string ElementId { get; set; }
        public string Category { get; set; }
        public string TypeName { get; set; }
        public string FamilyName { get; set; }
        public string Location { get; set; }
        public List<ParameterSummary> Parameters { get; set; }
    }

    public class ParameterSummary
    {
        public string Name { get; set; }
        public string StorageType { get; set; }
        public string Value { get; set; }
        public bool IsReadOnly { get; set; }
    }

    public class FamilyContextInfo
    {
        public string Error { get; set; }
        public string CategoryFilter { get; set; }
        public List<FamilySummary> Families { get; set; }
    }

    public class FamilySummary
    {
        public string FamilyName { get; set; }
        public string CategoryName { get; set; }
        public List<string> TypeNames { get; set; }
    }

    public class ParameterListInfo
    {
        public string Error { get; set; }
        public string CategoryName { get; set; }
        public List<ParameterSummary> InstanceParameters { get; set; }
        public List<ParameterSummary> TypeParameters { get; set; }
    }

    public class LevelListInfo
    {
        public string Error { get; set; }
        public List<LevelSummary> Levels { get; set; }
    }

    public class LevelSummary
    {
        public string Name { get; set; }
        public double Elevation { get; set; }
        public string ElementId { get; set; }
    }

    public class WorksetListInfo
    {
        public string Error { get; set; }
        public List<WorksetSummary> Worksets { get; set; }
    }

    public class WorksetSummary
    {
        public string Name { get; set; }
        public bool IsOpen { get; set; }
        public string Owner { get; set; }
    }

    public class PhaseListInfo
    {
        public string Error { get; set; }
        public List<PhaseSummary> Phases { get; set; }
    }

    public class PhaseSummary
    {
        public string Name { get; set; }
        public string ElementId { get; set; }
    }

    #endregion
}
