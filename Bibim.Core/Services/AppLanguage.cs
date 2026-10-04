// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;

namespace Bibim.Core
{
    /// <summary>
    /// Application language management. Ported from v2.
    /// </summary>
    public static class AppLanguage
    {
        public const string English = "en";
        public const string Korean = "kr";

        public static string Current { get; private set; } = Default;

        public static string Default
        {
            get
            {
#if APP_LANG_EN
                return English;
#else
                return Korean;
#endif
            }
        }

        public static bool IsEnglish => string.Equals(Current, English, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Single home for the en/kr string pick used across the app (provider
        /// UiText, orchestrator status labels, prompt builders). Add new localized
        /// literals through this instead of re-spelling the ternary per class.
        /// </summary>
        public static string Pick(string en, string kr) => IsEnglish ? en : kr;

        public static string Normalize(string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return Default;

            string n = language.Trim().ToLowerInvariant();
            if (n == "en" || n == "en-us" || n == "english") return English;
            if (n == "ko" || n == "ko-kr" || n == "kr" || n == "korean") return Korean;
            return Default;
        }

        public static void Initialize(string language = null)
        {
            Current = Normalize(language);
        }
    }
}
