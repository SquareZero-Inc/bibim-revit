// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Bibim.Core
{
    /// <summary>
    /// Runtime diagnostics — assembly signing, SDK load status, version info.
    /// Call RunFullDiagnostics() to get a complete report.
    /// Results are logged and can be sent to frontend via WebView2Bridge.
    /// </summary>
    public static class DiagnosticsService
    {
        /// <summary>
        /// Run all diagnostics and return structured report.
        /// </summary>
        public static DiagnosticsReport RunFullDiagnostics()
        {
            var report = new DiagnosticsReport
            {
                Timestamp = DateTime.Now,
                AppVersion = BibimApp.AppVersion,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Checks = new List<DiagCheck>()
            };

            // 1. Assembly signing
            report.Checks.Add(CheckAssemblySigning());

            // 2. SDK probes
            report.Checks.AddRange(CheckSdkLoads());

            // 3. Revit API availability
            report.Checks.Add(CheckRevitApi());

            // 4. WebView2 runtime
            report.Checks.Add(CheckWebView2Runtime());

            // 5. Config / resource files
            report.Checks.Add(CheckConfigFiles());

            // Log summary
            LogReport(report);

            return report;
        }

        // ── 1. Assembly Signing ──────────────────────────────

        private static DiagCheck CheckAssemblySigning()
        {
            var check = new DiagCheck { Name = "AssemblySigning" };
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var name = asm.GetName();
                var pubKey = name.GetPublicKeyToken();

                if (pubKey != null && pubKey.Length > 0)
                {
                    string token = BitConverter.ToString(pubKey).Replace("-", "").ToLowerInvariant();
                    check.Status = DiagStatus.OK;
                    check.Detail = $"Signed. PublicKeyToken={token}";
                }
                else
                {
                    check.Status = DiagStatus.Warning;
                    check.Detail = "Assembly is NOT strong-name signed (no PublicKeyToken)";
                }

                // Authenticode (digital signature) check
                check.Extra["StrongName"] = pubKey != null && pubKey.Length > 0;
                check.Extra["Location"] = asm.Location;
                check.Extra["FullName"] = asm.FullName;
            }
            catch (Exception ex)
            {
                check.Status = DiagStatus.Error;
                check.Detail = $"Signing check failed: {ex.Message}";
            }
            return check;
        }

        // ── 2. SDK Load Probes ───────────────────────────────

        private static readonly (string Name, string TypeProbe)[] SdkProbes = new[]
        {
            ("Anthropic SDK",                "Anthropic.AnthropicClient"),
            ("Roslyn (CodeAnalysis)",         "Microsoft.CodeAnalysis.CSharp.CSharpCompilation"),
            ("WebView2",                      "Microsoft.Web.WebView2.Core.CoreWebView2Environment"),

            ("Newtonsoft.Json",               "Newtonsoft.Json.JsonConvert"),
            ("System.Text.Json",              "System.Text.Json.JsonSerializer"),
            ("Markdig",                       "Markdig.Markdown"),
            ("Microsoft.Extensions.DI",       "Microsoft.Extensions.DependencyInjection.ServiceCollection"),
        };

        private static List<DiagCheck> CheckSdkLoads()
        {
            var results = new List<DiagCheck>();

            foreach (var (sdkName, typeProbe) in SdkProbes)
            {
                var check = new DiagCheck { Name = $"SDK:{sdkName}" };
                try
                {
                    var type = Type.GetType(typeProbe, throwOnError: false)
                               ?? AppDomain.CurrentDomain.GetAssemblies()
                                   .Select(a => a.GetType(typeProbe, false))
                                   .FirstOrDefault(t => t != null);

                    if (type != null)
                    {
                        var sdkAsm = type.Assembly;
                        var ver = sdkAsm.GetName().Version?.ToString() ?? "unknown";
                        var signed = (sdkAsm.GetName().GetPublicKeyToken()?.Length ?? 0) > 0;

                        check.Status = DiagStatus.OK;
                        check.Detail = $"v{ver} loaded, signed={signed}";
                        check.Extra["Version"] = ver;
                        check.Extra["Signed"] = signed;
                        check.Extra["Location"] = sdkAsm.Location;
                    }
                    else
                    {
                        check.Status = DiagStatus.Error;
                        check.Detail = $"Type '{typeProbe}' not found — SDK may not be loaded";
                    }
                }
                catch (Exception ex)
                {
                    check.Status = DiagStatus.Error;
                    check.Detail = $"Probe failed: {ex.Message}";
                }
                results.Add(check);
            }

            return results;
        }

        // ── 3. Revit API ─────────────────────────────────────

        private static DiagCheck CheckRevitApi()
        {
            var check = new DiagCheck { Name = "RevitAPI" };
            try
            {
                var revitAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "RevitAPI");

                if (revitAsm != null)
                {
                    var ver = revitAsm.GetName().Version?.ToString() ?? "unknown";
                    check.Status = DiagStatus.OK;
                    check.Detail = $"RevitAPI v{ver} loaded";
                    check.Extra["Version"] = ver;
                    check.Extra["Location"] = revitAsm.Location;
                }
                else
                {
                    check.Status = DiagStatus.Warning;
                    check.Detail = "RevitAPI not loaded (expected if running outside Revit)";
                }
            }
            catch (Exception ex)
            {
                check.Status = DiagStatus.Error;
                check.Detail = $"RevitAPI check failed: {ex.Message}";
            }
            return check;
        }

        // ── 4. WebView2 Runtime ──────────────────────────────

        private static DiagCheck CheckWebView2Runtime()
        {
            var check = new DiagCheck { Name = "WebView2Runtime" };
            try
            {
                string wv2Version = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .GetAvailableBrowserVersionString();

                if (!string.IsNullOrEmpty(wv2Version))
                {
                    check.Status = DiagStatus.OK;
                    check.Detail = $"WebView2 Runtime v{wv2Version}";
                    check.Extra["BrowserVersion"] = wv2Version;
                }
                else
                {
                    check.Status = DiagStatus.Error;
                    check.Detail = "WebView2 Runtime not installed";
                }
            }
            catch (Exception ex)
            {
                check.Status = DiagStatus.Warning;
                check.Detail = $"WebView2 Runtime check failed: {ex.Message}";
            }
            return check;
        }

        // ── 5. Config Files ─────────────────────────────────

        private static DiagCheck CheckConfigFiles()
        {
            var check = new DiagCheck { Name = "ConfigFiles" };
            try
            {
                string baseDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? "";

                var requiredFiles = new[]
                {
                    Path.Combine("Config", "rag_config.json"),
                    Path.Combine("Config", "i18n", "en.json"),
                    Path.Combine("Config", "i18n", "kr.json"),
                    Path.Combine("wwwroot", "index.html"),
                };

                var missing = new List<string>();
                var found = new List<string>();

                foreach (var rel in requiredFiles)
                {
                    string full = Path.Combine(baseDir, rel);
                    if (File.Exists(full))
                        found.Add(rel);
                    else
                        missing.Add(rel);
                }

                if (missing.Count == 0)
                {
                    check.Status = DiagStatus.OK;
                    check.Detail = $"All {found.Count} config files present";
                }
                else
                {
                    check.Status = DiagStatus.Warning;
                    check.Detail = $"Missing: {string.Join(", ", missing)}";
                }

                check.Extra["Found"] = found.Count;
                check.Extra["Missing"] = missing;
            }
            catch (Exception ex)
            {
                check.Status = DiagStatus.Error;
                check.Detail = $"Config check failed: {ex.Message}";
            }
            return check;
        }

        // ── Logging ──────────────────────────────────────────

        private static void LogReport(DiagnosticsReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== BIBIM Diagnostics v{report.AppVersion} ===");
            sb.AppendLine($"Runtime: {report.Runtime}");
            sb.AppendLine($"Time: {report.Timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();

            foreach (var c in report.Checks)
            {
                string icon = c.Status == DiagStatus.OK ? "OK" :
                              c.Status == DiagStatus.Warning ? "WARN" : "FAIL";
                sb.AppendLine($"  [{icon}] {c.Name}: {c.Detail}");
            }

            Logger.Log("Diagnostics", sb.ToString());
        }

        /// <summary>
        /// Format report as plain text for debug output / log file.
        /// </summary>
        public static string FormatAsText(DiagnosticsReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"BIBIM Diagnostics Report — v{report.AppVersion}");
            sb.AppendLine($"Runtime: {report.Runtime}");
            sb.AppendLine($"Generated: {report.Timestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('-', 50));

            int ok = 0, warn = 0, fail = 0;
            foreach (var c in report.Checks)
            {
                string icon = c.Status == DiagStatus.OK ? "[OK  ]" :
                              c.Status == DiagStatus.Warning ? "[WARN]" : "[FAIL]";
                sb.AppendLine($"{icon} {c.Name}");
                sb.AppendLine($"       {c.Detail}");

                if (c.Status == DiagStatus.OK) ok++;
                else if (c.Status == DiagStatus.Warning) warn++;
                else fail++;
            }

            sb.AppendLine(new string('-', 50));
            sb.AppendLine($"Summary: {ok} OK, {warn} warnings, {fail} failures");
            return sb.ToString();
        }
    }

    // ── Models ───────────────────────────────────────────

    public class DiagnosticsReport
    {
        public DateTime Timestamp { get; set; }
        public string AppVersion { get; set; }
        public string Runtime { get; set; }
        public List<DiagCheck> Checks { get; set; } = new List<DiagCheck>();

        public bool HasErrors => Checks.Any(c => c.Status == DiagStatus.Error);
        public bool HasWarnings => Checks.Any(c => c.Status == DiagStatus.Warning);
    }

    public class DiagCheck
    {
        public string Name { get; set; }
        public DiagStatus Status { get; set; } = DiagStatus.OK;
        public string Detail { get; set; }
        public Dictionary<string, object> Extra { get; set; } = new Dictionary<string, object>();
    }

    public enum DiagStatus
    {
        OK,
        Warning,
        Error
    }
}
