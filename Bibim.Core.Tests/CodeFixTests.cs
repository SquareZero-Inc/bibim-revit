// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using Xunit;

namespace Bibim.Core.Tests
{
    /// <summary>
    /// Tests for RoslynAnalyzerService.ApplyAutoFixes — LLM-free code fixes.
    /// Design doc §2.1 Step 5 — Code Fix Provider.
    /// </summary>
    public class CodeFixTests
    {
        private readonly RoslynAnalyzerService _analyzer = new RoslynAnalyzerService();

        [Fact]
        public void Fix_IntegerValue_To_Value()
        {
            string code = "int val = param.IntegerValue;";
            var result = _analyzer.ApplyAutoFixes(code);

            Assert.True(result.HasChanges);
            Assert.Contains(".Value", result.FixedCode);
            Assert.DoesNotContain(".IntegerValue", result.FixedCode);
            Assert.Contains("BIBIM004-FIX: IntegerValue", result.AppliedFixes[0]);
        }

        [Fact]
        public void AsInteger_And_LevelId_AreLeftAlone()
        {
            // Both exist unchanged in the Revit 2024 and 2026 APIs (checked against RevitAPI.dll);
            // rewriting them produced AsValueString() where an int was needed and
            // WALL_BASE_CONSTRAINT lookups on doors.
            string code = "var x = param.AsInteger();\nvar levelId = door.LevelId;";
            var result = _analyzer.ApplyAutoFixes(code);

            Assert.False(result.HasChanges);
            Assert.Equal(code, result.FixedCode);
        }

        [Fact]
        public void AsInteger_And_LevelId_AreNotReportedAsDeprecated()
        {
            _analyzer.SetRevitVersion("2026");
            var report = _analyzer.Analyze(@"
public class Test { public void Run(Autodesk.Revit.DB.Parameter p, Autodesk.Revit.DB.Element e) {
    int i = p.AsInteger(); var l = e.LevelId; } }");
            Assert.DoesNotContain(report.Diagnostics, d => d.Id == "BIBIM004");
        }

        [Theory]
        [InlineData("2024", AnalyzerSeverity.Warning)]   // obsolete but compiles
        [InlineData("2026", AnalyzerSeverity.Error)]     // removed
        public void IntegerValue_Severity_FollowsTheApi(string version, AnalyzerSeverity expected)
        {
            _analyzer.SetRevitVersion(version);
            var report = _analyzer.Analyze(@"
public class Test { public void Run(Autodesk.Revit.DB.ElementId id) { int v = id.IntegerValue; } }");
            Assert.Contains(report.Diagnostics, d => d.Id == "BIBIM004" && d.Severity == expected);
        }

        [Fact]
        public void Fix_CurveLoop_SuggestsOnly()
        {
            string code = "var loop = new CurveLoop(curves);";
            var result = _analyzer.ApplyAutoFixes(code);

            // CurveLoop fix is too complex for auto-fix, should only suggest
            Assert.True(result.SuggestedFixes.Count > 0);
            Assert.Contains("CurveLoop", result.SuggestedFixes[0]);
        }

        [Fact]
        public void Fix_NoChanges_WhenCodeIsClean()
        {
            string code = @"
var collector = new FilteredElementCollector(doc)
    .WhereElementIsNotElementType()
    .OfCategory(BuiltInCategory.OST_Walls)
    .ToList();";
            var result = _analyzer.ApplyAutoFixes(code);

            Assert.False(result.HasChanges);
            Assert.Empty(result.AppliedFixes);
        }

        [Fact]
        public void Fix_OnlyIntegerValue_IsRewritten()
        {
            string code = @"
int val = param.IntegerValue;
var x = param2.AsInteger();";
            var result = _analyzer.ApplyAutoFixes(code);

            Assert.True(result.HasChanges);
            Assert.Single(result.AppliedFixes);
            Assert.Contains("param.Value", result.FixedCode);
            Assert.Contains("param2.AsInteger()", result.FixedCode);
        }
    }
}
