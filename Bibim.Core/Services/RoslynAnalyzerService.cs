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
    /// Roslyn-based static analyzer for Revit C# code.
    /// Design doc §2.4 — Ghost Object Defense + Custom Analyzers.
    /// 
    /// Analyzers:
    ///   BIBIM001: Transaction 없이 수정 API 호출 감지
    ///   BIBIM002: FilteredElementCollector 미해제 감지
    ///   BIBIM003: ElementType 필터 누락 (유령객체 방어)
    ///   BIBIM004: Revit 2024+ 제거된 API 사용 감지
    ///   BIBIM005: XYZ 연산 안전성 검증
    /// 
    /// CodeFixProvider: LLM 없이 단순 패턴 자동 수정.
    /// </summary>
    public class RoslynAnalyzerService
    {
        private readonly RevitApiXmlIndex _xmlIndex;
        private string _revitVersion;

        /// <summary>
        /// The target Revit version for version-aware analysis.
        /// Set via SetRevitVersion() or defaults to ConfigService.GetEffectiveRevitVersion().
        /// </summary>
        public string RevitVersion
        {
            get => _revitVersion ?? "2025";
            private set => _revitVersion = value;
        }

        /// <summary>
        /// Parse the major version number (e.g., "2025" → 2025, "2025.3" → 2025).
        /// </summary>
        private int MajorVersion
        {
            get
            {
                string ver = RevitVersion;
                if (ver != null && ver.Contains("."))
                    ver = ver.Substring(0, ver.IndexOf('.'));
                int.TryParse(ver, out int major);
                return major;
            }
        }

        /// <summary>
        /// Update the target Revit version for analysis rules.
        /// </summary>
        public void SetRevitVersion(string version)
        {
            _revitVersion = version;
        }

        // Revit modification APIs that require a Transaction
        private static readonly HashSet<string> ModificationApis = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Create", "NewFamilyInstance", "NewWall", "NewFloor", "NewRoof",
            "Delete", "Move", "RotateElement", "MirrorElement", "Copy",
            "set_Parameter", "Set", "SetValueString",
            "Start", "Commit", "RollBack",
            "SetElementOverrides", "SetCategoryOverrides"
        };

        // Revit 2024+ removed/changed APIs
        private static readonly Dictionary<string, string> DeprecatedApiReplacements =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "IntegerValue", "Value" },
            { "LevelId", "get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)" },
            { "AsInteger", "AsValueString" }
        };

        // Known dangerous XYZ operations
        private static readonly HashSet<string> DangerousXyzOps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Divide", "Normalize"
        };

        public RoslynAnalyzerService()
        {
            try { _xmlIndex = RevitApiXmlProvider.GetOrLoad(); }
            catch { _xmlIndex = null; }
        }

        /// <summary>
        /// Run all BIBIM analyzers on the given source code.
        /// Returns a list of diagnostics (warnings/errors).
        /// </summary>
        public AnalyzerReport Analyze(string sourceCode)
        {
            var report = new AnalyzerReport();

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceCode,
                    new CSharpParseOptions(LanguageVersion.Latest));
                var root = tree.GetRoot();

                report.Diagnostics.AddRange(CheckBIBIM001_TransactionRequired(root));
                report.Diagnostics.AddRange(CheckBIBIM002_CollectorDisposal(root));
                report.Diagnostics.AddRange(CheckBIBIM003_GhostObjectFilter(root));
                report.Diagnostics.AddRange(CheckBIBIM004_DeprecatedApi(root));
                report.Diagnostics.AddRange(CheckBIBIM005_XyzSafety(root));
            }
            catch (Exception ex)
            {
                Logger.LogError("RoslynAnalyzerService", ex);
                report.Diagnostics.Add(new AnalyzerDiagnostic
                {
                    Id = "BIBIM000",
                    Message = $"Analyzer internal error: {ex.Message}",
                    Severity = AnalyzerSeverity.Warning,
                    Line = 0
                });
            }

            return report;
        }

        /// <summary>
        /// Apply automatic code fixes that don't require LLM.
        /// Design doc §2.1 Step 5 — Code Fix Provider.
        /// Returns the fixed source code and a list of applied fixes.
        /// </summary>
        public CodeFixResult ApplyAutoFixes(string sourceCode)
        {
            var result = new CodeFixResult { OriginalCode = sourceCode };
            string code = sourceCode;
            int major = MajorVersion;

            // Only apply 2024+ fixes when targeting Revit 2024+
            bool apply2024Fixes = major == 0 || major >= 2024;

            if (apply2024Fixes)
            {
                // Fix 1: IntegerValue → Value
                if (code.Contains(".IntegerValue"))
                {
                    code = code.Replace(".IntegerValue", ".Value");
                    result.AppliedFixes.Add("BIBIM004-FIX: IntegerValue → Value");
                }

                // Fix 2: AsInteger() → AsValueString() (common Revit 2024+ change)
                if (code.Contains(".AsInteger()"))
                {
                    code = code.Replace(".AsInteger()", ".AsValueString()");
                    result.AppliedFixes.Add("BIBIM004-FIX: AsInteger() → AsValueString()");
                }

                // Fix 3: element.LevelId → element.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()
                if (code.Contains(".LevelId") && !code.Contains("get_Parameter"))
                {
                    code = System.Text.RegularExpressions.Regex.Replace(
                        code,
                        @"(\w+)\.LevelId\b",
                        "$1.get_Parameter(Autodesk.Revit.DB.BuiltInParameter.WALL_BASE_CONSTRAINT).AsElementId()");
                    result.AppliedFixes.Add("BIBIM004-FIX: .LevelId → get_Parameter(WALL_BASE_CONSTRAINT).AsElementId()");
                }

                // Fix 4: new CurveLoop(list) — flag only
                if (code.Contains("new CurveLoop(") && code.Contains("CurveLoop(") &&
                    !code.Contains("new CurveLoop()"))
                {
                    result.SuggestedFixes.Add(
                        "BIBIM004-SUGGEST: CurveLoop constructor with list parameter removed in Revit 2024+. " +
                        "Use 'var loop = new CurveLoop(); foreach(var c in curves) loop.Append(c);'");
                }
            }

            result.FixedCode = code;
            result.HasChanges = !string.Equals(sourceCode, code, StringComparison.Ordinal);
            return result;
        }

        #region BIBIM001: Transaction Required

        /// <summary>
        /// BIBIM001: Detect modification API calls outside a Transaction block.
        /// </summary>
        private List<AnalyzerDiagnostic> CheckBIBIM001_TransactionRequired(SyntaxNode root)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Find all invocation expressions
            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                string methodName = GetMethodName(invocation);
                if (string.IsNullOrEmpty(methodName)) continue;
                if (!ModificationApis.Contains(methodName)) continue;

                // Skip if it's Transaction.Start/Commit/RollBack itself
                if (methodName == "Start" || methodName == "Commit" || methodName == "RollBack")
                {
                    string receiver = GetReceiverText(invocation);
                    if (receiver.Contains("tx") || receiver.Contains("transaction") ||
                        receiver.Contains("Transaction"))
                        continue;
                }

                // Check if this invocation is inside a Transaction using block
                if (!IsInsideTransactionBlock(invocation))
                {
                    var lineSpan = invocation.GetLocation().GetLineSpan();
                    diagnostics.Add(new AnalyzerDiagnostic
                    {
                        Id = "BIBIM001",
                        Message = $"Modification API '{methodName}' called outside a Transaction block. " +
                                  "Wrap in 'using (var tx = new Transaction(doc, \"name\")) {{ tx.Start(); ... tx.Commit(); }}'",
                        Severity = AnalyzerSeverity.Error,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1
                    });
                }
            }

            return diagnostics;
        }

        private bool IsInsideTransactionBlock(SyntaxNode node)
        {
            var current = node.Parent;
            while (current != null)
            {
                // Check for using statement with Transaction
                if (current is UsingStatementSyntax usingStmt)
                {
                    string usingText = usingStmt.Declaration?.ToString() ?? usingStmt.Expression?.ToString() ?? "";
                    if (usingText.Contains("Transaction"))
                        return true;
                }

                // Check for local declaration in using context
                if (current is LocalDeclarationStatementSyntax localDecl && localDecl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                {
                    string declText = localDecl.ToString();
                    if (declText.Contains("Transaction"))
                        return true;
                }

                // Check if we're inside the Execute method (which is called within a Transaction by BibimExecutionHandler)
                if (current is MethodDeclarationSyntax method && method.Identifier.Text == "Execute")
                    return true;

                current = current.Parent;
            }
            return false;
        }

        #endregion

        #region BIBIM002: Collector Disposal

        /// <summary>
        /// BIBIM002: Detect FilteredElementCollector not properly disposed.
        /// Collectors should be used inline or in a using statement.
        /// </summary>
        private List<AnalyzerDiagnostic> CheckBIBIM002_CollectorDisposal(SyntaxNode root)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Find variable declarations of FilteredElementCollector
            var declarations = root.DescendantNodes().OfType<VariableDeclarationSyntax>()
                .Where(d => d.Type.ToString().Contains("FilteredElementCollector"));

            foreach (var decl in declarations)
            {
                var parent = decl.Parent;

                // OK if in a using statement
                if (parent is UsingStatementSyntax) continue;
                if (parent is LocalDeclarationStatementSyntax localDecl &&
                    localDecl.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
                    continue;

                // Check if the collector is used inline (chained method calls)
                foreach (var variable in decl.Variables)
                {
                    string varName = variable.Identifier.Text;

                    // Check if Dispose() is called on this variable
                    var methodBody = decl.FirstAncestorOrSelf<BlockSyntax>();
                    if (methodBody == null) continue;

                    bool hasDispose = methodBody.DescendantNodes()
                        .OfType<InvocationExpressionSyntax>()
                        .Any(inv =>
                        {
                            string text = inv.ToString();
                            return text.Contains(varName + ".Dispose()");
                        });

                    if (!hasDispose)
                    {
                        var lineSpan = variable.GetLocation().GetLineSpan();
                        diagnostics.Add(new AnalyzerDiagnostic
                        {
                            Id = "BIBIM002",
                            Message = $"FilteredElementCollector '{varName}' is not disposed. " +
                                      "Use 'using var' or call .Dispose() to prevent memory leaks.",
                            Severity = AnalyzerSeverity.Warning,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1
                        });
                    }
                }
            }

            return diagnostics;
        }

        #endregion

        #region BIBIM003: Ghost Object Filter

        /// <summary>
        /// BIBIM003: Detect FilteredElementCollector usage without ElementType filter.
        /// Design doc §2.4 Layer 1 — Ghost object defense at compile time.
        /// </summary>
        private List<AnalyzerDiagnostic> CheckBIBIM003_GhostObjectFilter(SyntaxNode root)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Find all FilteredElementCollector creation expressions
            var collectorCreations = root.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(o => o.Type.ToString().Contains("FilteredElementCollector"));

            foreach (var creation in collectorCreations)
            {
                // Walk up to find the full expression chain
                var chainRoot = GetExpressionChainRoot(creation);
                string fullChain = chainRoot.ToString();

                // Check if WhereElementIsNotElementType() or WhereElementIsElementType() is present
                bool hasTypeFilter =
                    fullChain.Contains("WhereElementIsNotElementType") ||
                    fullChain.Contains("WhereElementIsElementType") ||
                    fullChain.Contains("OfClass") ||
                    fullChain.Contains("OfCategoryId");

                if (!hasTypeFilter)
                {
                    var lineSpan = creation.GetLocation().GetLineSpan();
                    diagnostics.Add(new AnalyzerDiagnostic
                    {
                        Id = "BIBIM003",
                        Message = "FilteredElementCollector missing ElementType filter. " +
                                  "Add .WhereElementIsNotElementType() to exclude ghost objects (ElementType, AnalyticalModel, etc.). " +
                                  "This prevents runtime crashes from accessing system internal elements.",
                        Severity = AnalyzerSeverity.Warning,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1
                    });
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// Walk up the syntax tree to find the root of a method chain expression.
        /// e.g., new FilteredElementCollector(doc).OfCategory(...).WhereElementIsNotElementType()
        /// </summary>
        private SyntaxNode GetExpressionChainRoot(SyntaxNode node)
        {
            var current = node;
            while (current.Parent is MemberAccessExpressionSyntax ||
                   current.Parent is InvocationExpressionSyntax ||
                   current.Parent is MemberBindingExpressionSyntax)
            {
                current = current.Parent;
            }

            // Also check if the collector is assigned to a variable and chained later
            if (current.Parent is EqualsValueClauseSyntax eq &&
                eq.Parent is VariableDeclaratorSyntax varDecl)
            {
                string varName = varDecl.Identifier.Text;
                var block = varDecl.FirstAncestorOrSelf<BlockSyntax>();
                if (block != null)
                {
                    // Check all usages of this variable in the same block
                    var usages = block.DescendantNodes()
                        .OfType<IdentifierNameSyntax>()
                        .Where(id => id.Identifier.Text == varName);

                    foreach (var usage in usages)
                    {
                        var usageChain = GetExpressionChainRoot(usage);
                        string usageText = usageChain.ToString();
                        if (usageText.Contains("WhereElementIsNotElementType") ||
                            usageText.Contains("WhereElementIsElementType") ||
                            usageText.Contains("OfClass") ||
                            usageText.Contains("OfCategoryId"))
                        {
                            // Found the filter in a subsequent chain — return that node
                            return usageChain;
                        }
                    }
                }
            }

            return current;
        }

        #endregion

        #region BIBIM004: Deprecated API

        /// <summary>
        /// BIBIM004: Detect Revit 2024+ removed/changed API usage.
        /// Uses both hardcoded patterns and RevitAPI.xml deprecated index.
        /// </summary>
        private List<AnalyzerDiagnostic> CheckBIBIM004_DeprecatedApi(SyntaxNode root)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();
            int major = MajorVersion;

            // Skip 2024+ deprecation checks for Revit 2022/2023
            bool check2024Deprecations = major == 0 || major >= 2024;

            // Check hardcoded deprecated patterns
            var memberAccesses = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>();

            foreach (var access in memberAccesses)
            {
                string memberName = access.Name.Identifier.Text;

                if (check2024Deprecations && DeprecatedApiReplacements.TryGetValue(memberName, out string replacement))
                {
                    var lineSpan = access.GetLocation().GetLineSpan();
                    diagnostics.Add(new AnalyzerDiagnostic
                    {
                        Id = "BIBIM004",
                        Message = $"'{memberName}' is removed/changed in Revit 2024+. Use '{replacement}' instead.",
                        Severity = major >= 2024 ? AnalyzerSeverity.Error : AnalyzerSeverity.Warning,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1,
                        SuggestedFix = replacement
                    });
                }
            }

            // Check CurveLoop constructor with list parameter (2024+ removal)
            if (check2024Deprecations)
            {
                var curveLoopCreations = root.DescendantNodes()
                    .OfType<ObjectCreationExpressionSyntax>()
                    .Where(o => o.Type.ToString().Contains("CurveLoop") &&
                                o.ArgumentList != null &&
                                o.ArgumentList.Arguments.Count > 0);

                foreach (var creation in curveLoopCreations)
                {
                    var lineSpan = creation.GetLocation().GetLineSpan();
                    diagnostics.Add(new AnalyzerDiagnostic
                    {
                        Id = "BIBIM004",
                        Message = "CurveLoop constructor with list parameter removed in Revit 2024+. " +
                                  "Use 'var loop = new CurveLoop(); foreach(var c in curves) loop.Append(c);'",
                        Severity = major >= 2024 ? AnalyzerSeverity.Error : AnalyzerSeverity.Warning,
                        Line = lineSpan.StartLinePosition.Line + 1,
                        Column = lineSpan.StartLinePosition.Character + 1
                    });
                }
            }

            // Check XML index for additional deprecated APIs
            if (_xmlIndex != null && _xmlIndex.Status == "ok")
            {
                foreach (var access in memberAccesses)
                {
                    string fullName = ExtractPotentialApiName(access);
                    if (string.IsNullOrEmpty(fullName)) continue;

                    if (_xmlIndex.DeprecatedMembers.Contains(fullName) ||
                        _xmlIndex.DeprecatedTypes.Contains(fullName))
                    {
                        // Don't duplicate hardcoded checks
                        string memberName = access.Name.Identifier.Text;
                        if (DeprecatedApiReplacements.ContainsKey(memberName)) continue;

                        string summary = "";
                        _xmlIndex.MemberSummaries.TryGetValue(fullName, out summary);

                        var lineSpan = access.GetLocation().GetLineSpan();
                        diagnostics.Add(new AnalyzerDiagnostic
                        {
                            Id = "BIBIM004",
                            Message = $"'{fullName}' is marked as deprecated in RevitAPI.xml. {summary}",
                            Severity = AnalyzerSeverity.Warning,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1
                        });
                    }
                }
            }

            return diagnostics;
        }

        private string ExtractPotentialApiName(MemberAccessExpressionSyntax access)
        {
            // Try to extract "TypeName.MemberName" pattern for XML lookup
            string memberName = access.Name.Identifier.Text;
            string expression = access.Expression.ToString();

            // Simple heuristic: if expression ends with a known Revit type name
            var parts = expression.Split('.');
            if (parts.Length > 0)
            {
                string lastPart = parts[parts.Length - 1];
                // Check if it looks like a type name (PascalCase, not a variable)
                if (lastPart.Length > 0 && char.IsUpper(lastPart[0]))
                    return lastPart + "." + memberName;
            }

            return null;
        }

        #endregion

        #region BIBIM005: XYZ Safety

        /// <summary>
        /// BIBIM005: Detect potentially unsafe XYZ operations.
        /// Division by zero or normalization of zero-length vectors.
        /// </summary>
        private List<AnalyzerDiagnostic> CheckBIBIM005_XyzSafety(SyntaxNode root)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            var invocations = root.DescendantNodes().OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                string methodName = GetMethodName(invocation);
                if (string.IsNullOrEmpty(methodName)) continue;

                if (DangerousXyzOps.Contains(methodName))
                {
                    // Check if there's a guard (length check, try-catch, etc.)
                    bool hasGuard = HasSafetyGuard(invocation);

                    if (!hasGuard)
                    {
                        var lineSpan = invocation.GetLocation().GetLineSpan();
                        diagnostics.Add(new AnalyzerDiagnostic
                        {
                            Id = "BIBIM005",
                            Message = $"XYZ.{methodName}() can throw if vector length is zero. " +
                                      "Add a length check: 'if (vector.GetLength() > 1e-9)' before calling.",
                            Severity = AnalyzerSeverity.Warning,
                            Line = lineSpan.StartLinePosition.Line + 1,
                            Column = lineSpan.StartLinePosition.Character + 1
                        });
                    }
                }
            }

            return diagnostics;
        }

        private bool HasSafetyGuard(InvocationExpressionSyntax invocation)
        {
            // Check if the invocation is inside a try-catch
            var current = invocation.Parent;
            while (current != null)
            {
                if (current is TryStatementSyntax) return true;

                // Check if inside an if-statement with a length guard
                if (current is IfStatementSyntax ifStmt)
                {
                    string condition = ifStmt.Condition.ToString().ToLowerInvariant();
                    if (condition.Contains("getlength") || condition.Contains("length") ||
                        condition.Contains("iszerolength") || condition.Contains("normalize"))
                        return true;
                }

                if (current is MethodDeclarationSyntax) break;
                current = current.Parent;
            }

            // Check if there's an if-guard in the same block before this invocation
            var block = invocation.FirstAncestorOrSelf<BlockSyntax>();
            if (block == null) return false;

            var ifStatements = block.DescendantNodes()
                .OfType<IfStatementSyntax>()
                .Where(ifs => ifs.SpanStart < invocation.SpanStart);

            foreach (var ifStmt in ifStatements)
            {
                string condition = ifStmt.Condition.ToString().ToLowerInvariant();
                if (condition.Contains("getlength") || condition.Contains("length") ||
                    condition.Contains("iszerolength") || condition.Contains("normalize"))
                    return true;
            }

            return false;
        }

        #endregion

        #region Helpers

        private string GetMethodName(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                return memberAccess.Name.Identifier.Text;
            if (invocation.Expression is IdentifierNameSyntax identifier)
                return identifier.Identifier.Text;
            return null;
        }

        private string GetReceiverText(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                return memberAccess.Expression.ToString();
            return string.Empty;
        }

        #endregion
    }

    #region Models

    public enum AnalyzerSeverity
    {
        Info,
        Warning,
        Error
    }

    public class AnalyzerDiagnostic
    {
        public string Id { get; set; }
        public string Message { get; set; }
        public AnalyzerSeverity Severity { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public string SuggestedFix { get; set; }

        public override string ToString() =>
            $"[{Id}] L{Line}:C{Column} ({Severity}) {Message}";
    }

    public class AnalyzerReport
    {
        public List<AnalyzerDiagnostic> Diagnostics { get; set; } = new List<AnalyzerDiagnostic>();

        public bool HasErrors => Diagnostics.Any(d => d.Severity == AnalyzerSeverity.Error);
        public bool HasWarnings => Diagnostics.Any(d => d.Severity == AnalyzerSeverity.Warning);
        public int ErrorCount => Diagnostics.Count(d => d.Severity == AnalyzerSeverity.Error);
        public int WarningCount => Diagnostics.Count(d => d.Severity == AnalyzerSeverity.Warning);

        public string FormatSummary()
        {
            if (Diagnostics.Count == 0) return "No issues found.";
            var lines = Diagnostics.Select(d => d.ToString());
            return string.Join("\n", lines);
        }
    }

    public class CodeFixResult
    {
        public string OriginalCode { get; set; }
        public string FixedCode { get; set; }
        public bool HasChanges { get; set; }
        public List<string> AppliedFixes { get; set; } = new List<string>();
        public List<string> SuggestedFixes { get; set; } = new List<string>();
    }

    #endregion
}
