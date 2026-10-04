// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Bibim.Core.Tests
{
    // Attached-document input (spec: BIBIM_AI_MD첨부입력_긴급구현_명세서). Runs in the KR default
    // language — AppLanguage is not switched here (it is process-wide).

    public class AttachedDocumentTests
    {
        private const string AgreedReport =
@"---
report: model-check
---
# 워크셋 위반 보고서

| 규칙 ID | ElementId | UniqueId | 카테고리 | 패밀리/타입 | 현재 값 | 기대 값 |
|---|---|---|---|---|---|---|
| WS-01 | 312345 | 6f1c-aaaa-0001 | 벽 | 기본 벽: 일반 200 | Shared Levels and Grids | Architecture |
| WS-01 | 312346 | 6f1c-aaaa-0002 | 벽 | 기본 벽: 일반 200 | Shared Levels and Grids | Architecture |
| WS-01 | 312347 |  | 벽 | 기본 벽: 일반 200 | Shared Levels and Grids | Architecture |
";

        [Fact]
        public void Compose_FollowsSpecFormat_DocumentFirst_InstructionLast()
        {
            var doc = AttachedDocument.Create("워크셋_위반.md", AgreedReport);
            string text = doc.ComposeForLlm("문서대로 수정해 줘");

            Assert.StartsWith("[첨부 문서]\n", text.Replace("\r\n", "\n"));
            Assert.Contains("파일명: 워크셋_위반.md", text);
            Assert.Contains("형식: markdown", text);
            int start = text.IndexOf(AttachedDocument.BlockStart);
            int end = text.IndexOf(AttachedDocument.BlockEnd);
            int list = text.IndexOf("[구조화된 대상 목록: 3건]");
            int instr = text.IndexOf("[사용자 지시]");
            Assert.True(start >= 0 && start < end && end < list && list < instr);
            Assert.EndsWith("문서대로 수정해 줘", text);
        }

        [Fact]
        public void LongDocument_IsCutAt200k_WithOmissionNote()
        {
            string body = new string('가', AttachedDocument.MaxChars + 1234);
            var doc = AttachedDocument.Create("긴문서.md", body);

            Assert.True(doc.Truncated);
            Assert.Equal(1234, doc.OmittedChars);
            Assert.Equal(AttachedDocument.MaxChars, doc.Content.Length);
            string text = doc.ComposeForLlm("검토");
            Assert.Contains("[문서가 길어 뒤쪽 1234자를 생략했습니다]", text);
            // The note sits inside the block, so the model reads it as part of the document.
            Assert.True(text.IndexOf("생략했습니다") < text.IndexOf(AttachedDocument.BlockEnd));
        }

        [Fact]
        public void ClientSideOmission_IsAddedToServerSideCut()
        {
            var doc = AttachedDocument.Create("a.md", "짧은 본문", 350000, omittedByClient: 90000);
            Assert.True(doc.Truncated);
            Assert.Equal(90000, doc.OmittedChars);
            Assert.Equal(350000, doc.SizeBytes);
        }

        [Fact]
        public void Bom_IsStripped()
        {
            var doc = AttachedDocument.Create("bom.md", "\uFEFF# 제목");
            Assert.Equal("# 제목", doc.Content);
        }

        [Fact]
        public void FromPayload_AcceptsOnlyTextExtensions()
        {
            Assert.NotNull(AttachedDocument.FromPayload(JObject.FromObject(new { name = "보고서.md", content = "x" })));
            Assert.NotNull(AttachedDocument.FromPayload(JObject.FromObject(new { name = "a.TXT", content = "x" })));
            Assert.NotNull(AttachedDocument.FromPayload(JObject.FromObject(new { name = "a.csv", content = "x" })));
            Assert.Null(AttachedDocument.FromPayload(JObject.FromObject(new { name = "a.pdf", content = "x" })));
            Assert.Null(AttachedDocument.FromPayload(JObject.FromObject(new { name = "a.exe", content = "x" })));
            Assert.Null(AttachedDocument.FromPayload(JObject.FromObject(new { name = "", content = "x" })));
            Assert.Null(AttachedDocument.FromPayload(null));
        }

        [Fact]
        public void FromPayload_ReadsSizeAndClientOmission_AndStripsPath()
        {
            var doc = AttachedDocument.FromPayload(JObject.Parse(
                @"{""name"":""C:\\temp\\검토 결과.md"",""content"":""본문"",""sizeBytes"":307200,""omittedChars"":1000}"));
            Assert.Equal("검토 결과.md", doc.Name);
            Assert.Equal(307200, doc.SizeBytes);
            Assert.Equal(1000, doc.OmittedChars);
        }

        [Fact]
        public void Redact_RemovesBody_KeepsInstruction()
        {
            var doc = AttachedDocument.Create("a.md", "비밀 본문 내용 ABC");
            string composed = doc.ComposeForLlm("수정해 줘");
            string redacted = AttachedDocument.Redact(composed);

            Assert.DoesNotContain("비밀 본문 내용 ABC", redacted);
            Assert.Contains("attached document body not stored", redacted);
            Assert.Contains("수정해 줘", redacted);
            Assert.Equal("plain text", AttachedDocument.Redact("plain text"));
        }

        [Fact]
        public void InstructionOf_ReturnsOnlyTheUsersInstruction()
        {
            var doc = AttachedDocument.Create("a.md", "export to csv 파일로 저장");
            Assert.Equal("워크셋 고쳐 줘", AttachedDocument.InstructionOf(doc.ComposeForLlm("워크셋 고쳐 줘")));
            Assert.Equal("그냥 메시지", AttachedDocument.InstructionOf("그냥 메시지"));
        }

        [Fact]
        public void DisplayMarker_HasNameLine_ThenInstruction()
        {
            var doc = AttachedDocument.Create("한글 파일명.md", "x");
            Assert.Equal("📎 한글 파일명.md\n고쳐 줘", doc.DisplayMarker("고쳐 줘"));
        }

        [Fact]
        public void Digest_ReportsCountRuleAndTransition()
        {
            var doc = AttachedDocument.Create("ws.md", AgreedReport);
            string digest = doc.BuildPlanDigest();

            // Acceptance #2 form: "대상 요소 N건, 규칙 X".
            Assert.Contains("**대상 요소 3건, 규칙 WS-01**", digest);
            Assert.Contains("현재 'Shared Levels and Grids' → 기대 'Architecture' (3건)", digest);
            Assert.Contains("목록 밖 요소는 건드리지 않습니다", digest);
        }

        [Fact]
        public void Digest_UsesRuleDescriptions_FromSummaryTable_AndCountsPerRule()
        {
            var doc = AttachedDocument.Create("mixed.md",
@"## 위반 요약
| 규칙 ID | 규칙 설명 | 건수 |
|---|---|---|
| WS-01 | 벽 워크셋 = Architecture | 2 |
| PR-01 | 도어 주석 필수 | 1 |

## 위반 목록
| 규칙 ID | ElementId | UniqueId | 카테고리 | 패밀리/타입 | 현재 값 | 기대 값 |
|---|---|---|---|---|---|---|
| WS-01 | 1 |  | 벽 | t | a | b |
| WS-01 | 2 |  | 벽 | t | a | b |
| PR-01 | 3 |  | 문 | t | (비어 있음) | FD-60 |");

            Assert.Equal("벽 워크셋 = Architecture", doc.RuleNames["WS-01"]);
            string digest = doc.BuildPlanDigest();
            Assert.Contains("**대상 요소 3건, 규칙 WS-01 벽 워크셋 = Architecture (2건), PR-01 도어 주석 필수 (1건)**", digest);
        }

        [Fact]
        public void Digest_WithoutTable_SaysPlanUsesFullText()
        {
            var doc = AttachedDocument.Create("memo.txt", "그냥 메모입니다.");
            Assert.Empty(doc.Targets);
            Assert.Contains("표를 찾지 못해", doc.BuildPlanDigest());
            Assert.Equal("", doc.BuildTargetList());
            Assert.DoesNotContain("구조화된 대상 목록", doc.ComposeForLlm("검토"));
        }
    }

    public class AttachmentTableParserTests
    {
        [Fact]
        public void AgreedKoreanLayout_MapsAllColumns()
        {
            var rows = AttachmentTableParser.ParseMarkdown(
@"| 규칙 ID | ElementId | UniqueId | 카테고리 | 패밀리/타입 | 현재 값 | 기대 값 |
|---|---|---|---|---|---|---|
| NM-02 | 4001 | uid-1 | 벽 타입 | BT 시연 타입 A | BT 시연 타입 A | 공백 없음 |");

            var t = Assert.Single(rows);
            Assert.Equal("NM-02", t.RuleId);
            Assert.Equal(4001, t.ElementId);
            Assert.Equal("uid-1", t.UniqueId);
            Assert.Equal("벽 타입", t.Category);
            Assert.Equal("BT 시연 타입 A", t.TypeName);
            Assert.Equal("BT 시연 타입 A", t.CurrentValue);
            Assert.Equal("공백 없음", t.ExpectedValue);
        }

        [Fact]
        public void EnglishExportLayout_MapsObserved_AndSkipsIndeterminate()
        {
            var rows = AttachmentTableParser.ParseMarkdown(
@"## findings

| outcome | severity | rule_id | rule | category | type | element_id | field | observed | user | origin | when_utc |
|---|---|---|---|---|---|---|---|---|---|---|---|
| violation | error | P-01 | 필수 파라미터 | Doors | 900 x 2100 | 5501 | Comments | (empty) | kim | local | 2026-09-27 |
| indeterminate | warning | P-01 | 필수 파라미터 | Doors | 900 x 2100 | 5502 | Comments | ? | kim | local | 2026-09-27 |");

            var t = Assert.Single(rows);
            Assert.Equal(5501, t.ElementId);
            Assert.Equal("P-01", t.RuleId);
            Assert.Equal("필수 파라미터", t.RuleName);
            Assert.Equal("Comments", t.Field);
            Assert.Equal("(empty)", t.CurrentValue);
        }

        [Fact]
        public void EscapedPipe_StaysInsideCell_AndBackticksAreTrimmed()
        {
            var rows = AttachmentTableParser.ParseMarkdown(
@"| ElementId | 현재 값 |
|---|---|
| `777` | a \| b |");
            var t = Assert.Single(rows);
            Assert.Equal(777, t.ElementId);
            Assert.Equal("a | b", t.CurrentValue);
        }

        [Fact]
        public void FirstTableWithIdColumn_Wins_OtherTablesIgnored()
        {
            var rows = AttachmentTableParser.ParseMarkdown(
@"| id | 규칙 |
|---|---|
| 1 | 워크셋 |

| ElementId | 기대 값 |
|---|---|
| 10 | Architecture |
| 11 | Architecture |");
            // The numbered rule table has no ElementId column — its ""1"" is not an element.
            Assert.Equal(new long[] { 10, 11 }, rows.Select(r => r.ElementId.Value).ToArray());
        }

        [Fact]
        public void RowsWithoutAnyId_AreDropped_AndGarbageNeverThrows()
        {
            var rows = AttachmentTableParser.ParseMarkdown(
@"| ElementId | 기대 값 |
|---|---|
| 없음 | x |
| 12 | y |");
            Assert.Single(rows);
            Assert.Empty(AttachmentTableParser.ParseMarkdown("| broken | table\n|--"));
            Assert.Empty(AttachmentTableParser.ParseMarkdown(null));
        }

        [Fact]
        public void Csv_WithQuotedCells()
        {
            var rows = AttachmentTableParser.ParseCsv(
"rule_id,element_id,unique_id,current,expected\r\nWS-01,100,\"u,1\",\"Shared\",Architecture\r\nWS-01,101,,Shared,Architecture\r\n");
            Assert.Equal(2, rows.Count);
            Assert.Equal("u,1", rows[0].UniqueId);
            Assert.Null(rows[1].UniqueId);
        }
    }

    public class AttachmentScopeTests
    {
        [Fact]
        public void AllInside_NoWarning()
        {
            var r = AttachmentScope.Compare(new[] { 1L, 2L, 3L }, new[] { 1L, 2L, 3L });
            Assert.Equal(3, r.ChangedTargets);
            Assert.Equal(0, r.ChangedOutside);
            Assert.Equal("문서 대상 대조: 목록 3건 중 3건 변경, 목록 밖 변경 0건", AttachmentScope.Describe(r));
        }

        [Fact]
        public void OutsideChange_IsFlaggedWithIds()
        {
            var r = AttachmentScope.Compare(new[] { 1L, 2L }, new[] { 1L, 99L });
            Assert.Equal(1, r.ChangedTargets);
            Assert.Equal(1, r.ChangedOutside);
            Assert.StartsWith("⚠ ", AttachmentScope.Describe(r));
            Assert.Contains("99", AttachmentScope.Describe(r));
        }

        [Fact]
        public void CappedIdList_IsMarkedPartial()
        {
            var r = AttachmentScope.Compare(new[] { 1L }, new[] { 1L }, affectedTotal: 900);
            Assert.True(r.Partial);
            Assert.Contains("일부만 대조", AttachmentScope.Describe(r));
        }

        [Fact]
        public void NoNumericTargets_NoComparison()
        {
            Assert.Null(AttachmentScope.Compare(new long[0], new[] { 1L }));
            Assert.Null(AttachmentScope.Compare(null, new[] { 1L }));
            Assert.Null(AttachmentScope.Describe(null));
        }
    }
}
