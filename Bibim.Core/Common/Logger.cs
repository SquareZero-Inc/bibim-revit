// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.IO;

namespace Bibim.Core
{
    /// <summary>
    /// Centralized logging utility for BIBIM v3.
    /// Logs to %APPDATA%\BIBIM\logs\bibim_debug.txt (matching the rest of the
    /// addon's storage layout under %APPDATA%\BIBIM\). The path was previously
    /// %USERPROFILE%\bibim_v3_debug.txt — a file landing at the home directory
    /// root surprises users and isn't covered by APPDATA-only backup scopes.
    /// Pre-v1.1 installs may have a stale file at the old location; we leave it
    /// in place rather than risk deleting a debug artifact the user still wants.
    /// </summary>
    public static class Logger
    {
        // Resolved once at class init: %APPDATA%\BIBIM\logs\bibim_debug.txt.
        // The directory is created lazily on first Log() so unit tests that
        // construct nothing don't materialize a directory.
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BIBIM", "logs");
        private static readonly string LogPath = Path.Combine(LogDir, "bibim_debug.txt");

        private static readonly object _lock = new object();
        private static bool _enabled = true;
        private static bool _dirEnsured;

        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        private const long MaxLogBytes = 10 * 1024 * 1024; // 10 MB

        public static void Log(string source, string message)
        {
            if (!_enabled) return;

            try
            {
                lock (_lock)
                {
                    if (!_dirEnsured)
                    {
                        Directory.CreateDirectory(LogDir);
                        _dirEnsured = true;
                    }

                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    {
                        if (File.Exists(LogPath + ".bak")) File.Delete(LogPath + ".bak");
                        File.Move(LogPath, LogPath + ".bak");
                    }

                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}]: {message}{Environment.NewLine}");
                }
            }
            catch { /* Silent fail — logging should never crash the app */ }
        }

        /// <summary>
        /// Resolved current log file path. Exposed for DiagnosticsService and the
        /// "Open log folder" Settings action — callers shouldn't hard-code the
        /// path since it has moved between versions.
        /// </summary>
        public static string CurrentLogPath => LogPath;

        public static void LogError(string source, Exception ex)
        {
            Log(source, $"ERROR: {ex.Message}\n{ex.StackTrace}");
        }

        public static void Clear()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(LogPath))
                        File.Delete(LogPath);
                }
            }
            catch { }
        }
    }
}
