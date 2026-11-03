using System;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;
using destination_local_parquet;

namespace Destination.LocalParquet.Tests
{
    internal static class TestData
    {
        public const string Schema = """
            {"type":"object","properties":{
              "amount":{"type":"number"},
              "base":{"type":"string"},
              "date":{"type":"string"},
              "rates":{"type":"object","additionalProperties":{"type":["number","null"]}}
            },"required":["amount","base","date","rates"]}
            """;

        public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        public static DestinationOptions Options(string root) =>
            DestinationOptions.FromJson(Json($$"""{"output_path":"{{root}}"}"""));

        public static ConfiguredAirbyteCatalog Catalog(string streamName, string jsonSchema,
            DestinationSyncMode mode = DestinationSyncMode.overwrite) => new()
        {
            Streams = new[]
            {
                new ConfiguredAirbyteStream
                {
                    Stream = new AirbyteStream
                    {
                        Name = streamName,
                        JsonSchema = JsonDocument.Parse(jsonSchema).RootElement.Clone()
                    },
                    SyncMode = SyncMode.full_refresh,
                    DestinationSyncMode = mode
                }
            }
        };

        public static AirbyteMessage Record(string stream, string json) => new()
        {
            Type = Airbyte.Cdk.Models.Type.RECORD,
            Record = new AirbyteRecordMessage
            {
                Stream = stream,
                EmittedAt = 0,
                Data = JsonDocument.Parse(json).RootElement.Clone()
            }
        };

        public static AirbyteMessage State() => new()
        {
            Type = Airbyte.Cdk.Models.Type.STATE,
            State = new AirbyteStateMessage()
        };

        public sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    "parquet-writer-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose() => Directory.Delete(Path, true);
        }
    }
}
