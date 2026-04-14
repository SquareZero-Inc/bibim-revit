// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.IO;

namespace Bibim.Core
{
    /// <summary>
    /// Centralized logging utility for BIBIM v3.
    /// Logs to %USERPROFILE%/bibim_v3_debug.txt
    /// </summary>
    public static class Logger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "bibim_v3_debug.txt"
        );

        private static readonly object _lock = new object();
        private static bool _enabled = true;

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
