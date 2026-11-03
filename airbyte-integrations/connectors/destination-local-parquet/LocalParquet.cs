using System;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using destination_local_parquet.OpenDal;
using destination_local_parquet.Parquet;

namespace destination_local_parquet
{
    public class LocalParquet : AbstractDestination
    {
        private ParquetRecordWriter? _writer;
        private IObjectStore? _store;

        internal Func<JsonElement, DestinationOptions> OptionsFactory { get; set; } =
            DestinationOptions.FromJson;

        internal Func<DestinationOptions, IObjectStore> StoreFactory { get; set; } =
            options => new OpenDalStore(options.OpenDal, options.OutputPrefix);

        public static async Task Main(string[] args) => await AirbyteEntrypoint.Main(args);

        public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config)
        {
            try
            {
                var options = OptionsFactory(config);
                using var store = StoreFactory(options);
                store.EnsureReadyAsync().GetAwaiter().GetResult();
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
                var options = OptionsFactory(config);
                _store = StoreFactory(options);
                _writer = new ParquetRecordWriter(options, catalog, logger, _store);
            }

            await _writer.WriteAsync(msg);
        }

        public override async Task CompleteAsync()
        {
            try
            {
                if (_writer is not null)
                    await _writer.CompleteAsync();
            }
            finally
            {
                _store?.Dispose();
                _store = null;
                _writer = null;
            }
        }
    }
}
