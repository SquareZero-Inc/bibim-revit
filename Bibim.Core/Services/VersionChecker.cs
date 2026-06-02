// Copyright (c) 2026 SquareZero Inc. - Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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
        /// <summary>
        /// One-line headline extracted from the release body. Capped at
        /// <see cref="ReleaseNotesHeadlineMaxChars"/>. The full markdown stays
        /// on the GitHub release page (see <see cref="ReleaseNotesUrl"/>).
        /// </summary>
        public string ReleaseNotes { get; set; }
        /// <summary>
        /// GitHub release page (html_url). Frontend opens this when the user
        /// clicks "View full release notes" instead of trying to render the
        /// raw markdown body in the alert bar.
        /// </summary>
        public string ReleaseNotesUrl { get; set; }
        public string ErrorMessage { get; set; }

        public const int ReleaseNotesHeadlineMaxChars = 140;

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
    /// Checks GitHub Releases (SquareZero-Inc/bibim-revit) for a newer version.
    /// Mandatory update: release body contains "[MANDATORY]".
    /// Asset selection: picks the KO or EN installer based on AppLanguage.IsEnglish.
    /// </summary>
    public static class VersionChecker
    {
        private const string ReleasesApiUrl =
            "https://api.github.com/repos/SquareZero-Inc/bibim-revit/releases/latest";

        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("BIBIM-AI", BibimApp.AppVersion));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        public static async Task<VersionCheckResult> CheckForUpdatesAsync()
        {
            string currentRaw = BibimApp.AppVersion; // e.g. "1.0.0-en" or "1.0.0-kr"
            // Strip language suffix to get bare semver for comparison
            int dashIdx = currentRaw.IndexOf('-');
            string currentVersion = dashIdx >= 0 ? currentRaw.Substring(0, dashIdx) : currentRaw;

            try
            {
                string json = await _httpClient.GetStringAsync(ReleasesApiUrl).ConfigureAwait(false);
                var release = JObject.Parse(json);

                string tagName = release["tag_name"]?.ToString() ?? "";
                string latestVersion = tagName.TrimStart('v', 'V');
                string body = release["body"]?.ToString() ?? "";
                bool isMandatory = body.IndexOf("[MANDATORY]", StringComparison.OrdinalIgnoreCase) >= 0;

                if (CompareVersions(latestVersion, currentVersion) <= 0)
                {
                    Logger.Log("VersionChecker", "Up to date (" + currentVersion + ")");
                    return VersionCheckResult.NoUpdateNeeded;
                }

                // Find matching asset: _EN_ for English build, _KO_ for Korean build
                string langToken = AppLanguage.IsEnglish ? "_EN_" : "_KO_";
                string downloadUrl = null;
                var assets = release["assets"] as JArray;
                if (assets != null)
                {
                    foreach (var asset in assets)
                    {
                        string name = asset["name"]?.ToString() ?? "";
                        if (name.IndexOf(langToken, StringComparison.OrdinalIgnoreCase) >= 0
                            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset["browser_download_url"]?.ToString();
                            break;
                        }
                    }
                }

                // Fallback: use release HTML page if no matching asset found
                if (string.IsNullOrEmpty(downloadUrl))
                    downloadUrl = release["html_url"]?.ToString() ?? "";

                Logger.Log("VersionChecker",
                    "Update available: " + currentVersion + " -> " + latestVersion
                    + ", mandatory=" + isMandatory);

                return new VersionCheckResult
                {
                    UpdateRequired = true,
                    IsMandatory = isMandatory,
                    LatestVersion = latestVersion,
                    CurrentVersion = currentVersion,
                    DownloadUrl = downloadUrl,
                    ReleaseNotes = ExtractReleaseHeadline(body),
                    ReleaseNotesUrl = release["html_url"]?.ToString() ?? ""
                };
            }
            catch (Exception ex)
            {
                Logger.Log("VersionChecker", "Version check failed: " + ex.Message);
                return VersionCheckResult.Error(ex.Message);
            }
        }

        /// <summary>
        /// Extract a single-line headline from a GitHub release body. The body is
        /// full markdown (headers, tables, code fences, ~9KB for a typical BIBIM
        /// release) — rendering it raw in the alert bar produced a multi-thousand-
        /// pixel text wall in v1.1.0. We now keep the rich content on the GitHub
        /// release page (linked separately) and only ship a short headline through
        /// the bridge.
        ///
        /// Heuristic — return the first non-empty line that is not a markdown
        /// header (#), horizontal rule (---), blockquote-only fence, or pure
        /// metadata (**Release date**, etc.). Strips obvious markdown decoration.
        /// Falls back to the bare first 140 chars if no clean line is found.
        /// </summary>
        private static string ExtractReleaseHeadline(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return "";

            string[] lines = body.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#")) continue;     // markdown headers
                if (line.StartsWith("---")) continue;   // horizontal rule
                if (line.StartsWith("==")) continue;
                if (line.StartsWith("```")) continue;   // code fence
                if (line.StartsWith("|")) continue;     // table row
                if (line.StartsWith("**Release date**", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("**릴리즈일**", StringComparison.Ordinal)) continue; // **릴리즈일**

                // Strip the most common decorations
                string clean = line.TrimStart('>', ' ', '*', '_', '-', '\t');
                clean = clean.Replace("**", "").Replace("*", "").Trim();
                if (clean.Length == 0) continue;

                return clean.Length > VersionCheckResult.ReleaseNotesHeadlineMaxChars
                    ? clean.Substring(0, VersionCheckResult.ReleaseNotesHeadlineMaxChars) + "..."
                    : clean;
            }

            // Fallback — no clean line. Just trim the raw body.
            string fallback = body.Trim();
            return fallback.Length > VersionCheckResult.ReleaseNotesHeadlineMaxChars
                ? fallback.Substring(0, VersionCheckResult.ReleaseNotesHeadlineMaxChars) + "..."
                : fallback;
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
