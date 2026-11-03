using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Models;
using Shouldly;
using Xunit;
using static Destination.LocalParquet.Tests.TestData;
using LocalParquetConnector = destination_local_parquet.LocalParquet;

namespace Destination.LocalParquet.Tests
{
    public class LocalParquetTests
    {
        [Fact]
        public async Task Test_Write_RecordThenState_WritesParquetFile()
        {
            using var dir = new TempDirectory();
            var connector = new LocalParquetConnector();
            var config = TestData.Json($$"""{"output_path":"{{dir.Path}}"}""");
            var catalog = new ConfiguredAirbyteCatalog
            {
                Streams = new[]
                {
                    new ConfiguredAirbyteStream
                    {
                        Stream = new AirbyteStream
                        {
                            Name = "latest",
                            JsonSchema = JsonDocument.Parse(TestData.Schema).RootElement.Clone()
                        },
                        SyncMode = SyncMode.full_refresh,
                        DestinationSyncMode = DestinationSyncMode.overwrite
                    }
                }
            };
            var logger = new AirbyteLogger(_ => { });

            await connector.Write(logger, config, catalog,
                new AirbyteMessage
                {
                    Type = Airbyte.Cdk.Models.Type.RECORD,
                    Record = new AirbyteRecordMessage
                    {
                        Stream = "latest",
                        EmittedAt = 0,
                        Data = JsonDocument.Parse("""{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}""").RootElement.Clone()
                    }
                });
            await connector.Write(logger, config, catalog,
                new AirbyteMessage { Type = Airbyte.Cdk.Models.Type.STATE, State = new AirbyteStateMessage() });

            Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").ShouldHaveSingleItem();
        }
    }
}
