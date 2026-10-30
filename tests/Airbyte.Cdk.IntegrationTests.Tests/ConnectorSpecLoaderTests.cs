#nullable enable
using System.Linq;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class ConnectorSpecLoaderTests
    {
        private const string MinimalSpec = @"
tests:
  - name: full refresh
    config:
      base: USD
    limit: 50
    streams:
      - name: symbols
      - name: historical
        sync_mode: incremental
        state:
          historical:
            USD: ""2026-10-29""
";

        [Fact]
        public void Test_Load_ParsesTestsStreamsAndConfig()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(MinimalSpec);

            var spec = ConnectorSpecLoader.Load(connector.Root);

            spec.Module.ShouldBe("ExchangeRates");
            spec.ProjectFile.ShouldEndWith("source_example.csproj");
            spec.Tests.Count.ShouldBe(1);

            var test = spec.Tests[0];
            test.Name.ShouldBe("full refresh");
            test.Limit.ShouldBe(50);
            test.Config.GetProperty("base").GetString().ShouldBe("USD");

            test.Streams.Count.ShouldBe(2);
            test.Streams[0].Name.ShouldBe("symbols");
            test.Streams[0].SyncMode.ShouldBe("full_refresh");
            test.Streams[0].MinRecords.ShouldBe(1);

            test.Streams[1].SyncMode.ShouldBe("incremental");
            test.Streams[1].State!.Value.GetProperty("historical").GetProperty("USD").GetString()
                .ShouldBe("2026-10-29");
        }

        [Fact]
        public void Test_Load_DerivesModuleFromDockerfile_WhenOmitted()
        {
            using var connector = new TempConnector()
                .WithDockerfile("Program")
                .WithProject()
                .WithSpec(MinimalSpec);

            ConnectorSpecLoader.Load(connector.Root).Module.ShouldBe("Program");
        }

        [Fact]
        public void Test_Load_RejectsUnknownSyncMode()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
tests:
  - name: bad
    streams:
      - name: symbols
        sync_mode: full_refresh_with_dedup
");

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
            error.Message.ShouldContain("full_refresh_with_dedup");
        }

        [Fact]
        public void Test_Load_RejectsAmbiguousProject()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject("a.csproj")
                .WithProject("b.csproj")
                .WithSpec(MinimalSpec);

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
            error.Message.ShouldContain("project");
        }

        [Fact]
        public void Test_Load_RejectsEmptyTestList()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec("tests: []\n");

            Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
        }

        [Fact]
        public void Test_Load_AcceptsSecretMapping()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
secrets:
  api_key: SOURCE_EXAMPLE_API_KEY
tests:
  - name: with secrets
    streams:
      - name: symbols
");

            var spec = ConnectorSpecLoader.Load(connector.Root);

            spec.Secrets["api_key"].ShouldBe("SOURCE_EXAMPLE_API_KEY");
        }
    }
}