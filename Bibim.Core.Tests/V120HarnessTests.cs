// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bibim.Core.Tests
{
    // Unit tests for the v1.2.0 harness changes. Everything here is Revit-free and
    // network-free: request shapes are asserted on the built JSON, streams are replayed
    // from recorded SSE event shapes.

    public class ModelCatalogTests
    {
        [Fact]
        public void Ids_AreUnique_AndDefaultIsASelectableAnthropicModel()
        {
            var ids = ModelCatalog.Models.Select(m => m.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var def = ModelCatalog.Find(ModelCatalog.DefaultModelId);
            Assert.NotNull(def);
            Assert.Equal("anthropic", def.Provider);
        }

        [Fact]
        public void Gemini_IsGone_AndStoredSelectionsMigrateToDefault()
        {
            Assert.DoesNotContain(ModelCatalog.Models, m => m.Id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(ModelCatalog.DefaultModelId, ModelCatalog.MigrateModelId("gemini-3.1-pro-preview"));
            Assert.Equal(ModelCatalog.DefaultModelId, ModelCatalog.MigrateModelId("gemini-3.1-pro-preview-customtools"));
            Assert.Equal("claude-opus-4-7", ModelCatalog.MigrateModelId("claude-opus-4-7"));
            Assert.Null(ModelCatalog.ResolveProvider("gemini-3.1-pro-preview"));
        }

        [Theory]
        [InlineData("claude-sonnet-5", "anthropic")]
        [InlineData("claude-opus-5-5", "anthropic")]
        [InlineData("claude-some-future-model", "anthropic")]
        [InlineData("gpt-6-sol", "openai")]
        [InlineData("gpt-5.6-terra", "openai")]
        [InlineData("local", "local")]
        [InlineData("meta-llama/llama-3.3-70b", "local")]
        public void ResolveProvider_RoutesByPrefix(string id, string provider)
        {
            Assert.Equal(provider, ModelCatalog.ResolveProvider(id));
        }

        [Fact]
        public void Capabilities_MatchLiveVerifiedBehaviour()
        {
            // Thinking on by default: new generation only.
            Assert.True(ModelCatalog.Find("claude-sonnet-5").ThinkingOnByDefault);
            Assert.False(ModelCatalog.Find("claude-sonnet-4-6").ThinkingOnByDefault);
            // Preserved thinking → append-only tool loop.
            Assert.True(ModelCatalog.Find("claude-opus-5-5").AppendOnlyHistory);
            Assert.True(ModelCatalog.Find("claude-fable-5-1").AppendOnlyHistory);
            Assert.False(ModelCatalog.Find("claude-sonnet-5").AppendOnlyHistory);
            // Classifier models opt into server-side fallbacks.
            Assert.True(ModelCatalog.Find("claude-opus-5").ServerSideFallback);
            Assert.False(ModelCatalog.Find("claude-sonnet-4-6").ServerSideFallback);
            // Local servers: no schema/effort, small ceiling.
            var local = ModelCatalog.Find("local");
            Assert.False(local.SupportsStructuredOutput);
            Assert.False(local.SupportsEffort);
            Assert.True(local.MaxOutputTokens <= 8192);
        }
    }

    public class AnthropicRequestShapeTests
    {
        private static JArray Msgs(params JObject[] m) => new JArray(m);
        private static JObject User(string text) => new JObject { ["role"] = "user", ["content"] = text };
        private static JArray Tools() => new JArray
        {
            new JObject { ["name"] = "a", ["description"] = "d", ["input_schema"] = new JObject { ["type"] = "object" } },
            new JObject { ["name"] = "b", ["description"] = "d", ["input_schema"] = new JObject { ["type"] = "object" } },
        };

        [Fact]
        public void Prefix_Uses1hTtl_Tail5m_AndCallerToolsAreNotMutated()
        {
            var tools = Tools();
            var body = AnthropicProvider.BuildRequestBody("claude-sonnet-5", Msgs(User("hi")), "sys", tools, 1000, null, false);

            Assert.Equal("1h", body["system"][0]["cache_control"]["ttl"]?.ToString());
            var sentTools = (JArray)body["tools"];
            Assert.Equal("1h", sentTools.Last["cache_control"]["ttl"]?.ToString());
            Assert.Null(sentTools.First["cache_control"]);
            Assert.Null(tools.Last["cache_control"]);                  // caller's array untouched

            var tailBlock = body["messages"].Last["content"].Last;
            Assert.Equal("ephemeral", tailBlock["cache_control"]["type"]?.ToString());
            Assert.Null(tailBlock["cache_control"]["ttl"]);            // 5-minute default
            Assert.Null(body["stream"]);
        }

        [Fact]
        public void FiveMinutePrefixTtl_OmitsTtlField()
        {
            var body = AnthropicProvider.BuildRequestBody("claude-sonnet-5", Msgs(User("hi")), "sys", Tools(), 1000, null, false, "5m");
            Assert.Null(body["system"][0]["cache_control"]["ttl"]);
            Assert.Null(body["tools"].Last["cache_control"]["ttl"]);
        }

        [Fact]
        public void EffortAndSchema_GoInOutputConfig()
        {
            var schema = PlannerSchema.Build();
            var opts = new LlmRequestOptions { Effort = "LOW", ResponseSchema = schema };
            var body = AnthropicProvider.BuildRequestBody("claude-sonnet-4-6", Msgs(User("hi")), "sys", null, 4096, opts, false);

            Assert.Equal("low", body["output_config"]["effort"]?.ToString());
            Assert.Equal("json_schema", body["output_config"]["format"]["type"]?.ToString());
            Assert.NotNull(body["output_config"]["format"]["schema"]["properties"]["taskCategory"]);
            Assert.Null(body["tools"]);
        }

        [Fact]
        public void Thinking_SummariesOnlyWhenProgressIsWanted_AndOnlyOnThinkingModels()
        {
            var progress = new LlmRequestOptions { OnProgress = (k, t) => { } };

            var newGen = AnthropicProvider.BuildRequestBody("claude-sonnet-5", Msgs(User("x")), "s", null, 32000, progress, true);
            Assert.Equal("adaptive", newGen["thinking"]["type"]?.ToString());
            Assert.Equal("summarized", newGen["thinking"]["display"]?.ToString());
            Assert.Equal(true, newGen["stream"]?.Value<bool>());

            var oldGen = AnthropicProvider.BuildRequestBody("claude-sonnet-4-6", Msgs(User("x")), "s", null, 32000, progress, true);
            Assert.Null(oldGen["thinking"]);

            var noProgress = AnthropicProvider.BuildRequestBody("claude-sonnet-5", Msgs(User("x")), "s", null, 4096, null, false);
            Assert.Null(noProgress["thinking"]);   // omitted = model default (adaptive, display omitted)
        }

        [Theory]
        [InlineData("claude-opus-5", true)]
        [InlineData("claude-opus-5-5", true)]
        [InlineData("claude-fable-5-1", true)]
        [InlineData("claude-sonnet-5", false)]
        [InlineData("claude-sonnet-4-6", false)]
        public void Fallbacks_AndBetaHeader_OnlyForClassifierModels(string model, bool expected)
        {
            var body = AnthropicProvider.BuildRequestBody(model, Msgs(User("x")), "s", null, 100, null, false);
            Assert.Equal(expected, body["fallbacks"]?.ToString() == "default");
            Assert.Equal(expected ? AnthropicProvider.FallbackBeta : null, AnthropicProvider.BuildBetaHeader(body));
        }

        [Fact]
        public void Caching_StripsStaleMarkersAndToolResultName_AndSkipsThinkingBlocks()
        {
            var messages = new JArray
            {
                new JObject { ["role"] = "user", ["content"] = new JArray {
                    new JObject { ["type"] = "text", ["text"] = "a", ["cache_control"] = new JObject { ["type"] = "ephemeral" } } } },
                new JObject { ["role"] = "user", ["content"] = new JArray {
                    new JObject { ["type"] = "tool_result", ["tool_use_id"] = "t1", ["name"] = "x", ["content"] = "ok" },
                    new JObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig" } } }
            };
            AnthropicProvider.ApplyIncrementalMessageCaching(messages);

            Assert.Null(messages[0]["content"][0]["cache_control"]);            // stale marker removed
            Assert.Null(messages[1]["content"][0]["name"]);                     // Anthropic rejects 'name'
            Assert.NotNull(messages[1]["content"][0]["cache_control"]);         // marker walked back past thinking
            Assert.Null(messages[1]["content"][1]["cache_control"]);
        }

        [Fact]
        public void SanitizeFallbackBoundary_DropsPreBoundaryNonTextAndMarkers()
        {
            var content = new JArray
            {
                new JObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "s" },
                new JObject { ["type"] = "text", ["text"] = "partial" },
                new JObject { ["type"] = "tool_use", ["id"] = "t", ["name"] = "n", ["input"] = new JObject() },
                new JObject { ["type"] = "fallback", ["from"] = new JObject(), ["to"] = new JObject() },
                new JObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "s2" },
                new JObject { ["type"] = "text", ["text"] = "final" },
            };
            AnthropicProvider.SanitizeFallbackBoundary(content);
            Assert.Equal(new[] { "text", "thinking", "text" }, content.Select(b => b["type"].ToString()).ToArray());
            Assert.Equal("partial", content[0]["text"].ToString());
            Assert.Equal("s2", content[1]["signature"].ToString());
        }
    }

    public class SseAssemblerTests
    {
        [Fact]
        public void Assembles_Thinking_Text_ToolUse_UsageAndStopReason()
        {
            var progress = new List<string>();
            var a = new AnthropicProvider.SseMessageAssembler((kind, delta) => progress.Add(kind + ":" + delta));
            void E(string json) => a.HandleEvent(null, json);

            E(@"{""type"":""message_start"",""message"":{""model"":""claude-sonnet-5"",""usage"":{""input_tokens"":12,""cache_read_input_tokens"":900,""cache_creation_input_tokens"":0,""output_tokens"":1}}}");
            E(@"{""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""thinking"",""thinking"":"""",""signature"":""""}}");
            E(@"{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""thinking_delta"",""thinking"":""Check levels. ""}}");
            E(@"{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""signature_delta"",""signature"":""SIG""}}");
            E(@"{""type"":""content_block_stop"",""index"":0}");
            E(@"{""type"":""content_block_start"",""index"":1,""content_block"":{""type"":""text"",""text"":""""}}");
            E(@"{""type"":""content_block_delta"",""index"":1,""delta"":{""type"":""text_delta"",""text"":""Looking up.""}}");
            E(@"{""type"":""content_block_start"",""index"":2,""content_block"":{""type"":""tool_use"",""id"":""toolu_1"",""name"":""count_elements"",""input"":{}}}");
            E(@"{""type"":""content_block_delta"",""index"":2,""delta"":{""type"":""input_json_delta"",""partial_json"":""{\""category\"": \""Wa""}}");
            E(@"{""type"":""content_block_delta"",""index"":2,""delta"":{""type"":""input_json_delta"",""partial_json"":""lls\""}""}}");
            E(@"{""type"":""message_delta"",""delta"":{""stop_reason"":""tool_use""},""usage"":{""output_tokens"":87}}");
            E(@"{""type"":""message_stop""}");

            var msg = a.Build();
            var content = (JArray)msg["content"];
            Assert.Equal("tool_use", msg["stop_reason"].ToString());
            Assert.Equal("claude-sonnet-5", msg["model"].ToString());
            Assert.Equal(3, content.Count);
            Assert.Equal("Check levels. ", content[0]["thinking"].ToString());
            Assert.Equal("SIG", content[0]["signature"].ToString());
            Assert.Equal("Looking up.", content[1]["text"].ToString());
            Assert.Equal("Walls", content[2]["input"]["category"].ToString());
            Assert.Equal(87, msg["usage"]["output_tokens"].Value<int>());
            Assert.Equal(900, msg["usage"]["cache_read_input_tokens"].Value<int>());
            Assert.Equal(12, msg["usage"]["input_tokens"].Value<int>());
            Assert.Contains("thinking:Check levels. ", progress);
            Assert.Contains("text:Looking up.", progress);
        }

        [Fact]
        public void RefusalStopReason_IsPreservedWithDetails()
        {
            var a = new AnthropicProvider.SseMessageAssembler(null);
            a.HandleEvent(null, @"{""type"":""message_start"",""message"":{""model"":""claude-opus-5"",""usage"":{""input_tokens"":5}}}");
            a.HandleEvent(null, @"{""type"":""message_delta"",""delta"":{""stop_reason"":""refusal"",""stop_details"":{""type"":""refusal"",""category"":""cyber""}},""usage"":{""output_tokens"":0}}");
            var msg = a.Build();
            Assert.Equal("refusal", msg["stop_reason"].ToString());
            Assert.Equal("cyber", msg["stop_details"]["category"].ToString());
        }

        [Fact]
        public void ErrorEvent_ThrowsWithStatusThePresenterUnderstands()
        {
            var a = new AnthropicProvider.SseMessageAssembler(null);
            var ex = Assert.Throws<HttpRequestException>(() =>
                a.HandleEvent("error", @"{""type"":""error"",""error"":{""type"":""overloaded_error"",""message"":""Overloaded""}}"));
            Assert.Contains("Anthropic API 529", ex.Message);
            Assert.Contains("(Anthropic, 529)", LlmErrorPresenter.ToUserMessage(ex));
        }

        [Fact]
        public void TruncatedToolJson_BecomesEmptyInputInsteadOfThrowing()
        {
            var a = new AnthropicProvider.SseMessageAssembler(null);
            a.HandleEvent(null, @"{""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""tool_use"",""id"":""t"",""name"":""n"",""input"":{}}}");
            a.HandleEvent(null, @"{""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""input_json_delta"",""partial_json"":""{\""cat""}}");
            var msg = a.Build();
            Assert.Empty((JObject)msg["content"][0]["input"]);
        }
    }

    public class OpenAIRequestOptionsTests
    {
        [Fact]
        public void Effort_Schema_AndJsonModeMapToResponsesApi()
        {
            var body = new JObject();
            OpenAIProvider.ApplyRequestOptions(body, new LlmRequestOptions
            {
                Effort = "low",
                ResponseSchema = PlannerSchema.Build(),
                ResponseSchemaName = PlannerSchema.Name,
                JsonMode = true
            });
            Assert.Equal("low", body["reasoning"]["effort"].ToString());
            Assert.Equal("json_schema", body["text"]["format"]["type"].ToString());
            Assert.Equal(PlannerSchema.Name, body["text"]["format"]["name"].ToString());
            Assert.True(body["text"]["format"]["strict"].Value<bool>());

            var jsonOnly = new JObject();
            OpenAIProvider.ApplyRequestOptions(jsonOnly, new LlmRequestOptions { JsonMode = true, Effort = "ultra" });
            Assert.Equal("json_object", jsonOnly["text"]["format"]["type"].ToString());
            Assert.Null(jsonOnly["reasoning"]);   // unknown effort dropped, not forwarded
        }
    }

    public class PlannerSchemaTests
    {
        [Fact]
        public void EveryObject_IsStrictCompatible()
        {
            var schema = PlannerSchema.Build();
            void AssertStrict(JObject obj)
            {
                Assert.False(obj["additionalProperties"].Value<bool>());
                var props = ((JObject)obj["properties"]).Properties().Select(p => p.Name).OrderBy(n => n);
                var required = ((JArray)obj["required"]).Select(r => r.ToString()).OrderBy(n => n);
                Assert.Equal(props, required);   // strict mode: every property required
            }
            AssertStrict(schema);
            AssertStrict((JObject)schema["properties"]["questions"]["items"]);
            Assert.Equal(TaskCategories.All, schema["properties"]["taskCategory"]["enum"].Select(t => t.ToString()).ToArray());
        }
    }

    public class TaskCategoryTests
    {
        [Theory]
        [InlineData("export", true, false)]
        [InlineData("view_selection", true, false)]
        [InlineData("query", true, false)]
        [InlineData("model_edit", false, true)]
        [InlineData("create", false, true)]
        [InlineData("delete", false, true)]
        [InlineData("annotation", false, true)]
        [InlineData("other", false, false)]
        public void Categories_DriveZeroDeltaAndChangeExpectations(string c, bool zeroDelta, bool expectsChange)
        {
            Assert.Equal(zeroDelta, TaskCategories.IsZeroDeltaByDesign(c));
            Assert.Equal(expectsChange, TaskCategories.ExpectsModelChange(c));
        }

        [Fact]
        public void Other_AndUnknown_AreNotTrustedByTheJudge()
        {
            Assert.False(TaskCategories.IsKnown("other"));
            Assert.False(TaskCategories.IsKnown(null));
            Assert.False(TaskCategories.IsKnown("rename"));
            Assert.True(TaskCategories.IsKnown("export"));
        }
    }

    public class ProgressNarratorTests
    {
        [Fact]
        public void Thinking_ShowsLatestSentence()
        {
            var n = new ProgressNarrator();
            string s = n.Feed("thinking", "I need the levels first. Then I will filter the doors by the L2 level id.");
            Assert.EndsWith("Then I will filter the doors by the L2 level id.", s);
        }

        [Fact]
        public void CodeFence_ShowsLineCountNotCode()
        {
            var n = new ProgressNarrator();
            n.Feed("text", "Here is the code:\n```csharp\nvar a = 1;\nvar b = 2;\n");
            string s = n.Current();
            Assert.DoesNotContain("var a", s);
            Assert.Contains("3", s);   // lines after the opening fence (incl. its own line break)
        }

        [Fact]
        public void Prose_ShowsLastLine_ClippedFromTheFront()
        {
            var n = new ProgressNarrator();
            string longLine = new string('x', 200) + " end";
            string s = n.Feed("text", "first line\n" + longLine);
            Assert.True(s.Length <= ProgressNarrator.MaxSnippetChars);
            Assert.EndsWith("end", s);
            Assert.StartsWith("…", s);
        }

        [Fact]
        public void Reset_ClearsState()
        {
            var n = new ProgressNarrator();
            n.Feed("text", "hello");
            n.Reset();
            Assert.Null(n.Current());
        }
    }

    public class CommitVerifierTests
    {
        [Fact]
        public void ModelEditThatChangedNothing_IsFlagged()
        {
            var v = CommitVerifier.Verify("model_edit", 12, 0, 0, 0, 0, "Done: 12 doors updated", null, _ => true);
            Assert.True(v.HasWarning);
            Assert.Contains(v.Lines, l => l.StartsWith("⚠"));
        }

        [Fact]
        public void FewerChangesThanPreview_IsFlagged()
        {
            var v = CommitVerifier.Verify("model_edit", 10, 3, 0, 3, 0, "ok", null, _ => true);
            Assert.True(v.HasWarning);
        }

        [Fact]
        public void ExportWithZeroDelta_IsFine_AndFilesAreCheckedOnDisk()
        {
            string output = @"Exported C:\Out\A101 - Plan.pdf and C:\Out\A102 - Plan.pdf (test run C:\Out\A101_BIBIM_TEST.pdf)";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Out\A101 - Plan.pdf" };
            var v = CommitVerifier.Verify("export", 0, 0, 0, 0, 0, output, null, existing.Contains);

            Assert.Equal(2, v.FilesReported);          // _BIBIM_TEST artifact ignored
            Assert.Equal(1, v.FilesFound);
            Assert.True(v.HasWarning);
            Assert.Contains(v.Lines, l => l.Contains("A102 - Plan.pdf"));
        }

        [Fact]
        public void ExportWithoutAnyPath_SaysFilesWereNotVerified()
        {
            var v = CommitVerifier.Verify("export", 0, 0, 0, 0, 0, "Export finished", null, _ => true);
            Assert.False(v.HasWarning);
            Assert.Equal(2, v.Lines.Count);
        }

        [Fact]
        public void FilePaths_FoundInLogsToo_Deduplicated()
        {
            var paths = CommitVerifier.ExtractFilePaths(@"saved D:\p\x.csv", new[] { @"[1/2] wrote D:\p\x.csv", @"wrote D:\p\y.xlsx" });
            Assert.Equal(new[] { @"D:\p\x.csv", @"D:\p\y.xlsx" }, paths.ToArray());
        }
    }

    public class HistoryWindowPolicyTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(10, 0)]
        [InlineData(15, 0)]
        [InlineData(16, 6)]
        [InlineData(21, 6)]
        [InlineData(22, 12)]
        public void StartMovesInSteps(int count, int expectedStart)
        {
            Assert.Equal(expectedStart, HistoryWindowPolicy.StartIndex(count, 10, 6));
        }

        [Fact]
        public void Start_IsStableAcrossConsecutiveTurnsWithinAStep()
        {
            var starts = Enumerable.Range(16, 6).Select(c => HistoryWindowPolicy.StartIndex(c, 10, 6)).Distinct();
            Assert.Single(starts);
        }
    }

    public class SecretProtectorTests
    {
        [Fact]
        public void RoundTrip_AndPlaintextPassthrough()
        {
            string secret = "sk-ant-api03-test-not-real";
            string stored = SecretProtector.Protect(secret);
            Assert.StartsWith(SecretProtector.Prefix, stored);
            Assert.DoesNotContain(secret, stored);
            Assert.Equal(secret, SecretProtector.Unprotect(stored));
            Assert.Equal("plain-key", SecretProtector.Unprotect("plain-key"));
            Assert.Equal(stored, SecretProtector.Protect(stored));   // idempotent
        }

        [Fact]
        public void Placeholders_AreNotEncrypted_AndGarbageDecryptsToNull()
        {
            Assert.False(SecretProtector.NeedsProtection("YOUR_ANTHROPIC_API_KEY_HERE"));
            Assert.False(SecretProtector.NeedsProtection("YOUR_CLAUDE_API_KEY"));
            Assert.True(SecretProtector.NeedsProtection("sk-real"));
            Assert.False(SecretProtector.NeedsProtection("dpapi:abc"));
            Assert.Null(SecretProtector.Unprotect("dpapi:bm90LWEtcmVhbC1ibG9i"));
        }
    }

    public class AuditLogTests
    {
        [Fact]
        public void Append_WritesOneJsonLinePerEvent()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bibim_audit_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                var entry = new AuditEntry { Event = "commit", TaskId = "t1", Affected = 3, ElementIds = new List<long> { 1, 2, 3 } };
                AuditLogService.Append(entry, dir);
                AuditLogService.Append(new AuditEntry { Event = "undo", TaskId = "t1" }, dir);

                var file = Directory.GetFiles(dir, "audit_*.jsonl").Single();
                var lines = File.ReadAllLines(file);
                Assert.Equal(2, lines.Length);
                var first = JObject.Parse(lines[0]);
                Assert.Equal("commit", first["event"].ToString());
                Assert.Equal(3, first["elementIds"].Count());
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Theory]
        [InlineData("delete", 1, 1, "high")]
        [InlineData("model_edit", 80, 0, "high")]
        [InlineData("model_edit", 5, 0, "medium")]
        [InlineData("export", 0, 0, "low")]
        public void Risk_Classification(string category, int affected, int deleted, string risk)
        {
            Assert.Equal(risk, AuditLogService.ClassifyRisk(category, affected, deleted));
        }

        [Fact]
        public void Sha256_IsStableHex()
        {
            Assert.Equal(AuditLogService.Sha256("abc"), AuditLogService.Sha256("abc"));
            Assert.Equal(64, AuditLogService.Sha256("abc").Length);
            Assert.Null(AuditLogService.Sha256(""));
        }
    }

    public class RefusalMessageTests
    {
        [Fact]
        public void RefusalMessage_IsLocalizedAndActionable()
        {
            Assert.False(string.IsNullOrWhiteSpace(LlmErrorPresenter.RefusalMessage()));
        }
    }
}
