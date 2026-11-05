#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        public void Test_Load_KeepsUnquotedScalarTypes()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
tests:
  - name: typed config
    config:
      retries: 3
      enabled: true
      ratio: 1.5
      base: ""USD""
    streams:
      - name: symbols
");

            var config = ConnectorSpecLoader.Load(connector.Root).Tests[0].Config;

            config.GetProperty("retries").ValueKind.ShouldBe(JsonValueKind.Number);
            config.GetProperty("enabled").ValueKind.ShouldBe(JsonValueKind.True);
            config.GetProperty("ratio").ValueKind.ShouldBe(JsonValueKind.Number);
            config.GetProperty("base").ValueKind.ShouldBe(JsonValueKind.String);
        }

        [Fact]
        public void Test_Load_RejectsNonPositiveLimit()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
tests:
  - name: capped
    limit: 0
    streams:
      - name: symbols
");

            Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
        }

        [Fact]
        public void Test_Load_RejectsDuplicateKeys()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
tests:
  - name: capped
    limit: 5
    limit: 7
    streams:
      - name: symbols
");

            Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
        }

        [Fact]
        public void Test_Load_RejectsNonPositiveTimeout()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
tests:
  - name: slow
    timeout_seconds: 0
    streams:
      - name: symbols
");

            Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
        }

        [Fact]
        public void Test_Load_RejectsMinRecordsAboveLimit()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
tests:
  - name: impossible
    limit: 5
    streams:
      - name: symbols
        min_records: 10
");

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
            error.Message.ShouldContain("can never pass");
        }

        [Fact]
        public void Test_Load_RejectsBlankSecretEntries()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
secrets:
  apikey: ""  ""
tests:
  - name: with secrets
    streams:
      - name: symbols
");

            Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
        }

        [Fact]
        public void Test_Load_RejectsDuplicateStreamNamesAfterTrimming()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(@"
tests:
  - name: dupes
    streams:
      - name: symbols
      - name: "" symbols ""
");

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

        private const string DestinationSpec = @"
connector:
  module: OpenDalParquet
  type: destination
source:
  connector: ../source-example
  config:
    base: USD
tests:
  - name: full refresh
    config: {}
    streams:
      - name: symbols
";

        [Fact]
        public void Test_Load_DestinationTypeAndSourceBlock()
        {
            var repo = new TempRepository("destination-example", "source-example");
            var destination = Path.Combine(repo.Root, "airbyte-integrations", "connectors", "destination-example");
            var source = Path.Combine(repo.Root, "airbyte-integrations", "connectors", "source-example");
            File.WriteAllText(Path.Combine(destination, "integration_tests.yaml"), DestinationSpec);
            File.WriteAllText(Path.Combine(destination, "destination_example.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            File.WriteAllText(Path.Combine(source, "Dockerfile"), "ENV AIRBYTE_IMPL_MODULE=\"ExchangeRates\"\n");
            File.WriteAllText(Path.Combine(source, "source_example.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

            var spec = ConnectorSpecLoader.Load(destination);

            spec.Type.ShouldBe(ConnectorType.Destination);
            spec.Source!.ConnectorDirectory.ShouldBe(Path.GetFullPath(source));
            spec.Source.Config.GetProperty("base").GetString().ShouldBe("USD");
            spec.Tests[0].Socket.ShouldBeTrue();
        }

        [Fact]
        public void Test_Load_DestinationWithoutProject_Throws()
        {
            var repo = new TempRepository("destination-example", "source-example");
            var destination = Path.Combine(repo.Root, "airbyte-integrations", "connectors", "destination-example");
            var source = Path.Combine(repo.Root, "airbyte-integrations", "connectors", "source-example");
            File.WriteAllText(Path.Combine(destination, "integration_tests.yaml"), DestinationSpec);
            File.WriteAllText(Path.Combine(source, "Dockerfile"), "ENV AIRBYTE_IMPL_MODULE=\"ExchangeRates\"\n");
            File.WriteAllText(Path.Combine(source, "source_example.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(destination));
            error.Message.ShouldContain("No .csproj found");
        }

        [Fact]
        public void Test_Load_DestinationWithoutSource_Throws()
        {
            using var connector = new TempConnector("destination-example")
                .WithDockerfile("OpenDalParquet")
                .WithProject("destination_example.csproj")
                .WithSpec(@"
connector:
  type: destination
tests:
  - name: t
    streams:
      - name: s
");

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
            error.Message.ShouldContain("source");
        }

        [Fact]
        public void Test_Load_DestinationWithLimit_Throws()
        {
            using var connector = new TempConnector("destination-example")
                .WithDockerfile("OpenDalParquet")
                .WithProject("destination_example.csproj")
                .WithSpec(@"
connector:
  type: destination
source:
  connector: ../source-example
tests:
  - name: t
    limit: 10
    streams:
      - name: s
");

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
            error.Message.ShouldContain("limit");
        }

        [Fact]
        public void Test_Load_DestinationWithSocketFalse_Throws()
        {
            using var connector = new TempConnector("destination-example")
                .WithDockerfile("OpenDalParquet")
                .WithProject("destination_example.csproj")
                .WithSpec(@"
connector:
  type: destination
source:
  connector: ../source-example
tests:
  - name: t
    socket: false
    streams:
      - name: s
");

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
            error.Message.ShouldContain("socket");
        }

        [Fact]
        public void Test_Load_UnknownConnectorType_Throws()
        {
            using var connector = new TempConnector("destination-example")
                .WithDockerfile("OpenDalParquet")
                .WithProject()
                .WithSpec(@"
connector:
  type: worker
tests:
  - name: t
    streams:
      - name: s
");

            var error = Should.Throw<ConnectorSpecException>(() => ConnectorSpecLoader.Load(connector.Root));
            error.Message.ShouldContain("type");
        }

        [Fact]
        public void Test_Load_SourceTypeDefaults()
        {
            using var connector = new TempConnector()
                .WithDockerfile("ExchangeRates")
                .WithProject()
                .WithSpec(MinimalSpec);

            ConnectorSpecLoader.Load(connector.Root).Type.ShouldBe(ConnectorType.Source);
        }
    }
}