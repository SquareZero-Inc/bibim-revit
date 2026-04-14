// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System.Reflection;
using Xunit;

namespace Bibim.Core.Tests
{
    public class RoslynCompilerServiceTests
    {
        [Fact]
        public void PrepareSource_WrapsTopLevelStatements_WhenStringLiteralContainsClassKeyword()
        {
            var service = new RoslynCompilerService();
            string source = @"using System;
var sb = new System.Text.StringBuilder();
sb.AppendLine(""  Class          : Duct"");
return sb.ToString();";

            object prepared = InvokePrepareSource(service, source);
            string wrapped = GetPreparedSourceProperty(prepared, "WrappedSource");

            Assert.Contains("namespace BibimGenerated", wrapped);
            Assert.Contains("public static object Execute", wrapped);
            Assert.Contains("var uidoc = uiApp.ActiveUIDocument;", wrapped);
            Assert.Contains(@"sb.AppendLine(""  Class          : Duct"");", wrapped);
        }

        [Fact]
        public void PrepareSource_PreservesStructuredCompilationUnit()
        {
            var service = new RoslynCompilerService();
            string source = @"namespace CustomNamespace
{
    public static class Program
    {
        public static object Execute(object uiApp)
        {
            return ""ok"";
        }
    }
}";

            object prepared = InvokePrepareSource(service, source);
            string wrapped = GetPreparedSourceProperty(prepared, "WrappedSource");

            Assert.DoesNotContain("namespace BibimGenerated", wrapped);
            Assert.Contains("namespace CustomNamespace", wrapped);
        }

        private static object InvokePrepareSource(RoslynCompilerService service, string source)
        {
            var method = typeof(RoslynCompilerService).GetMethod(
                "PrepareSource",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(method);
            return method.Invoke(service, new object[] { source });
        }

        private static string GetPreparedSourceProperty(object preparedSource, string propertyName)
        {
            var property = preparedSource.GetType().GetProperty(propertyName);

            Assert.NotNull(property);
            return property.GetValue(preparedSource) as string;
        }
    }
}
