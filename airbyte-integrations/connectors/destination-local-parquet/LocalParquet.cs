using System;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using destination_local_parquet.Parquet;

namespace destination_local_parquet
{
    public class LocalParquet : AbstractDestination
    {
        private ParquetRecordWriter? _writer;

        public static async Task Main(string[] args) => await AirbyteEntrypoint.Main(args);

        public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config)
        {
            try
            {
                DestinationOptions.FromJson(config).EnsureReady();
                return new AirbyteConnectionStatus { Status = Status.SUCCEEDED };
            }
            catch (Exception e)
            {
                return new AirbyteConnectionStatus { Status = Status.FAILED, Message = e.Message };
            }
        }

        public override async Task Write(AirbyteLogger logger, JsonElement config, ConfiguredAirbyteCatalog catalog,
            AirbyteMessage msg)
        {
            var writer = _writer ??= new ParquetRecordWriter(DestinationOptions.FromJson(config), catalog, logger);
            await writer.WriteAsync(msg);
        }
    }
}
