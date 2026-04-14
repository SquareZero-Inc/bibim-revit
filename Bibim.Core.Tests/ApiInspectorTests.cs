// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Linq;
using Xunit;

namespace Bibim.Core.Tests
{
    /// <summary>
    /// Tests for ApiInspectorService — API usage extraction and categorization.
    /// Design doc §3.2.
    /// </summary>
    public class ApiInspectorTests
    {
        private readonly ApiInspectorService _inspector = new ApiInspectorService();

        [Fact]
        public void Inspect_ExtractsFilteredElementCollector()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Document doc)
    {
        var collector = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Walls);
    }
}";
            var report = _inspector.Inspect(code);

            Assert.True(report.ApiUsages.Count > 0);
            Assert.Contains(report.ApiUsages, u =>
                u.FullExpression.Contains("FilteredElementCollector"));
        }

        [Fact]
        public void Inspect_ExtractsTransaction()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Document doc)
    {
        using (var tx = new Transaction(doc, ""test""))
        {
            tx.Start();
            tx.Commit();
        }
    }
}";
            var report = _inspector.Inspect(code);

            Assert.Contains(report.ApiUsages, u =>
                u.FullExpression.Contains("Transaction"));
        }

        [Fact]
        public void Inspect_CategorizesDeprecatedApi()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Parameter param)
    {
        int val = param.IntegerValue;
    }
}";
            var report = _inspector.Inspect(code);

            Assert.Contains(report.ApiUsages, u =>
                u.ApiName == "IntegerValue" && u.Status == ApiStatus.VersionSpecific);
            Assert.True(report.VersionSpecificCount > 0);
        }

        [Fact]
        public void Inspect_SafeApiCountedCorrectly()
        {
            string code = @"
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class Test
{
    public static object Execute(UIApplication uiApp)
    {
        var doc = uiApp.ActiveUIDocument.Document;
        var walls = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Walls)
            .ToList();
        return walls.Count;
    }
}";
            var report = _inspector.Inspect(code);

            Assert.True(report.SafeCount > 0);
            Assert.Equal(0, report.DeprecatedCount);
        }

        [Fact]
        public void Inspect_WithDryRunResult_Placeholder()
        {
            // ExecutionResult requires Revit SDK — tested in integration tests.
            // Here we verify the report works without DryRun data.
            string code = "var x = new FilteredElementCollector(doc);";
            var report = _inspector.Inspect(code);

            Assert.Null(report.DryRunSummary);
            Assert.True(report.ApiUsages.Count > 0);
        }

        [Fact]
        public void FormatReport_ProducesReadableOutput()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Parameter param)
    {
        int val = param.IntegerValue;
        var collector = new FilteredElementCollector(null);
    }
}";
            var report = _inspector.Inspect(code);
            string formatted = _inspector.FormatReport(report);

            Assert.Contains("API Inspector Report", formatted);
            Assert.Contains("✅", formatted);
        }
    }
}
