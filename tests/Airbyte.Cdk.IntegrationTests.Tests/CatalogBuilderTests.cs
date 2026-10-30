#nullable enable
using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class CatalogBuilderTests
    {
        private const string Discover = @"
{
  ""streams"": [
    {
      ""name"": ""symbols"",
      ""json_schema"": { ""type"": ""object"" },
      ""supported_sync_modes"": [""full_refresh""]
    },
    {
      ""name"": ""historical"",
      ""json_schema"": { ""type"": ""object"" },
      ""supported_sync_modes"": [""full_refresh"", ""incremental""],
      ""source_defined_cursor"": true,
      ""default_cursor_field"": [""date""]
    }
  ]
}";

        private static JsonElement Catalog() => JsonDocument.Parse(Discover).RootElement.Clone();

        [Fact]
        public void Test_Build_IncludesOnlyRequestedStreams()
        {
            var json = CatalogBuilder.Build(Catalog(), new[] { new StreamSpec("symbols") });

            var streams = JsonDocument.Parse(json).RootElement.GetProperty("streams");
            streams.GetArrayLength().ShouldBe(1);
            streams[0].GetProperty("stream").GetProperty("name").GetString().ShouldBe("symbols");
            streams[0].GetProperty("sync_mode").GetString().ShouldBe("full_refresh");
            streams[0].GetProperty("destination_sync_mode").GetString().ShouldBe("overwrite");
        }

        [Fact]
        public void Test_Build_UsesAppendAndCursorForIncremental()
        {
            var json = CatalogBuilder.Build(Catalog(),
                new[] { new StreamSpec("historical", "incremental") });

            var stream = JsonDocument.Parse(json).RootElement.GetProperty("streams")[0];
            stream.GetProperty("sync_mode").GetString().ShouldBe("incremental");
            stream.GetProperty("destination_sync_mode").GetString().ShouldBe("append");
            stream.GetProperty("cursor_field").EnumerateArray().Select(x => x.GetString())
                .ShouldBe(new[] { "date" });
        }

        [Fact]
        public void Test_Build_PrefersConfiguredCursorField()
        {
            var json = CatalogBuilder.Build(Catalog(),
                new[] { new StreamSpec("historical", "incremental", CursorField: new[] { "amount" }) });

            JsonDocument.Parse(json).RootElement.GetProperty("streams")[0]
                .GetProperty("cursor_field")[0].GetString().ShouldBe("amount");
        }

        [Fact]
        public void Test_Build_ThrowsForUnknownStream()
        {
            var error = Should.Throw<ConnectorSpecException>(() =>
                CatalogBuilder.Build(Catalog(), new[] { new StreamSpec("missing") }));

            error.Message.ShouldContain("missing");
            error.Message.ShouldContain("symbols");
        }
    }
}