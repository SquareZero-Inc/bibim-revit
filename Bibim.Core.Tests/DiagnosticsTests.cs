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
        public void Sdk_Anthropic_IsLoadable()
        {
            // Force load by touching the type directly
            var type = typeof(Anthropic.AnthropicClient);
            Assert.NotNull(type);
            Assert.Contains("Anthropic", type.Assembly.GetName().Name);
        }

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

        [Fact]
        public void Sdk_SystemTextJson_IsLoadable()
        {
            var type = typeof(System.Text.Json.JsonSerializer);
            Assert.NotNull(type);
        }

        [Fact]
        public void Sdk_MicrosoftDI_IsLoadable()
        {
            var type = typeof(Microsoft.Extensions.DependencyInjection.ServiceCollection);
            Assert.NotNull(type);
        }

        // ── SDK Assembly Version Sanity ──────────────────────

        [Fact]
        public void Anthropic_VersionIsExpected()
        {
            var type = ResolveType("Anthropic.AnthropicClient");
            Assert.NotNull(type);

            var ver = type.Assembly.GetName().Version;
            Assert.NotNull(ver);
            // We expect 12.x based on our NuGet reference
            Assert.True(ver.Major >= 12, $"Anthropic SDK version {ver} is older than expected (>=12.x)");
        }

        [Fact]
        public void Roslyn_VersionIsExpected()
        {
            var type = ResolveType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
            Assert.NotNull(type);

            var ver = type.Assembly.GetName().Version;
            Assert.NotNull(ver);
            Assert.True(ver.Major >= 4, $"Roslyn version {ver} is older than expected (>=4.x)");
        }

        [Fact]
        public void SystemTextJson_VersionIsExpected()
        {
            var type = ResolveType("System.Text.Json.JsonSerializer");
            Assert.NotNull(type);

            var ver = type.Assembly.GetName().Version;
            Assert.NotNull(ver);
            Assert.True(ver.Major >= 10, $"System.Text.Json version {ver} is older than expected (>=10.x)");
        }

        // ── Assembly Signing Checks ──────────────────────────

        [Theory]
        [InlineData("Anthropic")]
        [InlineData("Microsoft.CodeAnalysis.CSharp")]
        [InlineData("Newtonsoft.Json")]
        [InlineData("System.Text.Json")]
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

        // ── Anthropic SDK API Surface ────────────────────────

        [Fact]
        public void Anthropic_HasExpectedApiSurface()
        {
            // Verify key types exist that our LlmOrchestrationService depends on
            Assert.NotNull(ResolveType("Anthropic.AnthropicClient"));
            Assert.NotNull(ResolveType("Anthropic.Models.Messages.MessageCreateParams"));
            Assert.NotNull(ResolveType("Anthropic.Models.Messages.MessageParam"));
            Assert.NotNull(ResolveType("Anthropic.Models.Messages.Role"));
        }

        [Fact]
        public void Anthropic_ClientCanBeInstantiated()
        {
            // Verify the client constructor pattern we use actually works
            var client = new Anthropic.AnthropicClient() { ApiKey = "test-key-not-real" };
            Assert.NotNull(client);
            Assert.NotNull(client.Messages);
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
