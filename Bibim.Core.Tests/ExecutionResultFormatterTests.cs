// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using Xunit;

namespace Bibim.Core.Tests
{
    public class ExecutionResultFormatterTests
    {
        [Fact]
        public void BuildDryRunOutput_ReturnsGeneratedOutput_WhenPresent()
        {
            string result = ExecutionResultFormatter.BuildDryRunOutput(
                "현재 프로젝트 이름: HVAC_MODEL",
                0);

            Assert.Equal("현재 프로젝트 이름: HVAC_MODEL", result);
        }

        [Theory]
        [InlineData(null, 0, "Dry run succeeded. 0 elements would be affected.")]
        [InlineData("", 2, "Dry run succeeded. 2 elements would be affected.")]
        [InlineData("   ", 5, "Dry run succeeded. 5 elements would be affected.")]
        public void BuildDryRunOutput_FallsBackToAffectedCount_WhenOutputIsEmpty(
            string generatedOutput,
            int affectedElementCount,
            string expected)
        {
            string result = ExecutionResultFormatter.BuildDryRunOutput(
                generatedOutput,
                affectedElementCount);

            Assert.Equal(expected, result);
        }
    }
}
