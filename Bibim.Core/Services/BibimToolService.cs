// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Tool executor for the LLM tool-use API.
    /// Wraps RevitContextProvider, LocalRevitRagService, and Roslyn services
    /// as callable tools that the model can invoke on demand.
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

        // Saved Code Library snippets for search_code_library (null = tool unavailable).
        private readonly Func<IEnumerable<CodeSnippet>> _librarySource;

        public BibimToolService(
            RevitContextProvider contextProvider,
            RoslynCompilerService compiler,
            RoslynAnalyzerService analyzer,
            Func<Func<string>, Task<string>> mainThreadInvoker = null,
            Func<IEnumerable<CodeSnippet>> librarySource = null)
        {
            _contextProvider = contextProvider;
            _compiler = compiler;
            _analyzer = analyzer;
            _mainThreadInvoker = mainThreadInvoker;
            _librarySource = librarySource;
        }

        // ─────────────────────────────────────────────
        // Tool Definitions (JSON Schema for Claude API)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Build the full tool definition array (the 7 codegen tools). Equivalent to
        /// <c>GetToolDefinitions(null)</c>.
        /// </summary>
        public static JArray GetToolDefinitions() => GetToolDefinitions(null);

        /// <summary>
        /// Build the tool definition array, filtering out Revit-context tools that
        /// the current task is unlikely to need. <c>search_revit_api</c> and
        /// <c>run_roslyn_check</c> are always included; the 5 Revit-context tools
        /// (view / selection / parameters / family / levels) are included only when
        /// the hint text mentions matching keywords. Pass a null/empty hint to keep
        /// the full set.
        /// </summary>
        /// <param name="includeReadTools">READ tasks also get the typed read tools
        /// (count_elements / list_elements) so simple questions are answered straight
        /// from the model without generating and running code.</param>
        /// <param name="includeCodeLibrary">Offer search_code_library (only when the user
        /// has saved snippets — an empty library would just waste a turn).</param>
        public static JArray GetToolDefinitions(string contextHint, bool includeReadTools = false,
            bool includeCodeLibrary = false)
        {
            bool fullSet = string.IsNullOrWhiteSpace(contextHint);
            bool includeView      = fullSet || HintMatches(contextHint, _viewMarkers);
            bool includeSelection = fullSet || HintMatches(contextHint, _selectionMarkers);
            bool includeParams    = fullSet || HintMatches(contextHint, _parameterMarkers);
            bool includeFamily    = fullSet || HintMatches(contextHint, _familyMarkers);
            bool includeLevels    = fullSet || HintMatches(contextHint, _levelMarkers);

            var arr = new JArray { ToolSearchRevitApi(), ToolRunRoslynCheck() };
            if (includeView) arr.Add(ToolGetViewInfo());
            if (includeSelection) arr.Add(ToolGetSelectedElements());
            if (includeParams) arr.Add(ToolGetElementParameters());
            if (includeFamily) arr.Add(ToolGetFamilyTypes());
            if (includeLevels) arr.Add(ToolGetProjectLevels());
            if (includeReadTools)
            {
                arr.Add(ToolCountElements());
                arr.Add(ToolListElements());
            }
            if (includeCodeLibrary)
                arr.Add(ToolSearchCodeLibrary());
            return arr;
        }

        // Per-tool hint keyword sets. Matched case-insensitively, substring-style.
        // KR + EN markers; deliberately broad — false positives just include a tool
        // that the model may ignore, while false negatives lose tool access entirely.
        private static readonly string[] _viewMarkers = {
            "view", "sheet", "section", "elevation", "plan", "RCP", "3d view", "viewport",
            "뷰", "시트", "단면", "입면", "평면", "천장", "도면", "활성"
        };
        private static readonly string[] _selectionMarkers = {
            "selected", "selection", "선택", "highlight"
        };
        private static readonly string[] _parameterMarkers = {
            "parameter", "param", "property", "속성", "파라미터", "value", "값", "shared parameter"
        };
        private static readonly string[] _familyMarkers = {
            "family", "type", "instance", "place", "패밀리", "타입", "인스턴스",
            "door", "window", "furniture", "column", "beam", "wall type", "floor type",
            "문", "창", "기둥", "보", "가구"
        };
        private static readonly string[] _levelMarkers = {
            "level", "elevation", "story", "floor", "level 1", "level 2", "ground",
            "레벨", "층", "지상", "지하", "1층", "2층", "3층", "4층", "5층"
        };

        private static bool HintMatches(string hint, string[] markers)
        {
            for (int i = 0; i < markers.Length; i++)
                if (hint.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // Tool definition factories — split out so GetToolDefinitions can compose them.

        private static JObject ToolSearchRevitApi() => new JObject
        {
            // search_revit_api: local BM25 RAG — indexes RevitAPI.xml from Revit installation.
            // Index is built lazily on first call; subsequent calls use the cached BM25 engine.
            ["name"] = "search_revit_api",
            ["description"] = "Search the local Revit API documentation index (built from RevitAPI.xml). " +
                "Use this to verify class names, method signatures, constructor parameters, and property names " +
                "before writing code. Call with a concise English query like the class or method name you need. " +
                "STRICT LIMIT: maximum 2 calls per request.",
            ["input_schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "English search query, e.g. 'PDFExportOptions', 'RevisionCloud create', 'FilteredElementCollector OfClass'"
                    }
                },
                ["required"] = new JArray { "query" }
            }
        };

        private static JObject ToolRunRoslynCheck() => new JObject
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
        };

        private static JObject ToolGetViewInfo() => new JObject
        {
            ["name"] = "get_view_info",
            ["description"] = "Get information about the currently active Revit view (name, type, scale, visible categories). Use when the task involves view-specific operations or needs to know the current view context.",
            ["input_schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray()
            }
        };

        private static JObject ToolGetSelectedElements() => new JObject
        {
            ["name"] = "get_selected_elements",
            ["description"] = "Get the currently selected elements in the Revit model (category, type, ElementId, parameters, location). Use when the user refers to 'selected elements', 'these elements', or 'selection'.",
            ["input_schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray()
            }
        };

        private static JObject ToolGetElementParameters() => new JObject
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
        };

        private static JObject ToolGetFamilyTypes() => new JObject
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
        };

        private static JObject ToolGetProjectLevels() => new JObject
        {
            ["name"] = "get_project_levels",
            ["description"] = "Get all levels defined in the Revit project with their elevations and ElementIds. Use when code needs to reference or iterate over project levels.",
            ["input_schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject(),
                ["required"] = new JArray()
            }
        };

        private static JObject ScopeProperty() => new JObject
        {
            ["type"] = "string",
            ["enum"] = new JArray { "model", "active_view", "selection" },
            ["description"] = "Where to look: the whole model (default), only elements visible in the active view, or only the current Revit selection."
        };

        private static JObject ToolCountElements() => new JObject
        {
            ["name"] = "count_elements",
            ["description"] = "Exact count of model elements in ONE category, optionally limited to the active view, the current selection, or one level, with a per-type breakdown. Use for 'how many ...' questions instead of writing code.",
            ["input_schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["category"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "English category name (e.g. 'Walls', 'Doors', 'Rooms', 'Structural Columns') or a BuiltInCategory such as 'OST_Walls'."
                    },
                    ["scope"] = ScopeProperty(),
                    ["level"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional exact level name (e.g. 'L2'); filters by each element's base/reference level."
                    }
                },
                ["required"] = new JArray { "category" }
            }
        };

        private static JObject ToolListElements() => new JObject
        {
            ["name"] = "list_elements",
            ["description"] = "List elements of ONE category (Id, type, level, plus up to 5 named parameters, values in project units). Returns at most 100 rows and says explicitly when the list is truncated — never compute totals from a truncated list.",
            ["input_schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["category"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "English category name or BuiltInCategory (see count_elements)."
                    },
                    ["scope"] = ScopeProperty(),
                    ["level"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional exact level name."
                    },
                    ["parameters"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "string" },
                        ["description"] = "Up to 5 parameter names to include (instance first, then type), e.g. ['Mark', 'Comments', 'Fire Rating']."
                    },
                    ["limit"] = new JObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum rows (default 50, max 100)."
                    }
                },
                ["required"] = new JArray { "category" }
            }
        };

        private static JObject ToolSearchCodeLibrary() => new JObject
        {
            ["name"] = "search_code_library",
            ["description"] = "Search the user's Code Library — C# snippets BIBIM generated earlier that the user kept. " +
                "Call once at the start of a task that resembles earlier work; reuse or adapt a close match instead of " +
                "writing from scratch, and still verify it with run_roslyn_check.",
            ["input_schema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["query"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Keywords from the task in the user's language and/or English, e.g. '문 번호 변경 door mark'."
                    }
                },
                ["required"] = new JArray { "query" }
            }
        };

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

                    case "search_code_library":
                    {
                        string query = input["query"]?.ToString() ?? "";
                        if (_librarySource == null) return "[Tool Error] Code Library is not available.";
                        var matches = CodeLibrarySearch.Search(_librarySource() ?? Enumerable.Empty<CodeSnippet>(),
                            query, ConfigService.GetEffectiveRevitVersion());
                        return CodeLibrarySearch.Format(matches, query);
                    }

                    case "count_elements":
                    {
                        string category = input["category"]?.ToString();
                        string scope = input["scope"]?.ToString();
                        string level = input["level"]?.ToString();
                        return await OnMainAsync(() => _contextProvider?.CountElements(category, scope, level)
                            ?? "[Error] RevitContextProvider not initialized.");
                    }

                    case "list_elements":
                    {
                        string category = input["category"]?.ToString();
                        string scope = input["scope"]?.ToString();
                        string level = input["level"]?.ToString();
                        var parameters = (input["parameters"] as JArray)?
                            .Select(t => t?.ToString())
                            .Where(t => !string.IsNullOrWhiteSpace(t))
                            .ToList() ?? new List<string>();
                        int limit = 50;
                        if (input["limit"] != null && int.TryParse(input["limit"].ToString(), out int parsed))
                            limit = parsed;
                        return await OnMainAsync(() => _contextProvider?.ListElements(category, scope, level, parameters, limit)
                            ?? "[Error] RevitContextProvider not initialized.");
                    }

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

            // Use local BM25 RAG (indexes RevitAPI.xml from Revit installation).
            var result = await LocalRevitRagService.FetchAsync(query, revitVersion, ct);

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
            CompilationResult compileResult = null;
            if (_analyzer != null)
            {
                var analyzerReport = _analyzer.Analyze(code);
                var fixResult = _analyzer.ApplyAutoFixes(code);
                if (fixResult.HasChanges)
                {
                    // Auto-fixes are text-based (".IntegerValue" also hits WorksetId, which has
                    // no Value): keep them only when they turn a failing compile into a pass —
                    // otherwise the model is told its correct code "fails".
                    var original = _compiler.Compile(code);
                    compileResult = original;
                    if (!original.Success)
                    {
                        var fixedCompile = _compiler.Compile(fixResult.FixedCode);
                        if (fixedCompile.Success)
                        {
                            codeToCompile = fixResult.FixedCode;
                            compileResult = fixedCompile;
                            sb.AppendLine($"AUTO-FIXES APPLIED: {string.Join(", ", fixResult.AppliedFixes)}");
                        }
                    }
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

            // 2. Roslyn compile (already done above when an auto-fix was considered)
            compileResult = compileResult ?? _compiler.Compile(codeToCompile);

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
