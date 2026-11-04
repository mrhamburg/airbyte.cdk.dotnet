using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Models;
using Shouldly;
using Xunit;
using static Destination.OpenDalParquet.Tests.TestData;
using OpenDalParquetConnector = destination_opendal_parquet.OpenDalParquet;

namespace Destination.OpenDalParquet.Tests
{
    public class OpenDalParquetTests
    {
        [OpenDalAvailableFact]
        public async Task Test_Write_RecordThenState_WritesParquetFile()
        {
            using var dir = new TempDirectory();
            var connector = new OpenDalParquetConnector();
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

        [Fact]
        public async Task Test_Write_WithInjectedStore_WritesObject()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var connector = new OpenDalParquetConnector
            {
                OptionsFactory = TestData.OptionsFromJson,
                StoreFactory = _ => store
            };
            var config = TestData.Json($$"""{"output_path":"{{dir.Path}}"}""");
            var catalog = TestData.Catalog("latest", TestData.Schema);
            var logger = new AirbyteLogger(_ => { });

            await connector.Write(logger, config, catalog,
                TestData.Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await connector.Write(logger, config, catalog, TestData.State());
            await connector.CompleteAsync();

            store.Keys.ShouldHaveSingleItem();
            store.Keys[0].ShouldEndWith(".parquet");
        }
    }
}
