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
        public void Fix_AsInteger_To_AsValueString()
        {
            string code = "var x = param.AsInteger();";
            var result = _analyzer.ApplyAutoFixes(code);

            Assert.True(result.HasChanges);
            Assert.Contains(".AsValueString()", result.FixedCode);
            Assert.DoesNotContain(".AsInteger()", result.FixedCode);
        }

        [Fact]
        public void Fix_LevelId_To_GetParameter()
        {
            string code = "var levelId = wall.LevelId;";
            var result = _analyzer.ApplyAutoFixes(code);

            Assert.True(result.HasChanges);
            Assert.Contains("get_Parameter", result.FixedCode);
            Assert.Contains("WALL_BASE_CONSTRAINT", result.FixedCode);
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
        public void Fix_MultipleIssues_AllFixed()
        {
            string code = @"
int val = param.IntegerValue;
var x = param2.AsInteger();";
            var result = _analyzer.ApplyAutoFixes(code);

            Assert.True(result.HasChanges);
            Assert.Equal(2, result.AppliedFixes.Count);
            Assert.Contains(".Value", result.FixedCode);
            Assert.Contains(".AsValueString()", result.FixedCode);
        }
    }
}
