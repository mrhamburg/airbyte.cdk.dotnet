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
            if (_writer is null)
            {
                var options = DestinationOptions.FromJson(config);
                _writer = new ParquetRecordWriter(options, catalog, logger, options.FlushRowLimit);
            }

            await _writer.WriteAsync(msg);
        }

        public override async Task CompleteAsync()
        {
            if (_writer is not null)
                await _writer.CompleteAsync();
        }
    }
}
