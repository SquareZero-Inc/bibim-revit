// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Bibim.Core
{
    /// <summary>
    /// Centralized configuration from rag_config.json.
    /// Ported from v2 with namespace change.
    /// </summary>
    public static class ConfigService
    {
        private static RagConfig _cachedConfig;
        private static readonly object _lock = new object();

        public class RagConfig
        {
            public string RagStore { get; set; }
            public string RevitVersion { get; set; }
            public string DynamoVersion { get; set; }
            public string ClaudeModel { get; set; }
            public string GeminiModel { get; set; }
            public string ClaudeApiKey { get; set; }
            public string GeminiApiKey { get; set; }
            public bool ValidationGateEnabled { get; set; }
            public bool AutoFixEnabled { get; set; }
            public int AutoFixMaxAttempts { get; set; }
            public bool VerifyStageEnabled { get; set; }
            public bool EnableApiXmlHints { get; set; }
            public string ValidationRolloutPhase { get; set; }

            // Version-specific RAG stores map (e.g., "2025" → "fileSearchStores/...")
            public Dictionary<string, string> Stores { get; set; }
            public string FallbackStore { get; set; }
        }

        public static RagConfig GetRagConfig()
        {
            lock (_lock)
            {
                if (_cachedConfig != null) return _cachedConfig;
                _cachedConfig = LoadRagConfig();
                return _cachedConfig;
            }
        }

        public static void ClearCache()
        {
            lock (_lock) { _cachedConfig = null; }
        }

        /// <summary>
        /// Returns masked API key for display (e.g. "sk-ant-ap...Ab3c").
        /// Returns empty string if not configured.
        /// </summary>
        public static string GetMaskedApiKey()
        {
            try
            {
                string key = GetRagConfig()?.ClaudeApiKey ?? "";
                if (string.IsNullOrEmpty(key)) return "";
                if (key.Length <= 12) return new string('*', key.Length);
                return key.Substring(0, 8) + "..." + key.Substring(key.Length - 4);
            }
            catch { return ""; }
        }

        /// <summary>
        /// Saves a new Anthropic API key to rag_config.json and reloads the config cache.
        /// </summary>
        public static void SaveApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key must not be empty.");

            string configPath = GetConfigPath();
            var obj = ReadConfigJson(configPath);

            if (obj["api_keys"] == null)
                obj["api_keys"] = new JObject();
            ((JObject)obj["api_keys"])["claude_api_key"] = apiKey.Trim();

            // Set default model for first-time setup so GetRagConfig() doesn't throw
            if (string.IsNullOrEmpty(obj["claude_model"]?.ToString()))
                obj["claude_model"] = "claude-sonnet-4-6";

            File.WriteAllText(configPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
            ClearCache();
        }

        /// <summary>
        /// Saves a new Gemini API key to rag_config.json and reloads the config cache.
        /// </summary>
        public static void SaveGeminiApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("Gemini API key must not be empty.");

            string configPath = GetConfigPath();
            var obj = ReadConfigJson(configPath);

            if (obj["api_keys"] == null)
                obj["api_keys"] = new JObject();
            ((JObject)obj["api_keys"])["gemini_api_key"] = apiKey.Trim();

            File.WriteAllText(configPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
            ClearCache();
        }

        /// <summary>
        /// Saves a new claude_model value to rag_config.json and reloads the config cache.
        /// </summary>
        public static void SaveModel(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("Model ID must not be empty.");

            string configPath = GetConfigPath();
            var obj = ReadConfigJson(configPath);

            obj["claude_model"] = modelId.Trim();

            File.WriteAllText(configPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
            ClearCache();
        }

        /// <summary>
        /// Returns masked Gemini API key for display. Returns empty string if not configured.
        /// </summary>
        public static string GetMaskedGeminiApiKey()
        {
            try
            {
                string key = GetRagConfig()?.GeminiApiKey ?? "";
                if (string.IsNullOrEmpty(key)) return "";
                if (key.Length <= 12) return new string('*', key.Length);
                return key.Substring(0, 8) + "..." + key.Substring(key.Length - 4);
            }
            catch { return ""; }
        }

        /// <summary>
        /// Write path for rag_config.json — always %AppData%\BIBIM\ so writes succeed
        /// even when the add-in is installed under Program Files (which is read-only).
        /// </summary>
        private static string GetConfigPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "BIBIM");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "rag_config.json");
        }

        /// <summary>
        /// Read path — AppData first (user has saved keys), then installer default beside DLL.
        /// </summary>
        private static string GetReadConfigPath()
        {
            string appDataPath = GetConfigPath();
            if (File.Exists(appDataPath)) return appDataPath;
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDir, "Config", "rag_config.json");
        }

        private static JObject ReadConfigJson(string configPath)
        {
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                return JObject.Parse(json);
            }
            return new JObject();
        }

        public static (string revitVersion, string dynamoVersion, string ragStoreName, string modelInfo) GetDisplayInfo()
        {
            var config = GetRagConfig();
            string displayStore = config.RagStore;
            if (config.RagStore.Contains("/"))
                displayStore = config.RagStore.Substring(config.RagStore.LastIndexOf("/") + 1);

            return (
                config.RevitVersion,
                config.DynamoVersion,
                displayStore,
                $"Claude: {config.ClaudeModel}"
            );
        }

        /// <summary>
        /// Resolve the effective Revit version with priority:
        ///   1. BibimApp.DetectedRevitVersion (runtime, from Revit host)
        ///   2. rag_config.json detected_revit_version (config file)
        /// Also resolves the matching RAG store for the version.
        /// </summary>
        public static string GetEffectiveRevitVersion()
        {
            // Runtime detection takes priority
            string runtime = BibimApp.DetectedRevitVersion;
            if (!string.IsNullOrEmpty(runtime))
                return runtime;

            // Fallback to config
            var config = GetRagConfig();
            return config?.RevitVersion ?? "2025";
        }

        /// <summary>
        /// Resolve the RAG store name for a given Revit version.
        /// Uses the stores map in rag_config.json.
        /// </summary>
        public static string GetRagStoreForVersion(string revitVersion)
        {
            var config = GetRagConfig();
            if (config?.Stores != null && config.Stores.TryGetValue(revitVersion, out string store))
                return store;

            // Try major version only (e.g., "2025.3" → "2025")
            if (revitVersion != null && revitVersion.Contains("."))
            {
                string major = revitVersion.Substring(0, revitVersion.IndexOf('.'));
                if (config?.Stores != null && config.Stores.TryGetValue(major, out string majorStore))
                    return majorStore;
            }

            return config?.RagStore ?? config?.FallbackStore;
        }

        private static RagConfig LoadRagConfig()
        {
            string store = null, version = null, dynamoVersion = null;
            string claudeModel = null, geminiModel = null;
            string claudeApiKey = null, geminiApiKey = null;
            string fallbackStore = null;
            Dictionary<string, string> stores = null;
            bool validationGateEnabled = true, autoFixEnabled = true, verifyStageEnabled = false, enableApiXmlHints = true;
            int autoFixMaxAttempts = 2;
            string validationRolloutPhase = "phase3";

            try
            {
                string configPath = GetReadConfigPath();

                if (!File.Exists(configPath))
                    throw new FileNotFoundException($"rag_config.json not found at: {configPath}");

                string json = File.ReadAllText(configPath);

                var obj = JObject.Parse(json);
                store = obj["active_store"]?.ToString() ?? obj["fallback_store"]?.ToString();
                version = obj["detected_revit_version"]?.ToString() ?? obj["fallback_version"]?.ToString();
                dynamoVersion = obj["detected_dynamo_version"]?.ToString();
                claudeModel = obj["claude_model"]?.ToString();
                geminiModel = obj["gemini_model"]?.ToString();

                if (obj["api_keys"] is JObject apiKeys)
                {
                    claudeApiKey = apiKeys["claude_api_key"]?.ToString();
                    geminiApiKey = apiKeys["gemini_api_key"]?.ToString();
                }

                // Environment variable overrides — takes precedence over config file values.
                // Useful for CI/CD, installer distribution, or when rag_config.json is absent.
                string envClaude = Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
                if (!string.IsNullOrEmpty(envClaude)) claudeApiKey = envClaude;

                string envGemini = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
                if (!string.IsNullOrEmpty(envGemini)) geminiApiKey = envGemini;

                fallbackStore = obj["fallback_store"]?.ToString();

                if (obj["stores"] is JObject storesObj)
                {
                    stores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in storesObj.Properties())
                    {
                        stores[prop.Name] = prop.Value?.ToString();
                    }
                }

                if (obj["validation"] is JObject val)
                {
                    bool.TryParse(val["gate_enabled"]?.ToString(), out validationGateEnabled);
                    bool.TryParse(val["auto_fix_enabled"]?.ToString(), out autoFixEnabled);
                    int.TryParse(val["auto_fix_max_attempts"]?.ToString(), out autoFixMaxAttempts);
                    bool.TryParse(val["verify_stage_enabled"]?.ToString(), out verifyStageEnabled);
                    bool.TryParse(val["enable_api_xml_hints"]?.ToString(), out enableApiXmlHints);
                    validationRolloutPhase = val["rollout_phase"]?.ToString() ?? "phase3";
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load rag_config.json: {ex.Message}", ex);
            }

            // Only claude_model is required. store/version are optional (RAG is optional in BYOK mode).
            if (string.IsNullOrEmpty(claudeModel))
                throw new InvalidOperationException("rag_config.json missing required value: claude_model.");

            return new RagConfig
            {
                RagStore = store,
                RevitVersion = version,
                DynamoVersion = dynamoVersion ?? "Unknown",
                ClaudeModel = claudeModel,
                GeminiModel = geminiModel ?? "",
                ClaudeApiKey = claudeApiKey,
                GeminiApiKey = geminiApiKey,
                // SupabaseUrl / SupabaseAnonKey intentionally omitted — Supabase removed in OSS build (Phase 2)
                Stores = stores,
                FallbackStore = fallbackStore ?? store,
                ValidationGateEnabled = validationGateEnabled,
                AutoFixEnabled = autoFixEnabled,
                AutoFixMaxAttempts = autoFixMaxAttempts < 0 ? 0 : autoFixMaxAttempts,
                VerifyStageEnabled = verifyStageEnabled,
                EnableApiXmlHints = enableApiXmlHints,
                ValidationRolloutPhase = validationRolloutPhase
            };
        }

    }
}
