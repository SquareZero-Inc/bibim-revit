// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            public string ClaudeModel { get; set; }       // selected model id (kept as "claude_model" key for back-compat — may hold any catalog id, e.g. gpt-6-sol)
            // Optional cheaper model for the PLANNER call only (classification /
            // question generation doesn't need the premium codegen model). Applied
            // in EnsurePlannerService; ignored when empty or when its provider has
            // no key. Non-local providers only.
            public string PlannerModel { get; set; }
            // Provider keys. Stored DPAPI-encrypted ("dpapi:..."), held decrypted here.
            // ClaudeApiKey kept as alias for AnthropicApiKey for migration.
            public string AnthropicApiKey { get; set; }
            public string ClaudeApiKey { get; set; }      // legacy alias — same value as AnthropicApiKey
            public string OpenAiApiKey { get; set; }
            // Self-hosted local LLM (v1.1+). Server runs an OpenAI-compatible
            // /v1/chat/completions endpoint (Ollama / LM Studio / vLLM / llama.cpp server).
            // ApiKey is optional — only needed for authenticated self-hosted setups.
            // ModelName is the literal name the local server expects (may differ from
            // the canonical OpenRouter id stored in ClaudeModel).
            public string LocalServerUrl { get; set; }
            public string LocalApiKey { get; set; }
            public string LocalModelName { get; set; }
            public bool ValidationGateEnabled { get; set; }
            public bool AutoFixEnabled { get; set; }
            public int AutoFixMaxAttempts { get; set; }
            public bool VerifyStageEnabled { get; set; }
            public bool EnableApiXmlHints { get; set; }
            public string ValidationRolloutPhase { get; set; }

            // Runtime self-correction (안 A+). After a successful compile, run a
            // dry-run preview and regenerate if the result looks wrong (runtime
            // exception / 0 elements / missing step). Master switch + bounds.
            public bool SelfCorrectionEnabled { get; set; }
            public int SelfCorrectionMaxRetries { get; set; }  // runtime regenerations per task
            public int SelfCorrectionScaleGuard { get; set; }  // affected>this ⇒ skip (dry-run too costly)

            // LLM request tuning (v1.2.0+, optional "llm" object in rag_config.json).
            // Effort maps to Anthropic output_config.effort / OpenAI reasoning.effort.
            public string EffortPlanner { get; set; }   // default "low"  — classification + questions
            public string EffortChat { get; set; }      // default "medium"
            public string EffortCodegen { get; set; }   // default "high" — tool loop
            public string PromptCacheTtl { get; set; }  // "1h" (default) | "5m" for the tools+system prefix

            // Version-specific RAG stores map (e.g., "2025" → "fileSearchStores/...")
            public Dictionary<string, string> Stores { get; set; }
            public string FallbackStore { get; set; }
        }

        /// <summary>
        /// Models exposed in the UI. Order = display order in selectors. Derived from
        /// <see cref="ModelCatalog"/> — edit the catalog, not this list.
        /// </summary>
        public static readonly (string Id, string Label, string Provider)[] AvailableModels =
            ModelCatalog.Models.Select(m => (m.Id, m.Label, m.Provider)).ToArray();

        /// <summary>
        /// Default model when none is configured.
        /// </summary>
        public const string DefaultModelId = ModelCatalog.DefaultModelId;

        public static RagConfig GetRagConfig()
        {
            lock (_lock)
            {
                if (_cachedConfig != null) return _cachedConfig;
                _cachedConfig = LoadRagConfig();
                return _cachedConfig;
            }
        }

        /// <summary>
        /// Returns true if the model id is a legacy OSS vendor-prefixed id that
        /// existed in v1.1.x prior to the single-"local" picker collapse. Used
        /// during config load to trigger one-shot migration to "local".
        /// </summary>
        private static bool IsLegacyLocalVendorId(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return false;
            string m = modelId.Trim().ToLowerInvariant();
            return m.StartsWith("google/gemma-")
                || m.StartsWith("meta-llama/")
                || m.StartsWith("mistralai/")
                || m.StartsWith("qwen/")
                || m.StartsWith("nvidia/")
                || m.StartsWith("kwaipilot/");
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
            ((JObject)obj["api_keys"])["claude_api_key"] = SecretProtector.Protect(apiKey.Trim());

            // Set default model for first-time setup so GetRagConfig() doesn't throw
            if (string.IsNullOrEmpty(obj["claude_model"]?.ToString()))
                obj["claude_model"] = DefaultModelId;

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
            string claudeModel = null, plannerModel = null;
            string anthropicApiKey = null, openAiApiKey = null, localApiKey = null;
            string effortPlanner = "low", effortChat = "medium", effortCodegen = "high", promptCacheTtl = "1h";
            string localServerUrl = null, localModelName = null;
            string fallbackStore = null;
            Dictionary<string, string> stores = null;
            bool validationGateEnabled = true, autoFixEnabled = true, verifyStageEnabled = false, enableApiXmlHints = true;
            int autoFixMaxAttempts = 2;
            string validationRolloutPhase = "phase3";
            bool selfCorrectionEnabled = true;
            int selfCorrectionMaxRetries = 1;
            int selfCorrectionScaleGuard = 500;

            try
            {
                string configPath = GetReadConfigPath();

                JObject obj;
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    obj = JObject.Parse(json);
                }
                else
                {
                    // No config anywhere (fresh install, wiped AppData, or an installer
                    // that shipped without a default file). Start with built-in defaults
                    // instead of crashing the panel — credentials can still come from
                    // env vars or Settings.
                    Logger.Log("ConfigService",
                        $"rag_config.json not found at {configPath} — starting with defaults.");
                    obj = new JObject();
                }
                store = obj["active_store"]?.ToString() ?? obj["fallback_store"]?.ToString();
                version = obj["detected_revit_version"]?.ToString() ?? obj["fallback_version"]?.ToString();
                dynamoVersion = obj["detected_dynamo_version"]?.ToString();
                claudeModel = obj["claude_model"]?.ToString();
                plannerModel = obj["planner_model"]?.ToString();

                // Migration (v1.2.0): the Gemini provider was removed. A stored gemini-*
                // selection (including the old -customtools variant) moves to the default
                // model so the panel keeps working after the upgrade.
                if (!string.IsNullOrEmpty(claudeModel))
                {
                    string migrated = ModelCatalog.MigrateModelId(claudeModel);
                    if (!string.Equals(migrated, claudeModel, StringComparison.Ordinal))
                    {
                        string prior = claudeModel;
                        claudeModel = migrated;
                        obj["claude_model"] = claudeModel;
                        if (TryRewriteConfig(configPath, obj))
                            Logger.Log("ConfigService",
                                $"Migrated saved model id '{prior}' → '{claudeModel}' (provider removed; rewrote rag_config.json).");
                    }
                }
                if (!string.IsNullOrEmpty(plannerModel) &&
                    !string.Equals(ModelCatalog.MigrateModelId(plannerModel), plannerModel, StringComparison.Ordinal))
                {
                    // A planner override pointing at a removed provider is simply dropped.
                    plannerModel = null;
                }

                // Migration (v1.1.x+): older builds stored OSS vendor-prefixed
                // OpenRouter ids (google/gemma-..., meta-llama/..., mistralai/...,
                // qwen/..., nvidia/..., kwaipilot/...) directly as claude_model
                // when the model picker had three separate "Local" rows. The picker
                // is now collapsed to a single "local" entry — server-side model
                // name lives under local.model_name. Coerce the old id to "local"
                // and stash the model fragment as the override if no override is
                // already set, so behaviour is preserved across the upgrade.
                if (!string.IsNullOrEmpty(claudeModel) && IsLegacyLocalVendorId(claudeModel))
                {
                    string priorOssId = claudeModel;
                    claudeModel = "local";
                    try
                    {
                        obj["claude_model"] = claudeModel;

                        // Preserve the model name fragment as local.model_name if the
                        // user hadn't already typed an explicit override. Strip the
                        // vendor prefix (matching LocalProvider.StripVendorPrefix).
                        int slash = priorOssId.IndexOf('/');
                        string fragment = slash >= 0 && slash < priorOssId.Length - 1
                            ? priorOssId.Substring(slash + 1)
                            : priorOssId;

                        if (obj["local"] == null) obj["local"] = new JObject();
                        var localMigrationObj = (JObject)obj["local"];
                        if (string.IsNullOrWhiteSpace(localMigrationObj["model_name"]?.ToString()))
                            localMigrationObj["model_name"] = fragment;

                        string bakPath = configPath + ".bak";
                        if (!File.Exists(bakPath))
                        {
                            try { File.Copy(configPath, bakPath); } catch { /* non-fatal */ }
                        }
                        File.WriteAllText(configPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
                        Logger.Log("ConfigService",
                            $"Migrated saved model id '{priorOssId}' → 'local' (local.model_name = '{fragment}', rewrote rag_config.json).");
                    }
                    catch (Exception migEx)
                    {
                        Logger.Log("ConfigService",
                            $"In-memory migration applied; disk rewrite skipped: {migEx.Message}");
                    }
                }

                if (obj["api_keys"] is JObject apiKeys)
                {
                    // At-rest encryption (v1.2.0): plaintext keys from older versions or
                    // manual edits are re-saved DPAPI-encrypted before being read.
                    if (EncryptPlaintextKeys(apiKeys) && TryRewriteConfig(configPath, obj))
                        Logger.Log("ConfigService", "Encrypted plaintext API keys in rag_config.json (DPAPI, current user).");

                    // Read new canonical key names; fall back to legacy claude_api_key for migration.
                    anthropicApiKey = SecretProtector.Unprotect(apiKeys["anthropic_api_key"]?.ToString())
                                   ?? SecretProtector.Unprotect(apiKeys["claude_api_key"]?.ToString());
                    openAiApiKey   = SecretProtector.Unprotect(apiKeys["openai_api_key"]?.ToString());
                    localApiKey    = SecretProtector.Unprotect(apiKeys["local_api_key"]?.ToString());
                }

                if (obj["llm"] is JObject llmObj)
                {
                    effortPlanner  = NormalizeEffort(llmObj["effort_planner"]?.ToString(), effortPlanner);
                    effortChat     = NormalizeEffort(llmObj["effort_chat"]?.ToString(), effortChat);
                    effortCodegen  = NormalizeEffort(llmObj["effort_codegen"]?.ToString(), effortCodegen);
                    string ttl = llmObj["prompt_cache_ttl"]?.ToString();
                    if (string.Equals(ttl, "5m", StringComparison.OrdinalIgnoreCase)) promptCacheTtl = "5m";
                }

                if (obj["local"] is JObject localObj)
                {
                    localServerUrl = localObj["server_url"]?.ToString();
                    localModelName = localObj["model_name"]?.ToString();
                }

                // Environment variable overrides — takes precedence over config file values.
                // Useful for CI/CD, installer distribution, or when rag_config.json is absent.
                string envAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                                   ?? Environment.GetEnvironmentVariable("CLAUDE_API_KEY");
                if (!string.IsNullOrEmpty(envAnthropic)) anthropicApiKey = envAnthropic;

                string envOpenAi = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                if (!string.IsNullOrEmpty(envOpenAi)) openAiApiKey = envOpenAi;

                // Local LLM env overrides (CI / scripted installs / cloud GPU rentals)
                string envLocalUrl = Environment.GetEnvironmentVariable("BIBIM_LOCAL_LLM_URL");
                if (!string.IsNullOrEmpty(envLocalUrl)) localServerUrl = envLocalUrl;
                string envLocalKey = Environment.GetEnvironmentVariable("BIBIM_LOCAL_LLM_API_KEY");
                if (!string.IsNullOrEmpty(envLocalKey)) localApiKey = envLocalKey;
                string envLocalModel = Environment.GetEnvironmentVariable("BIBIM_LOCAL_LLM_MODEL");
                if (!string.IsNullOrEmpty(envLocalModel)) localModelName = envLocalModel;

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

                    // Self-correction keys — only override defaults when present
                    // (older configs omit them; we want enabled=true/retries=1/guard=500
                    // to survive a missing key rather than collapse to false/0).
                    if (val["self_correction_enabled"] != null)
                        bool.TryParse(val["self_correction_enabled"].ToString(), out selfCorrectionEnabled);
                    if (val["self_correction_max_retries"] != null)
                        int.TryParse(val["self_correction_max_retries"].ToString(), out selfCorrectionMaxRetries);
                    if (val["self_correction_scale_guard"] != null)
                        int.TryParse(val["self_correction_scale_guard"].ToString(), out selfCorrectionScaleGuard);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load rag_config.json: {ex.Message}", ex);
            }

            // claude_model is optional in v1.1+ — fall back to default if missing.
            if (string.IsNullOrEmpty(claudeModel))
                claudeModel = DefaultModelId;

            return new RagConfig
            {
                RagStore = store,
                RevitVersion = version,
                DynamoVersion = dynamoVersion ?? "Unknown",
                ClaudeModel = claudeModel,
                PlannerModel = plannerModel,
                AnthropicApiKey = anthropicApiKey,
                ClaudeApiKey = anthropicApiKey,        // legacy alias — same value
                OpenAiApiKey = openAiApiKey,
                LocalServerUrl = localServerUrl,
                LocalApiKey = localApiKey,
                LocalModelName = localModelName,
                Stores = stores,
                FallbackStore = fallbackStore ?? store,
                ValidationGateEnabled = validationGateEnabled,
                AutoFixEnabled = autoFixEnabled,
                AutoFixMaxAttempts = autoFixMaxAttempts < 0 ? 0 : autoFixMaxAttempts,
                VerifyStageEnabled = verifyStageEnabled,
                EnableApiXmlHints = enableApiXmlHints,
                ValidationRolloutPhase = validationRolloutPhase,
                SelfCorrectionEnabled = selfCorrectionEnabled,
                SelfCorrectionMaxRetries = selfCorrectionMaxRetries < 0 ? 0 : selfCorrectionMaxRetries,
                SelfCorrectionScaleGuard = selfCorrectionScaleGuard < 0 ? 0 : selfCorrectionScaleGuard,
                EffortPlanner = effortPlanner,
                EffortChat = effortChat,
                EffortCodegen = effortCodegen,
                PromptCacheTtl = promptCacheTtl
            };
        }

        private static string NormalizeEffort(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            string v = value.Trim().ToLowerInvariant();
            return v == "low" || v == "medium" || v == "high" ? v : fallback;
        }

        /// <summary>Encrypt any plaintext provider key in place. Returns true if anything changed.</summary>
        private static bool EncryptPlaintextKeys(JObject apiKeys)
        {
            bool changed = false;
            foreach (var prop in apiKeys.Properties().ToList())
            {
                if (!prop.Name.EndsWith("_api_key", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value == null || prop.Value.Type != JTokenType.String) continue;
                string v = prop.Value.ToString();
                if (!SecretProtector.NeedsProtection(v)) continue;
                try
                {
                    prop.Value = SecretProtector.Protect(v.Trim());
                    changed = true;
                }
                catch (Exception ex)
                {
                    Logger.Log("ConfigService", $"Key encryption skipped for {prop.Name}: {ex.GetType().Name}");
                }
            }
            return changed;
        }

        /// <summary>
        /// Rewrite rag_config.json after an in-place migration, keeping a one-time .bak.
        /// Only rewrites the per-user AppData copy — never the installer default beside
        /// the DLL (read-only under Program Files).
        /// </summary>
        private static bool TryRewriteConfig(string configPath, JObject obj)
        {
            try
            {
                string userPath = GetConfigPath();
                if (!string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(userPath), StringComparison.OrdinalIgnoreCase))
                    return false;
                string bakPath = configPath + ".bak";
                if (File.Exists(configPath) && !File.Exists(bakPath))
                {
                    try { File.Copy(configPath, bakPath); } catch { /* non-fatal */ }
                }
                File.WriteAllText(configPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("ConfigService", $"In-memory migration applied; disk rewrite skipped: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Anthropic Messages endpoint override (null = api.anthropic.com). Extension point
        /// for routing through a self-hosted gateway; the public build always returns null.
        /// </summary>
        public static string GetAnthropicEndpoint()
        {
            return null;
        }

        // ────────────────────────────────────────────────────────────
        // v1.1.0 multi-provider helpers
        // ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the provider name resolved from the currently configured model id.
        /// Falls back to "anthropic" if the model is unknown.
        /// </summary>
        public static string GetActiveProviderName()
        {
            string modelId = GetRagConfig()?.ClaudeModel ?? DefaultModelId;
            return LlmProviderFactory.ResolveProviderForModel(modelId) ?? "anthropic";
        }

        /// <summary>
        /// Resolve the active credentials: (providerName, apiKey, modelId).
        /// apiKey is null if the user hasn't configured a key for the active provider.
        /// </summary>
        public static (string Provider, string ApiKey, string ModelId) GetActiveCredentials()
        {
            var cfg = GetRagConfig();
            string modelId = cfg?.ClaudeModel ?? DefaultModelId;
            string provider = LlmProviderFactory.ResolveProviderForModel(modelId) ?? "anthropic";
            string apiKey = GetApiKeyForProvider(provider, cfg);
            return (provider, apiKey, modelId);
        }

        /// <summary>
        /// Look up the API key stored for a given provider name. Returns null if absent.
        /// </summary>
        public static string GetApiKeyForProvider(string providerName, RagConfig cfg = null)
        {
            cfg = cfg ?? GetRagConfig();
            if (cfg == null) return null;
            switch (providerName)
            {
                case "anthropic": return cfg.AnthropicApiKey;
                case "openai":    return cfg.OpenAiApiKey;
                // For "local", a key is OPTIONAL — most self-hosted setups (Ollama default,
                // LM Studio default) accept unauthenticated requests. Returning the value
                // (which may be empty) lets the caller decide whether to add a bearer header.
                case "local":     return cfg.LocalApiKey;
                default:          return null;
            }
        }

        /// <summary>
        /// Returns which provider names currently have a non-empty API key configured.
        /// Used by the Settings UI to gate model availability.
        /// For "local", availability is gated by Server URL (key is optional).
        /// </summary>
        public static bool HasKeyForProvider(string providerName)
        {
            // Local availability rule is different: URL configured == provider available.
            if (providerName == "local")
            {
                string url = GetRagConfig()?.LocalServerUrl;
                return !string.IsNullOrWhiteSpace(url);
            }

            string key = GetApiKeyForProvider(providerName);
            return !string.IsNullOrWhiteSpace(key) && !SecretProtector.IsPlaceholder(key);
        }

        /// <summary>
        /// Save an API key for a specific provider. Writes to api_keys.{anthropic|openai}_api_key
        /// (DPAPI-encrypted). For backwards compat, anthropic also mirrors to claude_api_key.
        /// </summary>
        public static void SaveApiKeyForProvider(string providerName, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(providerName))
                throw new ArgumentException("Provider name required.");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("API key must not be empty.");

            string configPath = GetConfigPath();

            // One-time migration backup
            if (File.Exists(configPath))
            {
                string bakPath = configPath + ".bak";
                if (!File.Exists(bakPath))
                {
                    try { File.Copy(configPath, bakPath); } catch { /* non-fatal */ }
                }
            }

            var obj = ReadConfigJson(configPath);
            if (obj["api_keys"] == null) obj["api_keys"] = new JObject();
            var apiKeys = (JObject)obj["api_keys"];

            string protectedKey = SecretProtector.Protect(apiKey.Trim());
            switch (providerName)
            {
                case "anthropic":
                    apiKeys["anthropic_api_key"] = protectedKey;
                    apiKeys["claude_api_key"] = protectedKey;   // legacy mirror for any older readers
                    break;
                case "openai":
                    apiKeys["openai_api_key"] = protectedKey;
                    break;
                default:
                    throw new ArgumentException($"Unknown provider: {providerName}");
            }

            // Default model on first-time setup so GetRagConfig() doesn't fall back unexpectedly
            if (string.IsNullOrEmpty(obj["claude_model"]?.ToString()))
                obj["claude_model"] = DefaultModelId;

            File.WriteAllText(configPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
            ClearCache();
        }

        /// <summary>
        /// Save the local LLM server config: base URL (required), model name (sent
        /// to the server, may differ from the canonical OpenRouter id), and optional
        /// API key for authenticated self-hosted setups (vLLM --api-key, sssh proxy,
        /// cloud GPU rental, etc.). Passing null/empty for a field clears it.
        /// </summary>
        public static void SaveLocalServerConfig(string serverUrl, string modelName, string apiKey)
        {
            string configPath = GetConfigPath();

            // One-time migration backup (same convention as SaveApiKeyForProvider)
            if (File.Exists(configPath))
            {
                string bakPath = configPath + ".bak";
                if (!File.Exists(bakPath))
                {
                    try { File.Copy(configPath, bakPath); } catch { /* non-fatal */ }
                }
            }

            var obj = ReadConfigJson(configPath);

            // local.server_url + local.model_name live under a "local" object.
            if (obj["local"] == null) obj["local"] = new JObject();
            var localObj = (JObject)obj["local"];
            localObj["server_url"] = string.IsNullOrWhiteSpace(serverUrl) ? null : serverUrl.Trim();
            localObj["model_name"] = string.IsNullOrWhiteSpace(modelName) ? null : modelName.Trim();

            // local API key lives alongside other provider keys under api_keys.
            if (obj["api_keys"] == null) obj["api_keys"] = new JObject();
            var apiKeys = (JObject)obj["api_keys"];
            apiKeys["local_api_key"] = string.IsNullOrWhiteSpace(apiKey) ? null : SecretProtector.Protect(apiKey.Trim());

            // Default model on first-time setup
            if (string.IsNullOrEmpty(obj["claude_model"]?.ToString()))
                obj["claude_model"] = DefaultModelId;

            File.WriteAllText(configPath, obj.ToString(Newtonsoft.Json.Formatting.Indented));
            ClearCache();
        }

        /// <summary>
        /// Returns a masked API key for display (e.g. "sk-ant-ap...Ab3c").
        /// </summary>
        public static string GetMaskedKeyForProvider(string providerName)
        {
            try
            {
                string key = GetApiKeyForProvider(providerName) ?? "";
                if (string.IsNullOrEmpty(key)) return "";
                if (key.Length <= 12) return new string('*', key.Length);
                return key.Substring(0, 8) + "..." + key.Substring(key.Length - 4);
            }
            catch { return ""; }
        }
    }
}
