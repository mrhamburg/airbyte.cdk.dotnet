using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;
using destination_opendal_parquet;
using destination_opendal_parquet.OpenDal;

namespace Destination.OpenDalParquet.Tests
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

        public static readonly IReadOnlyDictionary<string, string> EmptyEnvironment =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        public static DestinationOptions Options(string root, int? flushRowLimit = null,
            bool? flushOnState = null, string? fileNameTemplate = null)
        {
            var properties = new List<string> { $"\"output_path\":{JsonSerializer.Serialize(root)}" };
            if (flushRowLimit.HasValue)
                properties.Add($"\"flush_row_limit\":{flushRowLimit.Value}");
            if (flushOnState.HasValue)
                properties.Add($"\"flush_on_state\":{(flushOnState.Value ? "true" : "false")}");
            if (fileNameTemplate is not null)
                properties.Add($"\"file_name_template\":{JsonSerializer.Serialize(fileNameTemplate)}");

            return OptionsFromJson(Json("{" + string.Join(",", properties) + "}"));
        }

        public static DestinationOptions OptionsFromJson(JsonElement config) =>
            DestinationOptions.FromJson(config, EmptyEnvironment, _ => null);

        public static global::destination_opendal_parquet.OpenDalParquet Connector(IObjectStore store) => new()
        {
            OptionsFactory = OptionsFromJson,
            StoreFactory = _ => store
        };

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
