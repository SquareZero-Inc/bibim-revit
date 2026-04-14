// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Revit API XML documentation index.
    /// Loads RevitAPI.xml to detect deprecated types/members.
    /// Ported from v2 BIBIM_MVP.RevitApiXmlProvider with namespace change.
    /// </summary>
    public sealed class RevitApiXmlIndex
    {
        public string Status { get; set; }
        public string SourcePath { get; set; }
        public HashSet<string> DeprecatedTypes { get; set; }
        public HashSet<string> DeprecatedMembers { get; set; }
        public Dictionary<string, string> TypeSummaries { get; set; }
        public Dictionary<string, string> MemberSummaries { get; set; }
    }

    /// <summary>
    /// Loads and caches RevitAPI.xml for deprecated API detection.
    /// Used by RoslynAnalyzerService (BIBIM004) and ApiInspectorService.
    /// </summary>
    public static class RevitApiXmlProvider
    {
        private static readonly object LockObject = new object();
        private static RevitApiXmlIndex _cache;
        private static DateTime _cacheTimestampUtc = DateTime.MinValue;
        private static string _cachePath = string.Empty;

        public static RevitApiXmlIndex GetOrLoad()
        {
            lock (LockObject)
            {
                string xmlPath = ResolveRevitApiXmlPath();
                if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
                    return CreateEmpty("xml_not_found", xmlPath);

                DateTime writeUtc = File.GetLastWriteTimeUtc(xmlPath);
                if (_cache != null &&
                    string.Equals(_cachePath, xmlPath, StringComparison.OrdinalIgnoreCase) &&
                    _cacheTimestampUtc == writeUtc)
                    return _cache;

                _cache = LoadXml(xmlPath);
                _cachePath = xmlPath;
                _cacheTimestampUtc = writeUtc;
                return _cache;
            }
        }

        private static RevitApiXmlIndex LoadXml(string xmlPath)
        {
            try
            {
                var doc = XDocument.Load(xmlPath);
                var index = new RevitApiXmlIndex
                {
                    Status = "ok",
                    SourcePath = xmlPath,
                    DeprecatedTypes = new HashSet<string>(StringComparer.Ordinal),
                    DeprecatedMembers = new HashSet<string>(StringComparer.Ordinal),
                    TypeSummaries = new Dictionary<string, string>(StringComparer.Ordinal),
                    MemberSummaries = new Dictionary<string, string>(StringComparer.Ordinal)
                };

                foreach (var member in doc.Descendants("member"))
                {
                    var nameAttr = member.Attribute("name");
                    if (nameAttr == null) continue;

                    string memberKey = nameAttr.Value ?? string.Empty;
                    if (memberKey.Length == 0) continue;

                    string summary = NormalizeSummary(
                        member.Element("summary") != null ? member.Element("summary").Value : string.Empty);
                    bool isDeprecated = IsDeprecatedText(summary);

                    string typeName, memberName;
                    if (!TryParseXmlMemberKey(memberKey, out typeName, out memberName))
                        continue;

                    if (string.IsNullOrWhiteSpace(memberName))
                    {
                        if (!index.TypeSummaries.ContainsKey(typeName) && !string.IsNullOrWhiteSpace(summary))
                            index.TypeSummaries[typeName] = summary;
                        if (isDeprecated)
                            index.DeprecatedTypes.Add(typeName);
                    }
                    else
                    {
                        string combined = typeName + "." + memberName;
                        if (!index.MemberSummaries.ContainsKey(combined) && !string.IsNullOrWhiteSpace(summary))
                            index.MemberSummaries[combined] = summary;
                        if (isDeprecated)
                            index.DeprecatedMembers.Add(combined);
                    }
                }

                return index;
            }
            catch (Exception ex)
            {
                return CreateEmpty("xml_parse_failed:" + ex.GetType().Name, xmlPath);
            }
        }

        private static RevitApiXmlIndex CreateEmpty(string status, string path)
        {
            return new RevitApiXmlIndex
            {
                Status = status,
                SourcePath = path ?? string.Empty,
                DeprecatedTypes = new HashSet<string>(StringComparer.Ordinal),
                DeprecatedMembers = new HashSet<string>(StringComparer.Ordinal),
                TypeSummaries = new Dictionary<string, string>(StringComparer.Ordinal),
                MemberSummaries = new Dictionary<string, string>(StringComparer.Ordinal)
            };
        }

        private static string ResolveRevitApiXmlPath()
        {
            try
            {
                var revitApiAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(a => string.Equals(
                        a.GetName().Name, "RevitAPI", StringComparison.OrdinalIgnoreCase));

                if (revitApiAssembly == null) return string.Empty;

                string dir = Path.GetDirectoryName(revitApiAssembly.Location);
                if (string.IsNullOrWhiteSpace(dir)) return string.Empty;

                return Path.Combine(dir, "RevitAPI.xml");
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeSummary(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string trimmed = raw.Replace("\r", " ").Replace("\n", " ").Trim();
            while (trimmed.Contains("  "))
                trimmed = trimmed.Replace("  ", " ");
            return trimmed;
        }

        private static bool IsDeprecatedText(string summary)
        {
            if (string.IsNullOrWhiteSpace(summary)) return false;
            string text = summary.ToLowerInvariant();
            return text.Contains("obsolete") || text.Contains("deprecated");
        }

        private static bool TryParseXmlMemberKey(string memberKey, out string typeName, out string memberName)
        {
            typeName = string.Empty;
            memberName = string.Empty;

            int prefixIdx = memberKey.IndexOf("Autodesk.Revit.DB.", StringComparison.Ordinal);
            if (prefixIdx < 0) return false;

            string shortName = memberKey.Substring(prefixIdx + "Autodesk.Revit.DB.".Length);
            int argsIdx = shortName.IndexOf('(');
            if (argsIdx >= 0)
                shortName = shortName.Substring(0, argsIdx);

            var parts = shortName.Split('.');
            if (parts.Length == 0) return false;

            typeName = parts[0];
            if (parts.Length == 1) return true;

            memberName = parts[1];
            if (memberName.StartsWith("get_", StringComparison.Ordinal))
                memberName = memberName.Substring("get_".Length);
            else if (memberName.StartsWith("set_", StringComparison.Ordinal))
                memberName = memberName.Substring("set_".Length);

            return !string.IsNullOrWhiteSpace(typeName);
        }
    }
}
