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

            var stateEmitter = new StateEmitter(channel, PartitionTracker);
            var trackingChannel = new StateTrackingChannelWriter(channel, stateEmitter, PartitionTracker);
            Channel = trackingChannel;

            // The caller owns the logger. The entry point supplies a channel-backed logger so logs and
            // traces stay ordered with records/state; direct hosts may supply their own sink.
            Logger = logger;
            trackingChannel.Logger = logger;

            var stateManager = new ConnectorStateManager(StateReader.Parse(state));

            logger.Info($"Starting syncing {Name}");
            var streamsInstances = Streams(config);
            var concurrency = Concurrency(config);

            if (concurrency <= 1)
            {
                var parentRecords = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
                foreach (var configuredStream in catalog.Streams)
                {
                    var streamInstance = ResolveStream(configuredStream, streamsInstances);
                    await ReadStreamSafely(logger, streamInstance, configuredStream,
                        streamsInstances, parentRecords, stateManager, stateEmitter, trackingChannel);
                }

                return;
            }

            await ReadConcurrently(logger, ResolveStreams(catalog, streamsInstances), streamsInstances,
                stateManager, stateEmitter, channel, concurrency);
        }

        private int Concurrency(JsonElement config) =>
            config.TryGetProperty("_concurrency", out var element) &&
            element.TryGetInt32(out var concurrency) && concurrency > 0
                ? concurrency
                : Math.Max(1, MaxConcurrency);

        private static List<ResolvedStream> ResolveStreams(ConfiguredAirbyteCatalog catalog, Stream[] streamsInstances)
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
            Stream[] streamsInstances, ConnectorStateManager stateManager, StateEmitter stateEmitter,
            ChannelWriter<AirbyteMessage> channel, int concurrency)
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
                    var parentRecords = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
                    foreach (var configuredStream in group)
                    {
                        var streamChannel = new StateTrackingChannelWriter(channel, stateEmitter, PartitionTracker)
                        {
                            Logger = logger
                        };
                        await ReadStreamSafely(logger, configuredStream.Stream, configuredStream.Configured,
                            streamsInstances, parentRecords, stateManager, stateEmitter, streamChannel);
                    }
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

        private async Task ReadStreamSafely(AirbyteLogger logger, Stream streamInstance,
            ConfiguredAirbyteStream configuredStream, Stream[] streamsInstances,
            Dictionary<string, IReadOnlyList<JsonElement>> parentRecords, ConnectorStateManager stateManager,
            StateEmitter stateEmitter, ChannelWriter<AirbyteMessage> channel)
        {
            try
            {
                await ReadStream(logger, streamInstance, configuredStream, streamsInstances, parentRecords,
                    stateManager, stateEmitter, channel);
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

        private async Task ReadStream(AirbyteLogger logger, Stream streaminstance, ConfiguredAirbyteStream configuredstream,
            Stream[] streamsInstances, Dictionary<string, IReadOnlyList<JsonElement>> parentRecords,
            ConnectorStateManager stateManager, StateEmitter stateEmitter, ChannelWriter<AirbyteMessage> channel)
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
            PartitionTracker?.BeginStream(descriptor);
            if (channel is StateTrackingChannelWriter trackingChannel)
                trackingChannel.CurrentDescriptor = descriptor;

            logger.TraceStreamStatus(descriptor, AirbyteStreamStatus.STARTED);

            // A stream other streams depend on keeps its records so the substreams do not read it a second time.
            var streamChannel = channel;
            var collector = default(RecordCollectingChannelWriter);
            var cached = false;
            IReadOnlyList<JsonElement>? parentCache = null;
            if (streaminstance.Dependency == null && parentRecords.TryGetValue(streaminstance.Name, out var found))
            {
                cached = true;
                parentCache = found;
            }
            if (!cached && streamsInstances.Any(x => x.Dependency?.ParentStream == streaminstance.Name))
            {
                collector = new RecordCollectingChannelWriter(channel);
                streamChannel = collector;
            }

            try
            {
                long recordcount;
                if (streaminstance.Dependency != null)
                    recordcount = await ReadSubstream(logger, streaminstance, descriptor, configuredstream, streamsInstances,
                        parentRecords, stateElement, recordlimit, stateEmitter, streamChannel);
                else if (cached)
                    recordcount = await ReplayCachedRecords(streaminstance, parentCache!, streamChannel, recordlimit);
                else if (configuredstream.SyncMode == SyncMode.incremental && streaminstance.SupportsIncremental)
                    recordcount = await ReadIncremental(logger, streaminstance, configuredstream, stateElement, recordlimit, streamChannel);
                else
                    recordcount = await streaminstance.ReadRecords(logger, SyncMode.full_refresh, streamChannel, stateElement, recordlimit,
                        configuredstream.CursorField!, null!);

                if (collector != null)
                    parentRecords[streaminstance.Name] = collector.Records;

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

        private async Task<long> ReadIncremental(AirbyteLogger logger, Stream streaminstance, ConfiguredAirbyteStream configuredstream, JsonElement stateElement, long? recordlimit = null,
            ChannelWriter<AirbyteMessage>? channel = null)
        {
            var slices = streaminstance.StreamSlices(SyncMode.incremental, configuredstream.CursorField!, stateElement);
            return await streaminstance.ReadRecords(logger, SyncMode.incremental, channel ?? Channel, stateElement, recordlimit, null!, slices);
        }

        /// <summary>
        /// Reads a substream once per record of its parent. The parent key is passed as a stream slice, which the
        /// stream uses to build its request, for example <c>projects/{project_id}/line_items</c>.
        /// </summary>
        private async Task<long> ReadSubstream(AirbyteLogger logger, Stream streaminstance, StreamDescriptor descriptor,
            ConfiguredAirbyteStream configuredstream, Stream[] streamsInstances,
            Dictionary<string, IReadOnlyList<JsonElement>> parentRecords, JsonElement stateElement, long? recordlimit,
            StateEmitter stateEmitter, ChannelWriter<AirbyteMessage> channel)
        {
            var dependency = streaminstance.Dependency!;

            if (recordlimit is <= 0)
                return 0;

            var parents = await ParentRecords(logger, streaminstance, dependency, streamsInstances, parentRecords);
            logger.Info($"Reading substream {streaminstance.Name} for {parents.Count} {dependency.ParentStream} record(s)");

            // A substream keeps the state of the stream it is, not one cursor per parent. The cursor therefore has
            // to move forward monotonically across parents, so the last emitted state is fed to the next parent and
            // the stream's GetUpdatedState decides how to merge it.
            var incremental = configuredstream.SyncMode == SyncMode.incremental && streaminstance.SupportsIncremental;
            var state = stateElement;

            long recordcount = 0;
            foreach (var parent in parents)
            {
                var key = ExtractKey(parent, dependency.ParentKeyField);
                if (string.IsNullOrWhiteSpace(key))
                {
                    logger.Warn(
                        $"Skipping a {dependency.ParentStream} record without '{dependency.ParentKeyField}' for substream {streaminstance.Name}");
                    continue;
                }

                var remaining = recordlimit.HasValue ? recordlimit.Value - recordcount : (long?)null;
                if (remaining is <= 0)
                    break;

                var slice = incremental
                    ? streaminstance.StreamSlices(SyncMode.incremental, configuredstream.CursorField!, state)
                    : new Dictionary<string, object>();
                slice ??= new Dictionary<string, object>();
                slice[dependency.SliceParameter] = key!;

                recordcount += await streaminstance.ReadRecords(logger,
                    incremental ? SyncMode.incremental : SyncMode.full_refresh, channel, state, remaining,
                    configuredstream.CursorField, slice);

                if (incremental && stateEmitter.TryGetLastState(descriptor, out var emitted))
                    state = emitted;
            }

            return recordcount;
        }

        /// <summary>
        /// Emits the records of a parent that was already read for one of its substreams, so a catalog that lists
        /// the substream first does not read the parent twice.
        /// </summary>
        private static async Task<long> ReplayCachedRecords(Stream streaminstance, IReadOnlyList<JsonElement> records,
            ChannelWriter<AirbyteMessage> channel, long? recordlimit)
        {
            var replay = recordlimit.HasValue ? records.Take((int)Math.Min(recordlimit.Value, int.MaxValue)) : records;
            long recordcount = 0;

            foreach (var record in replay)
            {
                await channel.WriteAsync(AsAirbyteMessage(streaminstance.Name, streaminstance.Namespace, record));
                recordcount++;
            }

            return recordcount;
        }

        /// <summary>
        /// Returns the parent records, reading the parent once and reusing the result for every substream that
        /// depends on it. A parent that is not part of the catalog is read here without emitting records.
        /// </summary>
        private async Task<IReadOnlyList<JsonElement>> ParentRecords(AirbyteLogger logger, Stream substream,
            SubstreamDependency dependency, Stream[] streamsInstances,
            Dictionary<string, IReadOnlyList<JsonElement>> parentRecords)
        {
            if (parentRecords.TryGetValue(dependency.ParentStream, out var cached))
                return cached;

            var parent = streamsInstances.FirstOrDefault(x => x.Name == dependency.ParentStream)
                         ?? throw new Exception(
                             $"Stream '{substream.Name}' depends on '{dependency.ParentStream}', which the source does not provide. " +
                             $"Available streams: {string.Join(", ", streamsInstances.Select(x => x.Name))}");

            if (parent.Dependency != null)
                throw new Exception(
                    $"Stream '{parent.Name}' is itself a substream of '{parent.Dependency.ParentStream}'; nested substreams are not supported");

            logger.Info($"Reading parent stream {parent.Name} for substream {substream.Name}");

            var channel = System.Threading.Channels.Channel.CreateUnbounded<AirbyteMessage>();
            var collecting = Task.Run(async () =>
            {
                var records = new List<JsonElement>();
                await foreach (var message in channel.Reader.ReadAllAsync())
                    if (message.Type == Type.RECORD && message.Record?.Data.ValueKind == JsonValueKind.Object)
                        records.Add(message.Record.Data);
                return (IReadOnlyList<JsonElement>)records;
            });

            parent.StateEmitter = new StateEmitter(channel.Writer);
            try
            {
                await parent.ReadRecords(logger, SyncMode.full_refresh, channel.Writer, "{}".AsJsonElement(), null, null!, null!);
            }
            finally
            {
                // Completing in a finally keeps the collecting task from waiting forever when the parent read throws.
                channel.Writer.TryComplete();
            }

            var records = await collecting;
            parentRecords[dependency.ParentStream] = records;
            return records;
        }

        private static string? ExtractKey(JsonElement record, string field)
        {
            var current = record;
            foreach (var part in field.Split('.'))
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                    return null;

            return current.ValueKind switch
            {
                JsonValueKind.String => current.GetString(),
                JsonValueKind.Number => current.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
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
