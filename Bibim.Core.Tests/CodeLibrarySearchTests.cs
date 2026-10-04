// Copyright (c) 2026 SquareZero Inc. — Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Bibim.Core.Tests
{
    public class CodeLibrarySearchTests
    {
        private static List<CodeSnippet> Library() => new List<CodeSnippet>
        {
            new CodeSnippet { Title = "문 번호 일괄 변경", Summary = "선택한 문의 Mark 값을 순번으로 채웁니다", Code = "// doors", RevitVersion = "2026", CreatedAt = new DateTime(2026, 7, 1) },
            new CodeSnippet { Title = "Export sheets to PDF", Summary = "Per-sheet PDF export with unique names", Code = "// pdf", RevitVersion = "2025", CreatedAt = new DateTime(2026, 6, 1) },
            new CodeSnippet { Title = "Export sheets to PDF (2026)", Summary = "Combined export", Code = "// pdf26", RevitVersion = "2026", CreatedAt = new DateTime(2026, 5, 1) },
            new CodeSnippet { Title = "Empty", Summary = "no code", Code = "" },
        };

        [Fact]
        public void Korean_MatchesRegardlessOfSpacing()
        {
            var hits = CodeLibrarySearch.Search(Library(), "문번호 바꿔줘");
            Assert.Single(hits);
            Assert.Equal("문 번호 일괄 변경", hits[0].Title);
        }

        [Fact]
        public void SameRevitVersion_RanksFirst()
        {
            var hits = CodeLibrarySearch.Search(Library(), "export sheets pdf", "2026");
            Assert.Equal(2, hits.Count);
            Assert.Equal("2026", hits[0].RevitVersion);
        }

        [Fact]
        public void SnippetsWithoutCode_AndNonMatches_AreExcluded()
        {
            Assert.Empty(CodeLibrarySearch.Search(Library(), "empty"));
            Assert.Empty(CodeLibrarySearch.Search(Library(), "rotate grids"));
            Assert.Empty(CodeLibrarySearch.Search(null, "pdf"));
            Assert.Empty(CodeLibrarySearch.Search(Library(), "  "));
        }

        [Fact]
        public void Format_CarriesCodeAndAVerificationReminder()
        {
            var text = CodeLibrarySearch.Format(CodeLibrarySearch.Search(Library(), "pdf"), "pdf");
            Assert.Contains("```csharp", text);
            Assert.Contains("run_roslyn_check", text);
            Assert.Contains("No saved snippet", CodeLibrarySearch.Format(new List<CodeSnippet>(), "x"));
        }

        [Fact]
        public void Tokenize_KeepsSingleHangulNouns_DropsSingleLatinLetters()
        {
            var t = CodeLibrarySearch.Tokenize("벽 a PDF 내보내기");
            Assert.Contains("벽", t);
            Assert.Contains("pdf", t);
            Assert.DoesNotContain("a", t);
        }
    }
}
