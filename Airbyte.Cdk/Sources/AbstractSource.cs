#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
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
        /// Maximum number of streams that may be read at the same time. The default of 1 keeps reads
        /// sequential. Override this in a connector to read independent streams concurrently; streams that
        /// form a parent/substream group are always read in order.
        /// </summary>
        public virtual int MaxConcurrency => 1;

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
            var concurrency = Concurrency(config);

            if (concurrency <= 1)
            {
                // Resolve lazily so a missing stream later in the catalog fails only when it is reached.
                foreach (var configuredStream in catalog.Streams)
                {
                    var streamInstance = ResolveStream(configuredStream, streamsInstances);
                    await ReadStreamSafely(logger, reader, streamInstance, configuredStream, streamsInstances);
                }

                return;
            }

            await ReadConcurrently(logger, ResolveStreams(catalog, streamsInstances), streamsInstances, channel,
                stateEmitter, stateManager, concurrency);
        }

        private int Concurrency(JsonElement config) =>
            config.TryGetProperty("_concurrency", out var element) &&
            element.TryGetInt32(out var concurrency) && concurrency > 0
                ? concurrency
                : Math.Max(1, MaxConcurrency);

        private static List<ResolvedStream> ResolveStreams(ConfiguredAirbyteCatalog catalog,
            Stream[] streamsInstances)
        {
            var resolved = new List<ResolvedStream>(catalog.Streams.Length);
            foreach (var configuredStream in catalog.Streams)
                resolved.Add(new ResolvedStream(ResolveStream(configuredStream, streamsInstances), configuredStream));

            return resolved;
        }

        private static Stream ResolveStream(ConfiguredAirbyteStream configuredStream, Stream[] streamsInstances)
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

            return streamInstance;
        }

        private async Task ReadConcurrently(AirbyteLogger logger, List<ResolvedStream> configuredStreams,
            Stream[] streamsInstances, ChannelWriter<AirbyteMessage> channel, StateEmitter stateEmitter,
            ConnectorStateManager stateManager, int concurrency)
        {
            foreach (var configuredStream in configuredStreams)
            {
                var dependency = configuredStream.Stream.Dependency;
                if (dependency == null)
                    continue;

                var parent = streamsInstances.FirstOrDefault(x => x.Name == dependency.ParentStream);
                if (parent?.Dependency != null)
                    throw new Exception(
                        $"Stream '{parent.Name}' is itself a substream of '{parent.Dependency.ParentStream}'; nested substreams are not supported");
            }

            // Streams that share a parent are one unit: the parent is read once and its records feed every
            // substream, so those streams cannot run beside each other. Independent units run in parallel.
            var groups = GroupStreams(configuredStreams);
            using var gate = new SemaphoreSlim(concurrency, concurrency);
            using var cancellation = new CancellationTokenSource();
            Exception? firstError = null;

            var tasks = groups.Select(async group =>
            {
                try
                {
                    await gate.WaitAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    // Each parallel unit gets its own tracking channel and parent cache; the state emitter
                    // and partition tracker are shared and thread-safe.
                    var streamChannel =
                        new StateTrackingChannelWriter(channel, stateEmitter, PartitionTracker) { Logger = logger };
                    var context = new SourceReadContext(logger, streamChannel, Config, Catalog, State,
                        PartitionTracker, stateEmitter, streamChannel, stateManager);
                    var reader = new SourceStreamReader(context);

                    foreach (var configuredStream in group)
                        await ReadStreamSafely(logger, reader, configuredStream.Stream, configuredStream.Configured,
                            streamsInstances);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref firstError, e, null);
                    cancellation.Cancel();
                    throw;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                // The first stream failure is rethrown below; the other tasks were only observed here
                // so their exceptions are not left unobserved.
            }

            if (firstError != null)
                ExceptionDispatchInfo.Capture(firstError).Throw();
        }

        private static List<List<ResolvedStream>> GroupStreams(List<ResolvedStream> configuredStreams)
        {
            var groups = new List<List<ResolvedStream>>();
            var byParent = new Dictionary<string, List<ResolvedStream>>(StringComparer.Ordinal);

            foreach (var configuredStream in configuredStreams)
            {
                var key = configuredStream.Stream.Dependency?.ParentStream ?? configuredStream.Stream.Name;
                if (!byParent.TryGetValue(key, out var group))
                {
                    group = new List<ResolvedStream>();
                    byParent[key] = group;
                    groups.Add(group);
                }

                group.Add(configuredStream);
            }

            // A configured parent must be read before its substreams so the substreams reuse its cache
            // instead of reading the parent a second time.
            for (var i = 0; i < groups.Count; i++)
                groups[i] = groups[i].OrderBy(x => x.Stream.Dependency == null ? 0 : 1).ToList();

            return groups;
        }

        private static async Task ReadStreamSafely(AirbyteLogger logger, SourceStreamReader reader,
            Stream streamInstance, ConfiguredAirbyteStream configuredStream, Stream[] streamsInstances)
        {
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

        private sealed class ResolvedStream
        {
            public ResolvedStream(Stream stream, ConfiguredAirbyteStream configured)
            {
                Stream = stream;
                Configured = configured;
            }

            public Stream Stream { get; }
            public ConfiguredAirbyteStream Configured { get; }
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
