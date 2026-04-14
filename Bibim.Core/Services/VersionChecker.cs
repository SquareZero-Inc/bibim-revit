// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Threading.Tasks;

namespace Bibim.Core
{
    /// <summary>
    /// Result of a version check.
    /// </summary>
    public class VersionCheckResult
    {
        public bool UpdateRequired { get; set; }
        public bool IsMandatory { get; set; }
        public string LatestVersion { get; set; }
        public string CurrentVersion { get; set; }
        public string DownloadUrl { get; set; }
        public string ReleaseNotes { get; set; }
        public string ErrorMessage { get; set; }

        public static VersionCheckResult NoUpdateNeeded => new VersionCheckResult
        {
            UpdateRequired = false,
            IsMandatory = false
        };

        public static VersionCheckResult Error(string message) => new VersionCheckResult
        {
            UpdateRequired = false,
            IsMandatory = false,
            ErrorMessage = message
        };
    }

    /// <summary>
    /// Version checker — currently a stub; returns NoUpdateNeeded.
    /// Future: GitHub Releases API check against SquareZero-Inc/bibim-revit.
    /// </summary>
    public static class VersionChecker
    {
        public static Task<VersionCheckResult> CheckForUpdatesAsync()
        {
            Logger.Log("VersionChecker", "Version check not yet implemented");
            return Task.FromResult(VersionCheckResult.NoUpdateNeeded);
        }

        public static int CompareVersions(string v1, string v2)
        {
            if (string.IsNullOrEmpty(v1) && string.IsNullOrEmpty(v2)) return 0;
            if (string.IsNullOrEmpty(v1)) return -1;
            if (string.IsNullOrEmpty(v2)) return 1;

            v1 = v1.TrimStart('v', 'V');
            v2 = v2.TrimStart('v', 'V');

            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');
            int max = Math.Max(parts1.Length, parts2.Length);

            for (int i = 0; i < max; i++)
            {
                int p1 = i < parts1.Length && int.TryParse(parts1[i], out int a) ? a : 0;
                int p2 = i < parts2.Length && int.TryParse(parts2[i], out int b) ? b : 0;
                if (p1 < p2) return -1;
                if (p1 > p2) return 1;
            }
            return 0;
        }
    }
}
