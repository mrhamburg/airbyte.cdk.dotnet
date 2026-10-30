#nullable enable
using System.IO;
using System.Linq;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class ChangedConnectorsTests
    {
        [Fact]
        public void Test_FromChangedFiles_ReturnsDistinctConnectorPaths()
        {
            using var repo = new TempRepository("source-alpha", "source-beta");

            var result = ChangedConnectors.FromChangedFiles(new[]
            {
                "airbyte-integrations/connectors/source-alpha/ExchangeRates.cs",
                "airbyte-integrations/connectors/source-alpha/schemas/symbols.json",
                "airbyte-integrations/connectors/source-beta/Dockerfile"
            }, repo.Root);

            result.Count.ShouldBe(2);
            result.ShouldContain(Path.Combine(repo.Root, "airbyte-integrations", "connectors", "source-alpha"));
            result.ShouldContain(Path.Combine(repo.Root, "airbyte-integrations", "connectors", "source-beta"));
        }

        [Fact]
        public void Test_FromChangedFiles_IgnoresPathsOutsideConnectors()
        {
            using var repo = new TempRepository("source-alpha");

            ChangedConnectors.FromChangedFiles(new[]
            {
                "Airbyte.Cdk/Sources/AbstractSource.cs",
                "README.md",
                "tests/Airbyte.Cdk.IntegrationTests/Program.cs"
            }, repo.Root).ShouldBeEmpty();
        }

        [Fact]
        public void Test_FromChangedFiles_IgnoresDeletedConnectors()
        {
            using var repo = new TempRepository("source-alpha");

            ChangedConnectors.FromChangedFiles(new[]
            {
                "airbyte-integrations/connectors/source-gone/Program.cs"
            }, repo.Root).ShouldBeEmpty();
        }

        [Fact]
        public void Test_FromChangedFiles_IgnoresTheConnectorsRootItself()
        {
            using var repo = new TempRepository("source-alpha");

            ChangedConnectors.FromChangedFiles(new[]
            {
                "airbyte-integrations/connectors/README.md"
            }, repo.Root).ShouldBeEmpty();
        }
    }
}