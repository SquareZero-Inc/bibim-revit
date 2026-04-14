// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using Xunit;

namespace Bibim.Core.Tests
{
    /// <summary>
    /// Tests for streaming Cut-off detection and code merging logic.
    /// Design doc §3.1.
    /// These tests verify the algorithms independently of the LLM service.
    /// </summary>
    public class StreamingCutoffTests
    {

        [Fact]
        public void IsLikelyCutOff_DetectsUnclosedCodeFence()
        {
            string text = "Here is the code:\n```csharp\npublic class Test {\n    public void Run() {";

            // Odd number of ``` = unclosed fence
            int fenceCount = CountOccurrences(text, "```");
            Assert.Equal(1, fenceCount);
            Assert.True(fenceCount % 2 != 0); // Cut-off detected
        }

        [Fact]
        public void IsLikelyCutOff_DetectsUnbalancedBraces()
        {
            string code = "public class Test {\n    public void Run() {\n        var x = 1;";

            Assert.False(HasBalancedBraces(code)); // Missing closing braces
        }

        [Fact]
        public void IsLikelyCutOff_BalancedBracesAreOk()
        {
            string code = "public class Test {\n    public void Run() {\n        var x = 1;\n    }\n}";

            Assert.True(HasBalancedBraces(code));
        }

        [Fact]
        public void ExtractCSharpCode_ExtractsFromFencedBlock()
        {
            string text = "Here is the code:\n```csharp\npublic class Test { }\n```\nDone.";

            string code = ExtractCSharpCode(text);
            Assert.Equal("public class Test { }", code);
        }

        [Fact]
        public void ExtractCSharpCode_ReturnsNullWhenNoCode()
        {
            string text = "This is just a text response with no code.";

            string code = ExtractCSharpCode(text);
            Assert.Null(code);
        }

        [Fact]
        public void ExtractCSharpCode_HandlesUnclosedFence()
        {
            string text = "```csharp\npublic class Test {";

            string code = ExtractCSharpCode(text);
            Assert.Equal("public class Test {", code);
        }

        [Fact]
        public void MergeCodeParts_MergesTwoCodeBlocks()
        {
            string original = "Here:\n```csharp\npublic class Test {\n```";
            string continuation = "```csharp\n    public void Run() { }\n}\n```";

            string merged = MergeCodeParts(original, continuation);
            Assert.Contains("public class Test {", merged);
            Assert.Contains("public void Run() { }", merged);
        }

        [Fact]
        public void MergeCodeParts_FallsBackToTextConcat()
        {
            string original = "Some text response";
            string continuation = " that continues here.";

            string merged = MergeCodeParts(original, continuation);
            Assert.Contains("Some text response", merged);
            Assert.Contains("that continues here", merged);
        }

        #region Helper methods (mirroring StreamingResponseHandler private methods)

        private bool HasBalancedBraces(string code)
        {
            int depth = 0;
            foreach (char c in code)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
                if (depth < 0) return false;
            }
            return depth == 0;
        }

        private string ExtractCSharpCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] markers = { "```csharp", "```cs", "```C#" };
            foreach (var marker in markers)
            {
                int start = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (start < 0) continue;
                start = text.IndexOf('\n', start);
                if (start < 0) continue;
                start++;
                int end = text.IndexOf("```", start, StringComparison.Ordinal);
                if (end < 0) end = text.Length;
                return text.Substring(start, end - start).Trim();
            }
            return null;
        }

        private string MergeCodeParts(string original, string continuation)
        {
            if (string.IsNullOrEmpty(continuation)) return original;
            string origCode = ExtractCSharpCode(original);
            string contCode = ExtractCSharpCode(continuation);
            if (!string.IsNullOrEmpty(origCode) && !string.IsNullOrEmpty(contCode))
            {
                string mergedCode = origCode + "\n" + contCode;
                int codeStart = original.IndexOf("```csharp", StringComparison.OrdinalIgnoreCase);
                if (codeStart < 0) codeStart = original.IndexOf("```cs", StringComparison.OrdinalIgnoreCase);
                string prefix = codeStart >= 0 ? original.Substring(0, codeStart) : "";
                return prefix + "```csharp\n" + mergedCode + "\n```";
            }
            return original + "\n" + continuation;
        }

        private int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int idx = 0;
            while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += pattern.Length;
            }
            return count;
        }

        #endregion
    }
}
