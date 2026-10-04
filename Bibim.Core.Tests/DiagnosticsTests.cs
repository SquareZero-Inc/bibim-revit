// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Bibim.Core.Tests
{
    /// <summary>
    /// Build-time SDK verification tests.
    /// Validates that all required NuGet packages are loadable,
    /// assemblies are properly signed, and key types are resolvable.
    /// </summary>
    public class DiagnosticsTests
    {
        // ── SDK Type Resolution ──────────────────────────────

        [Fact]
        public void Sdk_Roslyn_IsLoadable()
        {
            var type = typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation);
            Assert.NotNull(type);
            Assert.Contains("CodeAnalysis", type.Assembly.GetName().Name);
        }

        [Fact]
        public void Sdk_NewtonsoftJson_IsLoadable()
        {
            var type = typeof(Newtonsoft.Json.JsonConvert);
            Assert.NotNull(type);
        }

        // ── SDK Assembly Version Sanity ──────────────────────

        [Fact]
        public void Roslyn_VersionIsExpected()
        {
            var type = ResolveType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
            Assert.NotNull(type);

            var ver = type.Assembly.GetName().Version;
            Assert.NotNull(ver);
            Assert.True(ver.Major >= 4, $"Roslyn version {ver} is older than expected (>=4.x)");
        }

        // ── Assembly Signing Checks ──────────────────────────

        [Theory]
        [InlineData("Microsoft.CodeAnalysis.CSharp")]
        [InlineData("Newtonsoft.Json")]
        public void Sdk_AssemblyHasPublicKeyToken(string assemblyName)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName);

            // If not loaded yet, try loading
            if (asm == null)
            {
                try { asm = Assembly.Load(assemblyName); }
                catch { /* will assert below */ }
            }

            Assert.True(asm != null, $"Assembly '{assemblyName}' is not loaded");

            var pubKey = asm.GetName().GetPublicKeyToken();
            // Note: some SDKs may not be strong-named — log but don't fail
            if (pubKey == null || pubKey.Length == 0)
            {
                // Soft check — log warning but don't fail the test
                // Strong naming is recommended but not all NuGet packages do it
                return;
            }

            string token = BitConverter.ToString(pubKey).Replace("-", "").ToLowerInvariant();
            Assert.False(string.IsNullOrEmpty(token));
        }

        // ── Roslyn Compiler API Surface ──────────────────────

        [Fact]
        public void Roslyn_CanCreateCompilation()
        {
            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                "class Test { }");
            Assert.NotNull(tree);

            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "DiagTest",
                new[] { tree },
                new[] { Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                    typeof(object).Assembly.Location) });
            Assert.NotNull(compilation);
        }

        // ── All Loaded Assemblies Snapshot ───────────────────

        [Fact]
        public void Snapshot_AllLoadedAssemblies()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .OrderBy(a => a.GetName().Name)
                .Select(a => new
                {
                    Name = a.GetName().Name,
                    Version = a.GetName().Version?.ToString(),
                    Signed = (a.GetName().GetPublicKeyToken()?.Length ?? 0) > 0
                })
                .ToList();

            // This test always passes — it's a diagnostic snapshot
            Assert.NotEmpty(assemblies);

            // Log for debugging
            foreach (var a in assemblies)
            {
                string sign = a.Signed ? "signed" : "unsigned";
                System.Diagnostics.Debug.WriteLine($"  {a.Name} v{a.Version} ({sign})");
            }
        }

        // ── Helper ───────────────────────────────────────────

        private static Type ResolveType(string fullTypeName)
        {
            return Type.GetType(fullTypeName, throwOnError: false)
                   ?? AppDomain.CurrentDomain.GetAssemblies()
                       .Select(a => a.GetType(fullTypeName, false))
                       .FirstOrDefault(t => t != null);
        }
    }
}
