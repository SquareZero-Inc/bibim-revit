// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Security.Cryptography;
using System.Text;

namespace Bibim.Core
{
    /// <summary>
    /// Encrypts API keys at rest in rag_config.json with Windows DPAPI (CurrentUser scope):
    /// only the same Windows account on the same machine can decrypt them. Stored form is
    /// "dpapi:&lt;base64&gt;". Plaintext values (manual edits, pre-1.2.0 configs) are still
    /// read and are re-saved encrypted by ConfigService on the next load.
    ///
    /// A value that cannot be decrypted (config copied from another PC or user profile)
    /// resolves to null, which surfaces as "key not configured" instead of a crash.
    /// </summary>
    public static class SecretProtector
    {
        public const string Prefix = "dpapi:";

        // Fixed per-app entropy: another app running as the same user cannot decrypt
        // these blobs with a bare ProtectedData.Unprotect call.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BIBIM.rag_config.api_keys.v1");

        public static bool IsProtected(string stored) =>
            !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>Template placeholders ("YOUR_..._KEY", "..._HERE") are not secrets.</summary>
        public static bool IsPlaceholder(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string v = value.Trim();
            return v.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
                || v.IndexOf("_HERE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True for a real plaintext key that should be migrated to encrypted form.</summary>
        public static bool NeedsProtection(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored) || IsProtected(stored)) return false;
            return !IsPlaceholder(stored);
        }

        public static string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return plaintext;
            if (IsProtected(plaintext)) return plaintext;
            byte[] data = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(data);
        }

        /// <summary>Decrypt a stored value; plaintext passes through; failure returns null.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored) || !IsProtected(stored)) return stored;
            try
            {
                byte[] blob = Convert.FromBase64String(stored.Substring(Prefix.Length));
                byte[] data = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex)
            {
                Logger.Log("SecretProtector",
                    $"Stored key could not be decrypted (config from another user/PC?): {ex.GetType().Name}");
                return null;
            }
        }
    }
}
