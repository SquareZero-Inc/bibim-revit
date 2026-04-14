// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Temporary local artifact recorder for code generation debugging.
    /// Saves prompts, raw model outputs, wrapped code, and diagnostics under
    /// %APPDATA%\BIBIM\debug\codegen so failed runs can be inspected after testing.
    /// </summary>
    public static class CodegenDebugRecorder
    {
        private static readonly object _lock = new object();

        public static string BaseDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BIBIM",
            "debug",
            "codegen");

        public static string CreateRunDirectory(string taskId, string requestId, string tag = null)
        {
            string dayFolder = DateTime.Now.ToString("yyyyMMdd");
            string timestamp = DateTime.Now.ToString("HHmmss_fff");
            string safeTaskId = Sanitize(taskId, 48);
            string safeRequestId = Sanitize(requestId, 32);
            string safeTag = string.IsNullOrWhiteSpace(tag) ? "" : $"_{Sanitize(tag, 24)}";

            string folder = Path.Combine(
                BaseDirectory,
                dayFolder,
                $"{timestamp}_{safeTaskId}_{safeRequestId}{safeTag}");

            lock (_lock)
            {
                Directory.CreateDirectory(folder);
            }

            Logger.Log("CodegenDebug", $"Artifacts: {folder}");
            return folder;
        }

        public static void WriteText(string directory, string fileName, string content)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
                return;

            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, fileName), content ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("CodegenDebug", $"WriteText failed: {ex.Message}");
            }
        }

        public static void WriteJson(string directory, string fileName, object payload)
        {
            WriteText(directory, fileName, JsonHelper.Serialize(payload, indented: true));
        }

        public static void WriteCompilationArtifacts(string directory, string prefix, CompilationResult result)
        {
            if (string.IsNullOrWhiteSpace(directory) || result == null)
                return;

            string safePrefix = string.IsNullOrWhiteSpace(prefix) ? "compile" : Sanitize(prefix, 40);
            WriteText(directory, $"{safePrefix}_original.cs", result.OriginalSource);
            WriteText(directory, $"{safePrefix}_normalized.cs", result.NormalizedSource);
            WriteText(directory, $"{safePrefix}_wrapped.cs", result.WrappedSource);

            var diagnostics = result.Diagnostics?
                .Select(d => d.ToString())
                .ToList() ?? new List<string>();

            if (!string.IsNullOrWhiteSpace(result.ErrorSummary))
                diagnostics.Insert(0, result.ErrorSummary);

            WriteText(directory, $"{safePrefix}_diagnostics.txt", string.Join(Environment.NewLine, diagnostics));
        }

        private static string Sanitize(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "run";

            var chars = value
                .Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                .ToArray();

            string sanitized = chars.Length == 0 ? "run" : new string(chars);
            return sanitized.Length > maxLength ? sanitized.Substring(0, maxLength) : sanitized;
        }
    }
}
