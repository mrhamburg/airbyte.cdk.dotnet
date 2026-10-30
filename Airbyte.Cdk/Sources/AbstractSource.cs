#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Streams.Http;
using Airbyte.Cdk.Sources.Utils;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Sources
{
    /// <summary>
    /// Abstract base class for an Airbyte Source. Consumers should implement any abstract methods
    /// in this class to create an Airbyte Specification compliant Source.
    /// </summary>
    public abstract class AbstractSource : Source
    {
        protected AirbyteLogger Logger { get; private set; } = null!;

        protected ChannelWriter<AirbyteMessage> Channel { get; private set; } = null!;

        protected JsonElement Config { get; private set; }

        protected JsonElement State { get; private set; }

        protected ConfiguredAirbyteCatalog Catalog { get; private set; } = null!;

        /// <summary>
        /// Check if a connection can be established
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="config">The user-provided configuration as specified by the source's spec. This usually contains information required to check connection e.g. tokens, secrets and keys etc.</param>
        /// <returns>A Dictionary of (boolean, error). If boolean is true, then the connection check is successful and we can connect to the underlying data
        /// source using the provided configuration.</returns>
        public abstract bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc);

        /// <summary>
        /// An array of the streams in this source connector.
        /// </summary>
        /// <param name="config">The user-provided configuration as specified by the source's spec. Any stream construction related operation should happen here.</param>
        /// <returns></returns>
        public abstract Stream[] Streams(JsonElement config);

        public string Name
        {
            get => GetType().Name;
        }

        /// <summary>
        /// Implements the Discover operation from the Airbyte Specification. See https://docs.airbyte.io/architecture/airbyte-specification.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="config"></param>
        /// <returns></returns>
        public override AirbyteCatalog Discover(AirbyteLogger logger, JsonElement config)
            => new() { Streams = Streams(config).Select(x => x.AsAirbyteStream()).ToArray() };

        /// <summary>
        /// Implements the Check Connection operation from the Airbyte Specification. See https://docs.airbyte.io/architecture/airbyte-specification.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="config"></param>
        /// <returns></returns>
        public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config)
        {
            try
            {
                return CheckConnection(logger, config, out var exc)
                    ? new AirbyteConnectionStatus { Status = Status.SUCCEEDED }
                    : throw exc;
            }
            catch (Exception e)
            {
                return new AirbyteConnectionStatus { Status = Status.FAILED, Message = e.Message };
            }
        }

        /// <summary>
        /// Implements the Read operation from the Airbyte Specification. See https://docs.airbyte.io/architecture/airbyte-specification.
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="channel"></param>
        /// <param name="config"></param>
        /// <param name="catalog"></param>
        /// <param name="state"></param>
        /// <returns></returns>
        public override async Task Read(AirbyteLogger logger, ChannelWriter<AirbyteMessage> channel,
            JsonElement config,
            ConfiguredAirbyteCatalog catalog, JsonElement state)
        {
            //Set values
            Logger = logger;
            Channel = channel;
            Config = config;
            Catalog = catalog;
            State = state;

            var stateEmitter = new StateEmitter(channel);
            var trackingChannel = new StateTrackingChannelWriter(channel, stateEmitter);
            Channel = trackingChannel;

            // The caller owns the logger. The entry point supplies a channel-backed logger so logs and
            // traces stay ordered with records/state; direct hosts may supply their own sink.
            Logger = logger;
            trackingChannel.Logger = logger;

            var stateManager = new ConnectorStateManager(StateReader.Parse(state));

            logger.Info($"Starting syncing {Name}");
            var streamsInstances = Streams(config);
            foreach (var configuredStream in catalog.Streams)
            {
                if (configuredStream.Stream == null)
                    throw new Exception("The configured catalog contains a stream without a stream definition");

                var streamInstance = streamsInstances.FirstOrDefault(x =>
                    x.Name == configuredStream.Stream.Name &&
                    string.Equals(x.Namespace ?? string.Empty,
                        configuredStream.Stream.Namespace ?? string.Empty, StringComparison.Ordinal));
                if (streamInstance == null)
                    throw new Exception(
                        $"The requested stream {configuredStream.Stream.Name} was not found in the source. Available streams: {string.Join(", ", streamsInstances.Select(x => x.Name))}");

                try
                {
                    await ReadStream(logger, streamInstance, configuredStream, stateManager, stateEmitter);
                }
                catch (Exception e)
                {
                    logger.TraceError("The connector failed", $"{e.GetType().Name}: {e.Message}",
                        e.StackTrace, AirbyteFailureType.system_error, new StreamDescriptor
                        {
                            Name = streamInstance.Name,
                            Namespace = streamInstance.Namespace
                        });
                    e.Data[AirbyteLogger.TraceEmittedDataKey] = true;
                    throw;
                }
            }
        }

        private async Task ReadStream(AirbyteLogger logger, Stream streaminstance, ConfiguredAirbyteStream configuredstream,
            ConnectorStateManager stateManager, StateEmitter stateEmitter)
        {
            var descriptor = new StreamDescriptor
            {
                Name = streaminstance.Name,
                Namespace = streaminstance.Namespace
            };

            if (Config.TryGetProperty("_page_size", out var pagesizeElement) &&
                streaminstance is HttpStream stream && pagesizeElement.TryGetInt32(out int pagesize))
            {
                Logger.Info($"Setting page size for {Name} to {pagesize}");
                stream.PageSize = pagesize;
            }

            long? recordlimit = Config.TryGetProperty("_limit", out var limitElement) && limitElement.TryGetInt64(out var limit) ? limit : null;
            Logger.Info($"Syncing stream: {streaminstance.Name}");

            var streamname = configuredstream.Stream!.Name;
            var stateElement = stateManager.GetStreamState(streaminstance.Name, streaminstance.Namespace);
            Logger.Info($"Setting state of {streamname} stream to {stateElement.GetRawText()}");

            streaminstance.StateEmitter = stateEmitter;
            if (Channel is StateTrackingChannelWriter trackingChannel)
                trackingChannel.CurrentDescriptor = descriptor;

            logger.TraceStreamStatus(descriptor, AirbyteStreamStatus.STARTED);

            try
            {
                long recordcount;
                if (configuredstream.SyncMode == SyncMode.incremental && streaminstance.SupportsIncremental)
                    recordcount = await ReadIncremental(logger, streaminstance, configuredstream, stateElement, recordlimit);
                else
                    recordcount = await streaminstance.ReadRecords(logger, SyncMode.full_refresh, Channel, stateElement, recordlimit,
                        configuredstream.CursorField!, null!);

                // Only emit the fallback terminal state on success. A failed stream may already have emitted
                // checkpoints, and emitting a stale state after a failure could move the cursor backwards.
                if (!stateEmitter.HasEmitted(descriptor))
                    await stateEmitter.EmitAsync(descriptor, stateElement, recordcount);

                logger.TraceStreamStatus(descriptor, AirbyteStreamStatus.COMPLETE);
                Logger.Info($"Read {recordcount} records from {streaminstance.Name} stream");
            }
            catch (Exception)
            {
                logger.TraceStreamStatus(descriptor, AirbyteStreamStatus.INCOMPLETE);
                throw;
            }
        }

        private async Task<long> ReadIncremental(AirbyteLogger logger, Stream streaminstance, ConfiguredAirbyteStream configuredstream, JsonElement stateElement, long? recordlimit = null)
        {
            var slices = streaminstance.StreamSlices(SyncMode.incremental, configuredstream.CursorField!, stateElement);
            return await streaminstance.ReadRecords(logger, SyncMode.incremental, Channel, stateElement, recordlimit, null!, slices);
        }

        public static AirbyteMessage AsAirbyteMessage(string streamname, JsonElement data) =>
            AsAirbyteMessage(streamname, null, data);

        public static AirbyteMessage AsAirbyteMessage(string streamname, string? namespaceName, JsonElement data) => new()
        {
            Type = Type.RECORD,
            Record = new()
            {
                EmittedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = data,
                Stream = streamname,
                Namespace = namespaceName
            }
        };
    }
}
