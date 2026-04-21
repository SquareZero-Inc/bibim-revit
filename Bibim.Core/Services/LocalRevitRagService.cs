// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Local BM25-based RAG service — drop-in replacement for GeminiRagService.
    ///
    /// Design decisions:
    ///   • Data source: RevitAPI.xml shipped with every Revit installation.
    ///     Same content as the official Autodesk API docs (the web docs render from this XML).
    ///   • Index strategy: Lazy-load on first call (Option B).
    ///     Revit startup is unaffected. The 1–3s build time is absorbed into
    ///     the first Claude API round-trip (~3–10s), invisible to the user.
    ///   • Chunk unit: one chunk per class. Includes class summary + all
    ///     member signatures + descriptions. Giant classes (BuiltInParameter,
    ///     FilteredElementCollector) are split into domain sub-groups.
    ///   • Search: BM25 (keyword). Handles exact API name lookups perfectly.
    ///     Phase 2 (OpenAI semantic layer) can be layered on top later.
    ///   • No external API needed — works with Claude API key only.
    ///
    /// Debug logging:
    ///   All RAG activity is logged via Logger.Log("LocalRAG", ...) so the
    ///   developer can trace index build, query, hit/miss, and chunk previews
    ///   in %APPDATA%\BIBIM\logs\bibim_debug.txt.
    /// </summary>
    internal static class LocalRevitRagService
    {
        // Lazy-loaded index — built on first call, cached for the process lifetime.
        private static BM25Engine _engine;
        private static string _indexedXmlPath;
        private static readonly object _lock = new object();

        // XML files to index (relative to RevitAPI.dll directory)
        private static readonly string[] XmlFileNames =
        {
            "RevitAPI.xml",
            "RevitAPIUI.xml",
            "RevitAPIIFC.xml"
        };

        // Top-k chunks to return per query
        private const int TopK = 5;

        // Max characters per chunk sent to Claude (~800 tokens each)
        private const int MaxChunkDisplayChars = 3000;

        /// <summary>
        /// Fetch relevant Revit API documentation for the given query.
        /// Returns RagFetchResult with the same shape as GeminiRagService.FetchAsync().
        /// </summary>
        public static Task<RagFetchResult> FetchAsync(
            string query,
            string revitVersion,
            CancellationToken ct = default)
        {
            // Run on thread pool — XML parsing is CPU-bound
            return Task.Run(() => FetchInternal(query, revitVersion), ct);
        }

        private static RagFetchResult FetchInternal(string query, string revitVersion)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                var engine = GetOrBuildEngine(revitVersion);

                if (engine == null)
                {
                    Logger.Log("LocalRAG", "[MISS] engine=null (RevitAPI.xml not found)");
                    return new RagFetchResult
                    {
                        Status = "no_index",
                        ElapsedMs = sw.ElapsedMilliseconds,
                        ErrorSummary = "RevitAPI.xml not found in Revit installation directory."
                    };
                }

                var hits = engine.Search(query, TopK);
                sw.Stop();

                if (hits.Count == 0)
                {
                    Logger.Log("LocalRAG",
                        $"[MISS] query=\"{Clip(query)}\" ms={sw.ElapsedMilliseconds}");
                    return new RagFetchResult
                    {
                        Status = "no_match",
                        ElapsedMs = sw.ElapsedMilliseconds
                    };
                }

                // Build context text from top hits
                var sb = new StringBuilder();
                for (int i = 0; i < hits.Count; i++)
                {
                    var chunk = hits[i];
                    sb.AppendLine($"--- [{i + 1}] {chunk.Namespace}.{chunk.ClassName} ---");
                    sb.AppendLine(chunk.DisplayText);
                    sb.AppendLine();
                }

                string contextText = sb.ToString();

                // Debug: log query + top hit preview
                Logger.Log("LocalRAG",
                    $"[HIT] query=\"{Clip(query)}\" hits={hits.Count} " +
                    $"top=\"{hits[0].ClassName}\" ms={sw.ElapsedMilliseconds} " +
                    $"preview=\"{Clip(hits[0].DisplayText, 120)}\"");

                return new RagFetchResult
                {
                    Status = "hit",
                    ContextText = contextText,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.LogError("LocalRAG.FetchInternal", ex);
                return new RagFetchResult
                {
                    Status = "error",
                    ElapsedMs = sw.ElapsedMilliseconds,
                    ErrorSummary = ex.Message
                };
            }
        }

        // ─────────────────────────────────────────────
        // Index build (lazy, cached per process)
        // ─────────────────────────────────────────────

        private static BM25Engine GetOrBuildEngine(string revitVersion)
        {
            lock (_lock)
            {
                string xmlDir = ResolveRevitXmlDirectory();

                if (string.IsNullOrEmpty(xmlDir))
                {
                    Logger.Log("LocalRAG", "[INDEX] RevitAPI.xml directory not resolved");
                    return null;
                }

                // If already built for this XML location, reuse
                if (_engine != null && string.Equals(_indexedXmlPath, xmlDir, StringComparison.OrdinalIgnoreCase))
                    return _engine;

                Logger.Log("LocalRAG", $"[INDEX_BUILD_START] dir=\"{xmlDir}\" revit={revitVersion}");
                var buildSw = Stopwatch.StartNew();

                var chunks = BuildChunks(xmlDir);
                buildSw.Stop();

                if (chunks.Count == 0)
                {
                    Logger.Log("LocalRAG", $"[INDEX_BUILD_FAILED] no chunks produced ms={buildSw.ElapsedMilliseconds}");
                    return null;
                }

                _engine = new BM25Engine(chunks);
                _indexedXmlPath = xmlDir;

                Logger.Log("LocalRAG",
                    $"[INDEX_BUILD_DONE] chunks={chunks.Count} bm25_tokens={_engine.ChunkCount} " +
                    $"ms={buildSw.ElapsedMilliseconds} dir=\"{xmlDir}\"");

                return _engine;
            }
        }

        // ─────────────────────────────────────────────
        // XML → RagChunk conversion
        // ─────────────────────────────────────────────

        private static List<RagChunk> BuildChunks(string xmlDir)
        {
            // Collect all members across all XML files
            var allMembers = new List<XElement>();
            foreach (string fileName in XmlFileNames)
            {
                string path = Path.Combine(xmlDir, fileName);
                if (!File.Exists(path)) continue;

                try
                {
                    var doc = XDocument.Load(path);
                    allMembers.AddRange(doc.Descendants("member"));
                    Logger.Log("LocalRAG", $"[XML_LOADED] file={fileName} members={allMembers.Count}");
                }
                catch (Exception ex)
                {
                    Logger.Log("LocalRAG", $"[XML_LOAD_ERROR] file={fileName} err={ex.Message}");
                }
            }

            if (allMembers.Count == 0) return new List<RagChunk>();

            // Group by class name
            var byClass = new Dictionary<string, ClassAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (var member in allMembers)
            {
                string nameAttr = member.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(nameAttr)) continue;

                if (!TryParseRevitMember(nameAttr, out string ns, out string className, out string memberName))
                    continue;

                string key = ns + "." + className;
                if (!byClass.TryGetValue(key, out var acc))
                {
                    acc = new ClassAccumulator { Namespace = ns, ClassName = className };
                    byClass[key] = acc;
                }

                string summary = NormalizeSummary(member.Element("summary")?.Value);
                string remarks = NormalizeSummary(member.Element("remarks")?.Value);

                if (string.IsNullOrEmpty(memberName))
                {
                    // Type-level summary
                    acc.ClassSummary = summary;
                    if (!string.IsNullOrEmpty(remarks))
                        acc.ClassRemarks = remarks;
                }
                else
                {
                    // Member: collect parameters
                    var paramDescs = new List<string>();
                    foreach (var p in member.Elements("param"))
                    {
                        string pname = p.Attribute("name")?.Value;
                        string pdesc = NormalizeSummary(p.Value);
                        if (!string.IsNullOrEmpty(pname) && !string.IsNullOrEmpty(pdesc))
                            paramDescs.Add($"  {pname}: {pdesc}");
                    }

                    string returnDesc = NormalizeSummary(member.Element("returns")?.Value);

                    acc.Members.Add(new MemberEntry
                    {
                        MemberName = memberName,
                        Summary = summary,
                        Remarks = remarks,
                        ParamDescriptions = paramDescs,
                        Returns = returnDesc,
                        MemberTypePrefix = nameAttr[0]  // T/M/P/F/E
                    });
                }
            }

            // Convert accumulators to chunks
            var chunks = new List<RagChunk>(byClass.Count);
            foreach (var kv in byClass.Values)
            {
                // Split giant BuiltInParameter into domain sub-groups
                if (kv.ClassName == "BuiltInParameter" && kv.Members.Count > 200)
                {
                    chunks.AddRange(SplitBuiltInParameterChunks(kv));
                    continue;
                }

                var chunk = AccumulatorToChunk(kv);
                if (chunk != null) chunks.Add(chunk);
            }

            return chunks;
        }

        private static RagChunk AccumulatorToChunk(ClassAccumulator acc)
        {
            if (acc.Members.Count == 0 && string.IsNullOrEmpty(acc.ClassSummary))
                return null;

            var sb = new StringBuilder();
            sb.AppendLine($"[Class: {acc.ClassName}]");
            sb.AppendLine($"Namespace: {acc.Namespace}");

            if (!string.IsNullOrEmpty(acc.ClassSummary))
                sb.AppendLine($"Summary: {acc.ClassSummary}");
            if (!string.IsNullOrEmpty(acc.ClassRemarks))
                sb.AppendLine($"Remarks: {acc.ClassRemarks}");

            if (acc.Members.Count > 0)
            {
                sb.AppendLine();
                foreach (var m in acc.Members.Take(60)) // cap at 60 members per chunk
                {
                    string prefix = m.MemberTypePrefix == 'M' ? "" :
                                    m.MemberTypePrefix == 'P' ? "[Property] " :
                                    m.MemberTypePrefix == 'F' ? "[Field] " :
                                    m.MemberTypePrefix == 'E' ? "[Event] " : "";

                    sb.Append($"{prefix}{m.MemberName}");
                    if (!string.IsNullOrEmpty(m.Returns)) sb.Append($" -> {m.Returns}");
                    sb.AppendLine();
                    if (!string.IsNullOrEmpty(m.Summary)) sb.AppendLine($"  {m.Summary}");
                    foreach (var pd in m.ParamDescriptions) sb.AppendLine(pd);
                    if (!string.IsNullOrEmpty(m.Remarks)) sb.AppendLine($"  Note: {m.Remarks}");
                }
            }

            string displayText = sb.ToString();
            if (displayText.Length > MaxChunkDisplayChars)
                displayText = displayText.Substring(0, MaxChunkDisplayChars) + "\n[...truncated]";

            // IndexText = display + extra tags for BM25 coverage
            string indexText = displayText + " " + acc.ClassName + " " + acc.Namespace;

            return new RagChunk
            {
                ClassName = acc.ClassName,
                Namespace = acc.Namespace,
                DisplayText = displayText,
                IndexText = indexText
            };
        }

        private static IEnumerable<RagChunk> SplitBuiltInParameterChunks(ClassAccumulator acc)
        {
            // Group BuiltInParameter fields by domain prefix (WALL_, LEVEL_, MEP_, etc.)
            var domainGroups = new Dictionary<string, List<MemberEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in acc.Members)
            {
                string domain = ExtractBuiltInParamDomain(m.MemberName);
                if (!domainGroups.TryGetValue(domain, out var list))
                {
                    list = new List<MemberEntry>();
                    domainGroups[domain] = list;
                }
                list.Add(m);
            }

            foreach (var kv in domainGroups)
            {
                var sub = new ClassAccumulator
                {
                    Namespace = acc.Namespace,
                    ClassName = $"BuiltInParameter.{kv.Key}",
                    ClassSummary = $"BuiltInParameter enum values for domain: {kv.Key}",
                    Members = kv.Value
                };
                var chunk = AccumulatorToChunk(sub);
                if (chunk != null) yield return chunk;
            }
        }

        private static string ExtractBuiltInParamDomain(string name)
        {
            if (string.IsNullOrEmpty(name)) return "OTHER";
            // Take the first underscore-delimited segment
            int idx = name.IndexOf('_');
            return idx > 0 ? name.Substring(0, idx) : name;
        }

        // ─────────────────────────────────────────────
        // XML parsing helpers
        // ─────────────────────────────────────────────

        private static bool TryParseRevitMember(
            string nameAttr,
            out string ns,
            out string className,
            out string memberName)
        {
            ns = "";
            className = "";
            memberName = "";

            if (nameAttr.Length < 3) return false;

            // Strip "T:", "M:", "P:", "F:", "E:" prefix
            string fullName = nameAttr.Length > 2 && nameAttr[1] == ':' ? nameAttr.Substring(2) : nameAttr;

            // Remove method parameter signature
            int parenIdx = fullName.IndexOf('(');
            if (parenIdx >= 0) fullName = fullName.Substring(0, parenIdx);

            // Must be in Autodesk.Revit namespace
            const string prefix = "Autodesk.Revit.";
            int prefixIdx = fullName.IndexOf(prefix, StringComparison.Ordinal);
            if (prefixIdx < 0) return false;

            string after = fullName.Substring(prefixIdx);  // e.g. "Autodesk.Revit.DB.Document.Export"

            // Split into segments
            string[] parts = after.Split('.');
            // parts[0]="Autodesk", parts[1]="Revit", parts[2]="DB" or "DB.Mechanical" sub-ns, ...
            // We want namespace = everything before the class, class = last capitalized segment

            if (parts.Length < 4) return false;  // need at least Autodesk.Revit.DB.ClassName

            // Find the class segment: the first segment after the known sub-namespaces
            // Strategy: walk from index 2 forward; the class starts at the first PascalCase segment
            // that is NOT a known sub-namespace word
            var knownSubNs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "DB", "UI", "Creation", "Structure", "Mechanical", "Plumbing",
                "Electrical", "Architecture", "Analysis", "IFC", "Macros",
                "Visual", "ApplicationServices", "Parameters", "Exceptions"
            };

            int classIdx = -1;
            for (int i = 2; i < parts.Length; i++)
            {
                if (!knownSubNs.Contains(parts[i]))
                {
                    classIdx = i;
                    break;
                }
            }

            if (classIdx < 0) return false;

            ns = string.Join(".", parts, 0, classIdx);
            className = parts[classIdx];
            memberName = classIdx + 1 < parts.Length ? parts[classIdx + 1] : "";

            // Strip property accessor prefixes
            if (memberName.StartsWith("get_", StringComparison.Ordinal))
                memberName = memberName.Substring(4);
            else if (memberName.StartsWith("set_", StringComparison.Ordinal))
                memberName = memberName.Substring(4);

            return !string.IsNullOrWhiteSpace(className);
        }

        private static string ResolveRevitXmlDirectory()
        {
            try
            {
                // Find RevitAPI.dll via loaded assemblies (we're running inside Revit)
                var revitApiAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(
                        a.GetName().Name, "RevitAPI", StringComparison.OrdinalIgnoreCase));

                if (revitApiAssembly != null)
                {
                    string dir = Path.GetDirectoryName(revitApiAssembly.Location);
                    if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "RevitAPI.xml")))
                        return dir;
                }

                // Fallback: search common install paths
                string[] commonYears = { "2026", "2025", "2024", "2027", "2023", "2022" };
                foreach (string year in commonYears)
                {
                    string candidate = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Autodesk", $"Revit {year}");
                    if (File.Exists(Path.Combine(candidate, "RevitAPI.xml")))
                        return candidate;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log("LocalRAG", $"[RESOLVE_ERROR] {ex.Message}");
                return null;
            }
        }

        private static string NormalizeSummary(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string trimmed = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            while (trimmed.Contains("  "))
                trimmed = trimmed.Replace("  ", " ");
            return trimmed;
        }

        private static string Clip(string text, int maxLen = 80)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("\r", " ").Replace("\n", " ");
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }

        // ─────────────────────────────────────────────
        // Internal data types
        // ─────────────────────────────────────────────

        private class ClassAccumulator
        {
            public string Namespace;
            public string ClassName;
            public string ClassSummary;
            public string ClassRemarks;
            public List<MemberEntry> Members = new List<MemberEntry>();
        }

        private class MemberEntry
        {
            public string MemberName;
            public string Summary;
            public string Remarks;
            public string Returns;
            public List<string> ParamDescriptions = new List<string>();
            public char MemberTypePrefix;  // M/P/F/E
        }
    }
}
