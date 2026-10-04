// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Bibim.Core
{
    /// <summary>One model-changing event (commit or undo) in the local audit log.</summary>
    public sealed class AuditEntry
    {
        [JsonProperty("ts")] public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        [JsonProperty("event")] public string Event { get; set; }            // "commit" | "undo"
        [JsonProperty("appVersion")] public string AppVersion { get; set; }
        [JsonProperty("taskId")] public string TaskId { get; set; }
        [JsonProperty("title")] public string Title { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("kind")] public string Kind { get; set; }
        [JsonProperty("document")] public string Document { get; set; }
        [JsonProperty("documentPath")] public string DocumentPath { get; set; }
        [JsonProperty("model")] public string Model { get; set; }
        [JsonProperty("success")] public bool Success { get; set; }
        [JsonProperty("error")] public string Error { get; set; }
        [JsonProperty("risk")] public string Risk { get; set; }              // low | medium | high
        [JsonProperty("affected")] public int Affected { get; set; }
        [JsonProperty("added")] public int Added { get; set; }
        [JsonProperty("modified")] public int Modified { get; set; }
        [JsonProperty("deleted")] public int Deleted { get; set; }
        [JsonProperty("previewAffected")] public int PreviewAffected { get; set; } = -1;
        [JsonProperty("elementIds")] public List<long> ElementIds { get; set; }
        [JsonProperty("codeSha256")] public string CodeSha256 { get; set; }
        [JsonProperty("output")] public string Output { get; set; }
        [JsonProperty("verification")] public string Verification { get; set; }
        [JsonProperty("revitWarnings")] public List<string> RevitWarnings { get; set; }
    }

    /// <summary>
    /// Append-only JSON-lines audit trail of every change BIBIM applied to a model:
    /// %APPDATA%\BIBIM\audit\audit_YYYYMM.jsonl (one file per month, local only —
    /// nothing is uploaded). Gives teams a reviewable record of what the agent did,
    /// to which document, with which code (hash) and which elements.
    /// Logging must never break the user flow: all failures are swallowed.
    /// </summary>
    public static class AuditLogService
    {
        private const int MaxOutputChars = 2000;
        private const int MaxElementIds = 500;
        private static readonly object _lock = new object();

        public static string AuditDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIBIM", "audit");

        public static void Append(AuditEntry entry, string directoryOverride = null)
        {
            if (entry == null) return;
            try
            {
                if (entry.Output != null && entry.Output.Length > MaxOutputChars)
                    entry.Output = entry.Output.Substring(0, MaxOutputChars) + "…";
                if (entry.ElementIds != null && entry.ElementIds.Count > MaxElementIds)
                    entry.ElementIds = entry.ElementIds.GetRange(0, MaxElementIds);

                string dir = directoryOverride ?? AuditDirectory;
                string path = Path.Combine(dir, $"audit_{entry.Timestamp:yyyyMM}.jsonl");
                string line = JsonConvert.SerializeObject(entry, Formatting.None) + "\n";
                lock (_lock)
                {
                    Directory.CreateDirectory(dir);
                    File.AppendAllText(path, line, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                Logger.Log("AuditLog", $"Audit append skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// high: anything deleted or more than 50 elements touched; low: nothing touched on a
        /// task whose success legitimately changes nothing (export / view / query); else medium.
        /// </summary>
        public static string ClassifyRisk(string category, int affected, int deleted)
        {
            if (deleted > 0 || category == TaskCategories.Delete || affected > 50) return "high";
            if (affected == 0 && (category == null || TaskCategories.IsZeroDeltaByDesign(category))) return "low";
            return "medium";
        }

        public static string Sha256(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
