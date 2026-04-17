// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Tool executor for Claude Tool Use API.
    /// Wraps RevitContextProvider, GeminiRagService, and Roslyn services
    /// as callable tools that Claude can invoke on demand.
    ///
    /// Threading: RevitContext tools (get_view_info, get_selected_elements, etc.)
    /// require the Revit main thread. Pass mainThreadInvoker to dispatch safely.
    /// Non-Revit tools (run_roslyn_check, search_revit_api) run on any thread.
    /// </summary>
    public class BibimToolService
    {
        private readonly RevitContextProvider _contextProvider;
        private readonly RoslynCompilerService _compiler;
        private readonly RoslynAnalyzerService _analyzer;

        // Dispatches a Func<string> to the Revit/WPF main thread asynchronously.
        // If null, the function is called directly (caller is already on main thread).
        private readonly Func<Func<string>, Task<string>> _mainThreadInvoker;

        public BibimToolService(
            RevitContextProvider contextProvider,
            RoslynCompilerService compiler,
            RoslynAnalyzerService analyzer,
            Func<Func<string>, Task<string>> mainThreadInvoker = null)
        {
            _contextProvider = contextProvider;
            _compiler = compiler;
            _analyzer = analyzer;
            _mainThreadInvoker = mainThreadInvoker;
        }

        // ─────────────────────────────────────────────
        // Tool Definitions (JSON Schema for Claude API)
        // ─────────────────────────────────────────────

        public static JArray GetToolDefinitions()
        {
            return new JArray
            {
                // search_revit_api (RAG) tool disabled — RAG store not yet available in OSS release.
                // GeminiRagService code retained for future re-enablement.
                new JObject
                {
                    ["name"] = "run_roslyn_check",
                    ["description"] = "Compile C# code with Roslyn and run BIBIM001-005 static analyzers. Returns compilation errors, warnings, and any auto-applied fixes. ALWAYS call this after writing code to verify it compiles before returning the final answer.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["code"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "The C# code to compile and analyze"
                            }
                        },
                        ["required"] = new JArray { "code" }
                    }
                },
                new JObject
                {
                    ["name"] = "get_view_info",
                    ["description"] = "Get information about the currently active Revit view (name, type, scale, visible categories). Use when the task involves view-specific operations or needs to know the current view context.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject(),
                        ["required"] = new JArray()
                    }
                },
                new JObject
                {
                    ["name"] = "get_selected_elements",
                    ["description"] = "Get the currently selected elements in the Revit model (category, type, ElementId, parameters, location). Use when the user refers to 'selected elements', 'these elements', or 'selection'.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject(),
                        ["required"] = new JArray()
                    }
                },
                new JObject
                {
                    ["name"] = "get_element_parameters",
                    ["description"] = "Get available instance and type parameters for elements of a specific Revit category. Use when you need to know what parameters a wall, floor, door, etc. exposes.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["category"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Revit category name (e.g. 'Walls', 'Doors', 'Floors', 'Structural Columns')"
                            }
                        },
                        ["required"] = new JArray { "category" }
                    }
                },
                new JObject
                {
                    ["name"] = "get_family_types",
                    ["description"] = "Get family types available in the project for a given category. Use when code needs to place or reference specific family instances.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["category"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Category to filter by (e.g. 'Doors', 'Furniture'). Leave empty for all families."
                            }
                        },
                        ["required"] = new JArray()
                    }
                },
                new JObject
                {
                    ["name"] = "get_project_levels",
                    ["description"] = "Get all levels defined in the Revit project with their elevations and ElementIds. Use when code needs to reference or iterate over project levels.",
                    ["input_schema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject(),
                        ["required"] = new JArray()
                    }
                }
            };
        }

        // ─────────────────────────────────────────────
        // Tool Executor
        // ─────────────────────────────────────────────

        /// <summary>
        /// Execute a tool by name with JSON input. Called by LlmOrchestrationService
        /// when Claude returns a tool_use block.
        /// </summary>
        public async Task<string> ExecuteAsync(
            string toolName, string inputJson, CancellationToken ct)
        {
            try
            {
                var input = string.IsNullOrWhiteSpace(inputJson)
                    ? new JObject()
                    : JObject.Parse(inputJson);

                Logger.Log("BibimTool", $"Executing: {toolName}");

                switch (toolName)
                {
                    case "search_revit_api":
                        return await SearchRevitApiAsync(input, ct);

                    case "run_roslyn_check":
                        return RunRoslynCheck(input);

                    case "get_view_info":
                        return await OnMainAsync(() => _contextProvider?.ResolveContextTag("@view")
                            ?? "[Error] RevitContextProvider not initialized.");

                    case "get_selected_elements":
                        return await OnMainAsync(() => _contextProvider?.ResolveContextTag("@selection")
                            ?? "[Error] RevitContextProvider not initialized.");

                    case "get_element_parameters":
                        string paramCat = input["category"]?.ToString() ?? "";
                        return await OnMainAsync(() => _contextProvider?.ResolveContextTag($"@parameters:{paramCat}")
                            ?? "[Error] RevitContextProvider not initialized.");

                    case "get_family_types":
                        string famCat = input["category"]?.ToString() ?? "";
                        return await OnMainAsync(() => _contextProvider?.ResolveContextTag($"@family:{famCat}")
                            ?? "[Error] RevitContextProvider not initialized.");

                    case "get_project_levels":
                        return await OnMainAsync(() => _contextProvider?.ResolveContextTag("@levels")
                            ?? "[Error] RevitContextProvider not initialized.");

                    default:
                        return $"[Tool Error] Unknown tool: {toolName}";
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"BibimTool.{toolName}", ex);
                return $"[Tool Error] {ex.GetType().Name}: {ex.Message}";
            }
        }

        // ─────────────────────────────────────────────
        // Tool Implementations
        // ─────────────────────────────────────────────

        private async Task<string> SearchRevitApiAsync(JObject input, CancellationToken ct)
        {
            string query = input["query"]?.ToString();
            if (string.IsNullOrWhiteSpace(query))
                return "[Tool Error] 'query' parameter is required.";

            string revitVersion = ConfigService.GetEffectiveRevitVersion();
            var result = await GeminiRagService.FetchAsync(query, revitVersion, ct);

            // Prepend timing header — auto-recorded in tool_turn_XX_search_revit_api_result.txt
            string header = $"[RAG: {result.ElapsedMs}ms, {result.Status}]\n";

            if (result.HasContext)
                return header + result.ContextText;

            return header + $"[No API documentation found for: {query}]"
                + (result.ErrorSummary != null ? $" — {result.ErrorSummary}" : "");
        }

        private string RunRoslynCheck(JObject input)
        {
            string code = input["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
                return "[Tool Error] 'code' parameter is required.";

            var sb = new StringBuilder();

            // 1. BIBIM001-005 static analysis + auto-fix
            string codeToCompile = code;
            if (_analyzer != null)
            {
                var analyzerReport = _analyzer.Analyze(code);
                var fixResult = _analyzer.ApplyAutoFixes(code);
                if (fixResult.HasChanges)
                {
                    codeToCompile = fixResult.FixedCode;
                    sb.AppendLine($"AUTO-FIXES APPLIED: {string.Join(", ", fixResult.AppliedFixes)}");
                }
                if (fixResult.SuggestedFixes?.Count > 0)
                {
                    sb.AppendLine("SUGGESTED FIXES:");
                    foreach (var fix in fixResult.SuggestedFixes)
                        sb.AppendLine($"  - {fix}");
                }
                if (analyzerReport.HasErrors || analyzerReport.HasWarnings)
                    sb.AppendLine($"ANALYZER: {analyzerReport.FormatSummary()}");
            }

            // 2. Roslyn compile
            var compileResult = _compiler.Compile(codeToCompile);

            if (compileResult.Success)
            {
                sb.Insert(0, "COMPILE: SUCCESS\n");
                // If auto-fixes were applied, return the fixed code so Claude can use it
                if (!string.Equals(code, codeToCompile, StringComparison.Ordinal))
                {
                    sb.AppendLine("FIXED CODE (use this version):");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(codeToCompile);
                    sb.AppendLine("```");
                }
            }
            else
            {
                sb.Insert(0, "COMPILE: FAILED\n");
                sb.AppendLine("ERRORS:");
                sb.AppendLine(compileResult.ErrorSummary);
            }

            return sb.ToString().TrimEnd();
        }

        // ─────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────

        private Task<string> OnMainAsync(Func<string> action)
        {
            if (_mainThreadInvoker != null)
                return _mainThreadInvoker(action);
            return Task.FromResult(action());
        }
    }
}
