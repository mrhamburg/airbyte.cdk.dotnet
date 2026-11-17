#nullable enable
using System;
using System.Collections.Generic;
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
                if (CheckConnection(logger, config, out var exc))
                    return new AirbyteConnectionStatus { Status = Status.SUCCEEDED };

                throw exc ?? new InvalidOperationException(
                    "The connection check failed but did not provide an error.");
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

            var stateEmitter = new StateEmitter(channel, PartitionTracker);
            var trackingChannel = new StateTrackingChannelWriter(channel, stateEmitter, PartitionTracker);
            Channel = trackingChannel;

            // The caller owns the logger. The entry point supplies a channel-backed logger so logs and
            // traces stay ordered with records/state; direct hosts may supply their own sink.
            trackingChannel.Logger = logger;

            var stateManager = new ConnectorStateManager(StateReader.Parse(state));
            var context = new SourceReadContext(logger, trackingChannel, config, catalog, state, PartitionTracker,
                stateEmitter, trackingChannel, stateManager);
            var reader = new SourceStreamReader(context);

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
                    await reader.ReadStreamAsync(streamInstance, configuredStream, streamsInstances);
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
