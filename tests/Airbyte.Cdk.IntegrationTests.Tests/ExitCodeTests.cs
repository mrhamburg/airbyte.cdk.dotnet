#nullable enable
using System.Linq;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class ExitCodeTests
    {
        private static ConnectorResult Result(params Status[] statuses) => new(
            "airbyte-integrations/connectors/source-example",
            statuses.Contains(Status.Failed)
                ? Status.Failed
                : statuses.All(x => x == Status.Skipped) ? Status.Skipped : Status.Passed,
            statuses.Select((status, index) =>
                    new TestResult($"case-{index}", status, System.Array.Empty<StreamResult>(), System.Array.Empty<string>()))
                .ToList(),
            System.Array.Empty<string>());

        [Fact]
        public void Test_Map_ReturnsZero_WhenPassed()
        {
            ExitCodes.Map(Result(Status.Passed), noSkip: false).ShouldBe(0);
        }

        [Fact]
        public void Test_Map_ReturnsOne_WhenFailed()
        {
            ExitCodes.Map(Result(Status.Failed), noSkip: false).ShouldBe(1);
        }

        [Fact]
        public void Test_Map_ReturnsOne_WhenSkippedWithNoSkip()
        {
            ExitCodes.Map(Result(Status.Skipped), noSkip: true).ShouldBe(1);
        }

        [Fact]
        public void Test_Map_ReturnsZero_WhenSkippedAndSkipsAllowed()
        {
            ExitCodes.Map(Result(Status.Skipped), noSkip: false).ShouldBe(0);
        }

        [Fact]
        public void Test_Map_ReturnsOne_WhenOneScenarioIsSkippedAndNoSkip()
        {
            ExitCodes.Map(Result(Status.Passed, Status.Skipped), noSkip: true).ShouldBe(1);
        }

        [Fact]
        public void Test_Map_ReturnsZero_WhenOneScenarioIsSkippedAndSkipsAllowed()
        {
            ExitCodes.Map(Result(Status.Passed, Status.Skipped), noSkip: false).ShouldBe(0);
        }

        [Fact]
        public void Test_Map_ReturnsTwo_WhenSpecInvalid()
        {
            var result = new ConnectorResult(
                "airbyte-integrations/connectors/source-example",
                Status.Failed,
                System.Array.Empty<TestResult>(),
                new[] { ConnectorResult.SpecErrorPrefix + "missing integration_tests.yaml" });

            ExitCodes.Map(result, noSkip: false).ShouldBe(2);
        }
    }
}