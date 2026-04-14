// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bibim.Core
{
    /// <summary>
    /// API Inspector — extracts and categorizes Revit API usage from generated code.
    /// Design doc §3.2.
    /// 
    /// Uses Roslyn Semantic Model to:
    ///   - Extract all Revit API calls (GetTypeInfo, GetSymbolInfo)
    ///   - Categorize as ✅ safe / ⚠️ version-specific / ❌ deprecated
    ///   - Combine with Dry Run results for impact summary
    /// </summary>
    public class ApiInspectorService
    {
        private readonly RevitApiXmlIndex _xmlIndex;
        private readonly RoslynAnalyzerService _analyzer;
        private readonly int _majorVersion;

        // Known version-specific APIs (behavior changed across Revit versions)
        private static readonly Dictionary<string, VersionSpecificInfo> VersionSpecificApis =
            new Dictionary<string, VersionSpecificInfo>(StringComparer.OrdinalIgnoreCase)
        {
            { "GetGeometryObjectFromReference", new VersionSpecificInfo { Note = "Behavior changed in Revit 2024+", AffectsFrom = 2024 } },
            { "IntegerValue", new VersionSpecificInfo { Note = "Removed in Revit 2024+, use Value", AffectsFrom = 2024 } },
            { "LevelId", new VersionSpecificInfo { Note = "Removed in Revit 2024+, use get_Parameter", AffectsFrom = 2024 } },
            { "AsInteger", new VersionSpecificInfo { Note = "Changed in Revit 2024+", AffectsFrom = 2024 } },
            { "NewFloor", new VersionSpecificInfo { Note = "Signature changed in Revit 2022+", AffectsFrom = 2022 } },
            { "NewRoof", new VersionSpecificInfo { Note = "Signature changed in Revit 2022+", AffectsFrom = 2022 } }
        };

        public ApiInspectorService(RoslynAnalyzerService analyzer = null, string revitVersion = null)
        {
            try { _xmlIndex = RevitApiXmlProvider.GetOrLoad(); }
            catch { _xmlIndex = null; }
            _analyzer = analyzer;

            // Resolve major version for version-aware categorization
            string ver = revitVersion ?? "2025";
            if (ver.Contains("."))
                ver = ver.Substring(0, ver.IndexOf('.'));
            int.TryParse(ver, out _majorVersion);
        }

        /// <summary>
        /// Inspect generated C# code and produce an API usage report.
        /// </summary>
        public ApiInspectionReport Inspect(string sourceCode, ExecutionResult dryRunResult = null)
        {
            var report = new ApiInspectionReport();

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode,
                    new CSharpParseOptions(LanguageVersion.Latest));
                var root = tree.GetRoot();

                // Extract all Revit API usages from syntax
                var apiUsages = ExtractRevitApiUsages(root);
                report.ApiUsages = apiUsages;

                // Categorize each usage
                foreach (var usage in apiUsages)
                {
                    CategorizeApiUsage(usage);
                }

                // Run BIBIM analyzers if available
                if (_analyzer != null)
                {
                    var analyzerReport = _analyzer.Analyze(sourceCode);
                    report.AnalyzerDiagnostics = analyzerReport.Diagnostics;
                }

                // Attach Dry Run results if available
                if (dryRunResult != null)
                {
                    report.DryRunSummary = new DryRunSummary
                    {
                        Success = dryRunResult.Success,
                        AffectedElementCount = dryRunResult.AffectedElementCount,
                        ErrorMessage = dryRunResult.ErrorMessage,
                        MemoryDeltaBytes = dryRunResult.MemoryAfter - dryRunResult.MemoryBefore
                    };
                }

                // Compute summary counts
                report.SafeCount = apiUsages.Count(u => u.Status == ApiStatus.Safe);
                report.VersionSpecificCount = apiUsages.Count(u => u.Status == ApiStatus.VersionSpecific);
                report.DeprecatedCount = apiUsages.Count(u => u.Status == ApiStatus.Deprecated);
            }
            catch (Exception ex)
            {
                report.Error = ex.Message;
                Logger.LogError("ApiInspectorService", ex);
            }

            return report;
        }

        /// <summary>
        /// Extract Revit API usages from the syntax tree.
        /// Looks for member access expressions that reference Revit namespaces.
        /// </summary>
        private List<ApiUsageEntry> ExtractRevitApiUsages(SyntaxNode root)
        {
            var usages = new List<ApiUsageEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Collect member access expressions
            var memberAccesses = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>();

            foreach (var access in memberAccesses)
            {
                string memberName = access.Name.Identifier.Text;
                string receiverText = access.Expression.ToString();

                // Filter to likely Revit API calls
                if (!IsLikelyRevitApi(receiverText, memberName)) continue;

                string key = receiverText + "." + memberName;
                if (seen.Contains(key)) continue;
                seen.Add(key);

                var lineSpan = access.GetLocation().GetLineSpan();
                usages.Add(new ApiUsageEntry
                {
                    ApiName = memberName,
                    FullExpression = key,
                    ReceiverType = GuessReceiverType(receiverText),
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Status = ApiStatus.Safe // Default, will be recategorized
                });
            }

            // Collect object creation expressions (new FilteredElementCollector, new Transaction, etc.)
            var creations = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>();

            foreach (var creation in creations)
            {
                string typeName = creation.Type.ToString();
                if (!IsRevitTypeName(typeName)) continue;

                string key = "new " + typeName;
                if (seen.Contains(key)) continue;
                seen.Add(key);

                var lineSpan = creation.GetLocation().GetLineSpan();
                usages.Add(new ApiUsageEntry
                {
                    ApiName = typeName,
                    FullExpression = key,
                    ReceiverType = typeName,
                    Line = lineSpan.StartLinePosition.Line + 1,
                    Status = ApiStatus.Safe,
                    IsConstructor = true
                });
            }

            return usages;
        }

        /// <summary>
        /// Categorize an API usage as safe, version-specific, or deprecated.
        /// </summary>
        private void CategorizeApiUsage(ApiUsageEntry usage)
        {
            // Check version-specific APIs — only flag if current version is affected
            if (VersionSpecificApis.TryGetValue(usage.ApiName, out VersionSpecificInfo versionInfo))
            {
                if (_majorVersion == 0 || _majorVersion >= versionInfo.AffectsFrom)
                {
                    usage.Status = ApiStatus.VersionSpecific;
                    usage.Note = versionInfo.Note;
                    return;
                }
                // For older Revit versions, this API is still safe
            }

            // Check XML deprecated index
            if (_xmlIndex != null && _xmlIndex.Status == "ok")
            {
                // Check type-level deprecation
                if (_xmlIndex.DeprecatedTypes.Contains(usage.ReceiverType))
                {
                    usage.Status = ApiStatus.Deprecated;
                    _xmlIndex.TypeSummaries.TryGetValue(usage.ReceiverType, out string summary);
                    usage.Note = summary ?? "Deprecated type";
                    return;
                }

                // Check member-level deprecation
                string memberKey = usage.ReceiverType + "." + usage.ApiName;
                if (_xmlIndex.DeprecatedMembers.Contains(memberKey))
                {
                    usage.Status = ApiStatus.Deprecated;
                    _xmlIndex.MemberSummaries.TryGetValue(memberKey, out string summary);
                    usage.Note = summary ?? "Deprecated member";
                    return;
                }
            }

            // Default: safe
            usage.Status = ApiStatus.Safe;
        }

        /// <summary>
        /// Format the inspection report as a human-readable string for UI display.
        /// </summary>
        public string FormatReport(ApiInspectionReport report)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("📋 API Inspector Report");
            sb.AppendLine(new string('─', 40));

            // API usage list
            if (report.ApiUsages != null && report.ApiUsages.Count > 0)
            {
                sb.AppendLine($"Used Revit APIs ({report.ApiUsages.Count}):");
                foreach (var usage in report.ApiUsages)
                {
                    string icon = usage.Status == ApiStatus.Safe ? "✅" :
                                  usage.Status == ApiStatus.VersionSpecific ? "⚠️" : "❌";
                    sb.AppendLine($"  {icon} {usage.FullExpression}");
                    if (!string.IsNullOrEmpty(usage.Note))
                        sb.AppendLine($"     └ {usage.Note}");
                }
                sb.AppendLine();
            }

            // Summary counts
            sb.AppendLine($"Summary: ✅ {report.SafeCount} safe, ⚠️ {report.VersionSpecificCount} version-specific, ❌ {report.DeprecatedCount} deprecated");

            // Analyzer diagnostics
            if (report.AnalyzerDiagnostics != null && report.AnalyzerDiagnostics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"BIBIM Analyzer ({report.AnalyzerDiagnostics.Count} issue(s)):");
                foreach (var diag in report.AnalyzerDiagnostics)
                    sb.AppendLine($"  [{diag.Id}] L{diag.Line}: {diag.Message}");
            }

            // Dry Run results
            if (report.DryRunSummary != null)
            {
                sb.AppendLine();
                sb.AppendLine("📊 Dry Run Results:");
                if (report.DryRunSummary.Success)
                {
                    sb.AppendLine($"  Affected elements: {report.DryRunSummary.AffectedElementCount}");
                    if (report.DryRunSummary.MemoryDeltaBytes > 0)
                        sb.AppendLine($"  Memory delta: {report.DryRunSummary.MemoryDeltaBytes / 1024}KB");
                }
                else
                {
                    sb.AppendLine($"  ❌ Failed: {report.DryRunSummary.ErrorMessage}");
                }
            }

            return sb.ToString();
        }

        #region Helpers

        private bool IsLikelyRevitApi(string receiverText, string memberName)
        {
            // Heuristic: check if receiver looks like a Revit object
            string[] revitIndicators = {
                "doc", "document", "uidoc", "uiApp", "app",
                "collector", "FilteredElementCollector",
                "Transaction", "tx", "transaction",
                "element", "elem", "wall", "floor", "roof", "door", "window",
                "family", "symbol", "param", "parameter",
                "view", "level", "workset", "phase",
                "XYZ", "Line", "Arc", "Curve", "CurveLoop",
                "ElementId", "BuiltInParameter", "BuiltInCategory"
            };

            string lower = receiverText.ToLowerInvariant();
            return revitIndicators.Any(ind => lower.Contains(ind.ToLowerInvariant()));
        }

        private bool IsRevitTypeName(string typeName)
        {
            string[] revitTypes = {
                "FilteredElementCollector", "Transaction", "SubTransaction",
                "TransactionGroup", "XYZ", "Line", "Arc", "CurveLoop",
                "ElementId", "UV", "BoundingBoxXYZ", "Transform",
                "Plane", "SketchPlane", "Reference", "Options"
            };

            return revitTypes.Any(t => typeName.Contains(t));
        }

        private string GuessReceiverType(string receiverText)
        {
            // Simple heuristic to guess the Revit type from variable name
            var parts = receiverText.Split('.');
            string last = parts[parts.Length - 1];

            // Direct type references
            if (char.IsUpper(last[0]) && last.Length > 2)
                return last;

            // Common variable name patterns
            var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "doc", "Document" }, { "document", "Document" },
                { "uidoc", "UIDocument" }, { "uiApp", "UIApplication" },
                { "app", "Application" }, { "tx", "Transaction" },
                { "collector", "FilteredElementCollector" },
                { "wall", "Wall" }, { "floor", "Floor" }, { "elem", "Element" },
                { "element", "Element" }, { "view", "View" }, { "level", "Level" }
            };

            if (nameMap.TryGetValue(last, out string mapped))
                return mapped;

            return last;
        }

        #endregion
    }

    #region Models

    public enum ApiStatus
    {
        Safe,
        VersionSpecific,
        Deprecated
    }

    public class VersionSpecificInfo
    {
        public string Note { get; set; }
        public int AffectsFrom { get; set; }
    }

    public class ApiUsageEntry
    {
        public string ApiName { get; set; }
        public string FullExpression { get; set; }
        public string ReceiverType { get; set; }
        public int Line { get; set; }
        public ApiStatus Status { get; set; }
        public string Note { get; set; }
        public bool IsConstructor { get; set; }
    }

    public class DryRunSummary
    {
        public bool Success { get; set; }
        public int AffectedElementCount { get; set; }
        public string ErrorMessage { get; set; }
        public long MemoryDeltaBytes { get; set; }
    }

    public class ApiInspectionReport
    {
        public string Error { get; set; }
        public List<ApiUsageEntry> ApiUsages { get; set; } = new List<ApiUsageEntry>();
        public List<AnalyzerDiagnostic> AnalyzerDiagnostics { get; set; } = new List<AnalyzerDiagnostic>();
        public DryRunSummary DryRunSummary { get; set; }
        public int SafeCount { get; set; }
        public int VersionSpecificCount { get; set; }
        public int DeprecatedCount { get; set; }
    }

    #endregion
}
