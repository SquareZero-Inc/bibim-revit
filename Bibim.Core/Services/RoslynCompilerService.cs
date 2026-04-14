// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Bibim.Core
{
    /// <summary>
    /// Roslyn-based in-memory C# compiler for LLM-generated code.
    /// Design doc §2.1 — Roslyn Compile Engine.
    /// 
    /// Flow: C# source → Parse → Add Revit references → Compile → Diagnostics → Assembly
    /// Custom analyzers BIBIM001-005 run during compilation.
    /// </summary>
    public class RoslynCompilerService
    {
        private readonly List<MetadataReference> _baseReferences;
        private readonly List<string> _defaultUsings;

        public RoslynCompilerService()
        {
            _baseReferences = BuildBaseReferences();
            _defaultUsings = new List<string>
            {
                "System",
                "System.Linq",
                "System.Collections.Generic",
                "Autodesk.Revit.DB",
                "Autodesk.Revit.UI",
                "Autodesk.Revit.DB.Architecture",
                "Autodesk.Revit.DB.Structure"
            };
        }

        /// <summary>
        /// Compile C# source code to an in-memory assembly.
        /// Returns compilation result with diagnostics.
        /// </summary>
        public CompilationResult Compile(string sourceCode, string assemblyName = null)
        {
            assemblyName = assemblyName ?? $"BibimGen_{Guid.NewGuid():N}";
            var result = new CompilationResult();

            try
            {
                var preparedSource = PrepareSource(sourceCode);
                result.OriginalSource = preparedSource.OriginalSource;
                result.NormalizedSource = preparedSource.NormalizedSource;
                result.WrappedSource = preparedSource.WrappedSource;

                var syntaxTree = CSharpSyntaxTree.ParseText(preparedSource.WrappedSource,
                    new CSharpParseOptions(LanguageVersion.Latest));

                // Check parse errors first
                var parseDiags = syntaxTree.GetDiagnostics().ToList();
                if (parseDiags.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    result.Success = false;
                    result.Diagnostics = parseDiags.Select(MapDiagnostic).ToList();
                    result.ErrorSummary = FormatErrors(parseDiags);
                    return result;
                }

                var compilation = CSharpCompilation.Create(
                    assemblyName,
                    syntaxTrees: new[] { syntaxTree },
                    references: _baseReferences,
                    options: new CSharpCompilationOptions(
                        OutputKind.DynamicallyLinkedLibrary,
                        optimizationLevel: OptimizationLevel.Release,
                        allowUnsafe: false));

                using (var ms = new MemoryStream())
                {
                    EmitResult emitResult = compilation.Emit(ms);

                    result.Diagnostics = emitResult.Diagnostics
                        .Where(d => d.Severity >= DiagnosticSeverity.Warning)
                        .Select(MapDiagnostic)
                        .ToList();

                    if (!emitResult.Success)
                    {
                        result.Success = false;
                        result.ErrorSummary = FormatErrors(
                            emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                        return result;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    result.Assembly = Assembly.Load(ms.ToArray());
                    result.Success = true;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorSummary = $"Compilation exception: {ex.Message}";
                Logger.LogError("RoslynCompilerService", ex);
            }

            return result;
        }

        /// <summary>
        /// Normalize raw source and wrap method-body fragments in the expected boilerplate.
        /// Generated code signature: BibimGenerated.Program.Execute(UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx)
        /// </summary>
        private PreparedSource PrepareSource(string code)
        {
            string original = code ?? string.Empty;
            string normalized = NormalizeLineEndings(original).Trim();
            var usingLines = ExtractLeadingUsingDirectives(ref normalized);

            if (LooksLikeCompilationUnit(normalized))
            {
                string fullSource = MergeUsingDirectives(usingLines) + normalized;
                return new PreparedSource
                {
                    OriginalSource = original,
                    NormalizedSource = fullSource,
                    WrappedSource = fullSource
                };
            }

            // Strip duplicate declarations of app/doc/uidoc that the wrapper already provides
            string bodyCode = StripPreludeVariables(normalized);

            string usings = MergeUsingDirectives(usingLines);
            string wrappedCode = $@"{usings}

namespace BibimGenerated
{{
    public class Program
    {{
        public static object Execute(Autodesk.Revit.UI.UIApplication uiApp, Bibim.Core.BibimExecutionContext ctx)
        {{
            var app = uiApp.Application;
            var doc = uiApp.ActiveUIDocument.Document;
            var uidoc = uiApp.ActiveUIDocument;

{bodyCode}

            return ""Execution completed."";
        }}
    }}
}}";

            return new PreparedSource
            {
                OriginalSource = original,
                NormalizedSource = normalized,
                WrappedSource = wrappedCode
            };
        }

        /// <summary>
        /// Remove lines that re-declare app, doc, uidoc, or ctx — the wrapper already provides these.
        /// </summary>
        private static string StripPreludeVariables(string code)
        {
            if (string.IsNullOrEmpty(code)) return code;

            var lines = code.Split(new[] { '\n' }, StringSplitOptions.None);
            var result = new List<string>(lines.Length);

            foreach (var line in lines)
            {
                string trimmed = line.TrimStart();
                if (IsPreludeVariableLine(trimmed))
                    continue;
                result.Add(line);
            }

            return string.Join("\n", result);
        }

        private static bool IsPreludeVariableLine(string trimmed)
        {
            string[] patterns = {
                "var doc ", "var doc=", "Document doc ", "Document doc=",
                "var uidoc ", "var uidoc=", "UIDocument uidoc ", "UIDocument uidoc=",
                "var app ", "var app=", "Application app ", "Application app=",
                "var ctx ", "var ctx=", "BibimExecutionContext ctx ", "BibimExecutionContext ctx=",
                "Bibim.Core.BibimExecutionContext ctx ", "Bibim.Core.BibimExecutionContext ctx=",
                "var ctx=new", "var ctx =new",
            };

            foreach (var p in patterns)
            {
                if (trimmed.StartsWith(p, StringComparison.Ordinal))
                    return true;
            }

            // Fully qualified patterns
            if (trimmed.Contains("Document doc ") || trimmed.Contains("Document doc="))
                return true;
            if (trimmed.Contains("UIDocument uidoc ") || trimmed.Contains("UIDocument uidoc="))
                return true;
            if (trimmed.Contains("Application app ") || trimmed.Contains("Application app="))
                return true;
            if (trimmed.Contains("BibimExecutionContext ctx ") || trimmed.Contains("BibimExecutionContext ctx="))
                return true;

            return false;
        }

        private static string NormalizeLineEndings(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
        }

        private List<string> ExtractLeadingUsingDirectives(ref string code)
        {
            var lines = NormalizeLineEndings(code)
                .Split(new[] { '\n' }, StringSplitOptions.None)
                .ToList();

            var usingLines = new List<string>();
            int index = 0;

            while (index < lines.Count)
            {
                string trimmed = (lines[index] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    index++;
                    continue;
                }

                if (trimmed.StartsWith("using ", StringComparison.Ordinal) && trimmed.EndsWith(";", StringComparison.Ordinal))
                {
                    usingLines.Add(trimmed);
                    index++;
                    continue;
                }

                break;
            }

            code = string.Join("\n", lines.Skip(index)).Trim();
            return usingLines;
        }

        private bool LooksLikeCompilationUnit(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return false;

            var syntaxTree = CSharpSyntaxTree.ParseText(
                code,
                new CSharpParseOptions(LanguageVersion.Latest));
            var root = syntaxTree.GetCompilationUnitRoot();

            bool hasGlobalStatements = root.Members.Any(member => member.IsKind(SyntaxKind.GlobalStatement));
            if (hasGlobalStatements)
                return false;

            return root.Members.Any(member =>
                member.IsKind(SyntaxKind.NamespaceDeclaration) ||
                member.IsKind(SyntaxKind.FileScopedNamespaceDeclaration) ||
                member.IsKind(SyntaxKind.ClassDeclaration) ||
                member.IsKind(SyntaxKind.StructDeclaration) ||
                member.IsKind(SyntaxKind.RecordDeclaration) ||
                member.IsKind(SyntaxKind.RecordStructDeclaration) ||
                member.IsKind(SyntaxKind.InterfaceDeclaration) ||
                member.IsKind(SyntaxKind.EnumDeclaration) ||
                member.IsKind(SyntaxKind.DelegateDeclaration));
        }

        private string MergeUsingDirectives(IEnumerable<string> extraUsings)
        {
            var merged = _defaultUsings
                .Select(u => $"using {u};")
                .Concat(extraUsings ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var sb = new StringBuilder();
            foreach (var line in merged)
                sb.AppendLine(line);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Build the base set of MetadataReferences for Revit API compilation.
        /// Includes: mscorlib, System.*, RevitAPI, RevitAPIUI
        /// </summary>
        private List<MetadataReference> BuildBaseReferences()
        {
            var refs = new List<MetadataReference>();

            // Core .NET references
            var trustedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location)
                .Distinct();

            foreach (var path in trustedAssemblies)
            {
                try
                {
                    refs.Add(MetadataReference.CreateFromFile(path));
                }
                catch { /* Skip assemblies that can't be referenced */ }
            }

            // Revit API references (loaded at runtime by Revit host)
            AddRevitReference(refs, "RevitAPI");
            AddRevitReference(refs, "RevitAPIUI");

            return refs;
        }

        private void AddRevitReference(List<MetadataReference> refs, string assemblyName)
        {
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == assemblyName);
                if (asm != null && !string.IsNullOrEmpty(asm.Location))
                {
                    refs.Add(MetadataReference.CreateFromFile(asm.Location));
                }
            }
            catch (Exception ex)
            {
                Logger.Log("RoslynCompilerService", $"Could not add {assemblyName} reference: {ex.Message}");
            }
        }

        private BibimDiagnostic MapDiagnostic(Diagnostic d)
        {
            var lineSpan = d.Location.GetMappedLineSpan();
            return new BibimDiagnostic
            {
                Id = d.Id,
                Message = d.GetMessage(),
                Severity = d.Severity.ToString(),
                Line = lineSpan.StartLinePosition.Line + 1,
                Column = lineSpan.StartLinePosition.Character + 1
            };
        }

        private string FormatErrors(IEnumerable<Diagnostic> diagnostics)
        {
            var errors = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(10)
                .Select(d =>
                {
                    var span = d.Location.GetMappedLineSpan();
                    return $"[{d.Id}] Line {span.StartLinePosition.Line + 1}: {d.GetMessage()}";
                });
            return string.Join("\n", errors);
        }
    }

    /// <summary>
    /// Result of a Roslyn compilation attempt.
    /// </summary>
    public class CompilationResult
    {
        public bool Success { get; set; }
        public Assembly Assembly { get; set; }
        public List<BibimDiagnostic> Diagnostics { get; set; } = new List<BibimDiagnostic>();
        public string ErrorSummary { get; set; }
        public string OriginalSource { get; set; }
        public string NormalizedSource { get; set; }
        public string WrappedSource { get; set; }
    }

    /// <summary>
    /// Simplified diagnostic info for UI display and LLM feedback.
    /// </summary>
    public class BibimDiagnostic
    {
        public string Id { get; set; }
        public string Message { get; set; }
        public string Severity { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }

        public override string ToString() => $"[{Id}] L{Line}:C{Column} ({Severity}) {Message}";
    }

    internal class PreparedSource
    {
        public string OriginalSource { get; set; }
        public string NormalizedSource { get; set; }
        public string WrappedSource { get; set; }
    }
}
