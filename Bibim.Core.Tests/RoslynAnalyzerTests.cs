// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Linq;
using Xunit;

namespace Bibim.Core.Tests
{
    /// <summary>
    /// Tests for RoslynAnalyzerService — BIBIM001~005 custom analyzers.
    /// Design doc §2.4 Ghost Object Defense + Custom Analyzers.
    /// </summary>
    public class RoslynAnalyzerTests
    {
        private readonly RoslynAnalyzerService _analyzer = new RoslynAnalyzerService();

        #region BIBIM001: Transaction Required

        [Fact]
        public void BIBIM001_DetectsModificationOutsideTransaction()
        {
            string code = @"
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class Test
{
    public void Run(Document doc)
    {
        doc.Delete(new ElementId(123));
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d => d.Id == "BIBIM001");
        }

        [Fact]
        public void BIBIM001_NoWarningInsideTransaction()
        {
            string code = @"
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class Test
{
    public void Run(Document doc)
    {
        using (var tx = new Transaction(doc, ""test""))
        {
            tx.Start();
            doc.Delete(new ElementId(123));
            tx.Commit();
        }
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.DoesNotContain(report.Diagnostics, d => d.Id == "BIBIM001" && d.Message.Contains("Delete"));
        }

        [Fact]
        public void BIBIM001_DetectsMissingTransactionInExecuteMethod()
        {
            // Regression for the v1.1 BIBIM001 heuristic fix: BibimExecutionHandler
            // does NOT wrap Execute() in a Transaction (RunCommit uses no outer
            // wrapper; RunDryRun uses a TransactionGroup, which doesn't allow
            // modification APIs directly). Generated code must open its own
            // Transaction even inside Execute. The previous heuristic skipped
            // this check based on the method name alone, hiding real bugs.
            string code = @"
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class Program
{
    public static object Execute(UIApplication uiApp)
    {
        var doc = uiApp.ActiveUIDocument.Document;
        doc.Delete(new ElementId(123));
        return null;
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d => d.Id == "BIBIM001" && d.Message.Contains("Delete"));
        }

        [Fact]
        public void BIBIM001_NoWarningInExecuteMethodWhenWrappedInTransaction()
        {
            // The legitimate happy path: generated code wraps modifications in
            // its own Transaction inside Execute. Must NOT flag.
            string code = @"
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class Program
{
    public static object Execute(UIApplication uiApp)
    {
        var doc = uiApp.ActiveUIDocument.Document;
        using (var tx = new Transaction(doc, ""delete""))
        {
            tx.Start();
            doc.Delete(new ElementId(123));
            tx.Commit();
        }
        return null;
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.DoesNotContain(report.Diagnostics, d => d.Id == "BIBIM001" && d.Message.Contains("Delete"));
        }

        [Fact]
        public void BIBIM001_TransactionGroupAloneDoesNotSatisfy()
        {
            // TransactionGroup is NOT a Transaction — it cannot wrap modification
            // APIs directly. BIBIM001 must still flag bare doc.Delete inside it.
            string code = @"
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class Program
{
    public static object Execute(UIApplication uiApp)
    {
        var doc = uiApp.ActiveUIDocument.Document;
        using (var tg = new TransactionGroup(doc, ""group""))
        {
            tg.Start();
            doc.Delete(new ElementId(123));
            tg.Assimilate();
        }
        return null;
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d => d.Id == "BIBIM001" && d.Message.Contains("Delete"));
        }

        #endregion

        #region BIBIM002: Collector Disposal

        [Fact]
        public void BIBIM002_DetectsUndisposedCollector()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Document doc)
    {
        FilteredElementCollector collector = new FilteredElementCollector(doc);
        var elements = collector.OfClass(typeof(Wall)).ToList();
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d => d.Id == "BIBIM002");
        }

        [Fact]
        public void BIBIM002_NoWarningWithDispose()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Document doc)
    {
        FilteredElementCollector collector = new FilteredElementCollector(doc);
        var elements = collector.OfClass(typeof(Wall)).ToList();
        collector.Dispose();
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.DoesNotContain(report.Diagnostics, d => d.Id == "BIBIM002");
        }

        #endregion

        #region BIBIM003: Ghost Object Filter

        [Fact]
        public void BIBIM003_DetectsMissingElementTypeFilter()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Document doc)
    {
        var collector = new FilteredElementCollector(doc);
        var elements = collector.ToList();
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d => d.Id == "BIBIM003");
        }

        [Fact]
        public void BIBIM003_NoWarningWithWhereElementIsNotElementType()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Document doc)
    {
        var elements = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .OfCategory(BuiltInCategory.OST_Walls)
            .ToList();
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.DoesNotContain(report.Diagnostics, d => d.Id == "BIBIM003");
        }

        [Fact]
        public void BIBIM003_NoWarningWithOfClass()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run(Document doc)
    {
        var walls = new FilteredElementCollector(doc)
            .OfClass(typeof(Wall))
            .ToList();
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.DoesNotContain(report.Diagnostics, d => d.Id == "BIBIM003");
        }

        #endregion

        #region BIBIM004: Deprecated API

        [Fact]
        public void BIBIM004_DetectsIntegerValue()
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
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d =>
                d.Id == "BIBIM004" && d.Message.Contains("IntegerValue"));
        }

        [Fact]
        public void BIBIM004_DetectsCurveLoopWithArgs()
        {
            string code = @"
using System.Collections.Generic;
using Autodesk.Revit.DB;

public class Test
{
    public void Run()
    {
        var curves = new List<Curve>();
        var loop = new CurveLoop(curves);
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d =>
                d.Id == "BIBIM004" && d.Message.Contains("CurveLoop"));
        }

        #endregion

        #region BIBIM005: XYZ Safety

        [Fact]
        public void BIBIM005_DetectsUnsafeNormalize()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run()
    {
        var v = new XYZ(1, 0, 0);
        var n = v.Normalize();
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.Contains(report.Diagnostics, d => d.Id == "BIBIM005");
        }

        [Fact]
        public void BIBIM005_NoWarningWithGuard()
        {
            string code = @"
using Autodesk.Revit.DB;

public class Test
{
    public void Run()
    {
        var v = new XYZ(1, 0, 0);
        if (v.GetLength() > 1e-9)
        {
            var n = v.Normalize();
        }
    }
}";
            var report = _analyzer.Analyze(code);
            Assert.DoesNotContain(report.Diagnostics, d => d.Id == "BIBIM005");
        }

        #endregion
    }
}
