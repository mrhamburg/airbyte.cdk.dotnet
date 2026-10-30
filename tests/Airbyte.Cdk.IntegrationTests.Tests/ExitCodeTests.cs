#nullable enable
using System.Linq;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class ExitCodeTests
    {
        private static ConnectorResult Result(Status status) => new(
            "airbyte-integrations/connectors/source-example",
            status,
            new[] { new TestResult("case", status, System.Array.Empty<StreamResult>(), System.Array.Empty<string>()) },
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