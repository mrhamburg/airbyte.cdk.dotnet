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
    /// State shared by one source read: the output channel, the catalog, the state manager, and the
    /// parent-record cache. Keeping it in one object lets the stream reader stay stateless.
    /// </summary>
    internal sealed class SourceReadContext
    {
        public SourceReadContext(AirbyteLogger logger, ChannelWriter<AirbyteMessage> channel, JsonElement config,
            ConfiguredAirbyteCatalog catalog, JsonElement state, PartitionTracker? partitionTracker,
            StateEmitter stateEmitter, StateTrackingChannelWriter trackingChannel,
            ConnectorStateManager stateManager)
        {
            Logger = logger;
            Channel = channel;
            Config = config;
            Catalog = catalog;
            State = state;
            PartitionTracker = partitionTracker;
            StateEmitter = stateEmitter;
            TrackingChannel = trackingChannel;
            StateManager = stateManager;
        }

        public AirbyteLogger Logger { get; }
        public ChannelWriter<AirbyteMessage> Channel { get; }
        public JsonElement Config { get; }
        public ConfiguredAirbyteCatalog Catalog { get; }
        public JsonElement State { get; }
        public PartitionTracker? PartitionTracker { get; }
        public StateEmitter StateEmitter { get; }
        public StateTrackingChannelWriter TrackingChannel { get; }
        public ConnectorStateManager StateManager { get; }
        public Dictionary<string, IReadOnlyList<JsonElement>> ParentRecords { get; } =
            new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reads one configured stream, including incremental state, substreams, and cached parent
    /// records. <see cref="AbstractSource"/> owns the catalog loop and delegates the per-stream work
    /// to this class.
    /// </summary>
    internal sealed class SourceStreamReader
    {
        private readonly SourceReadContext _context;

        public SourceStreamReader(SourceReadContext context) => _context = context;

        public async Task ReadStreamAsync(Stream streaminstance, ConfiguredAirbyteStream configuredstream,
            Stream[] streamsInstances)
        {
            var descriptor = new StreamDescriptor
            {
                Name = streaminstance.Name,
                Namespace = streaminstance.Namespace
            };

            if (_context.Config.TryGetProperty("_page_size", out var pagesizeElement) &&
                streaminstance is HttpStream stream && pagesizeElement.TryGetInt32(out int pagesize))
            {
                _context.Logger.Info($"Setting page size for {streaminstance.Name} to {pagesize}");
                stream.PageSize = pagesize;
            }

            long? recordlimit = _context.Config.TryGetProperty("_limit", out var limitElement) &&
                                limitElement.TryGetInt64(out var limit)
                ? limit
                : null;
            _context.Logger.Info($"Syncing stream: {streaminstance.Name}");

            var streamname = configuredstream.Stream!.Name;
            var stateElement = _context.StateManager.GetStreamState(streaminstance.Name, streaminstance.Namespace);
            _context.Logger.Info($"Setting state of {streamname} stream to {stateElement.GetRawText()}");

            streaminstance.StateEmitter = _context.StateEmitter;
            _context.PartitionTracker?.BeginStream(descriptor);
            _context.TrackingChannel.CurrentDescriptor = descriptor;

            _context.Logger.TraceStreamStatus(descriptor, AirbyteStreamStatus.STARTED);

            // A stream other streams depend on keeps its records so the substreams do not read it a second time.
            var channel = _context.Channel;
            var collector = default(RecordCollectingChannelWriter);
            var cached = false;
            IReadOnlyList<JsonElement>? parentCache = null;
            if (streaminstance.Dependency == null &&
                _context.ParentRecords.TryGetValue(streaminstance.Name, out var found))
            {
                cached = true;
                parentCache = found;
            }

            if (!cached && streamsInstances.Any(x => x.Dependency?.ParentStream == streaminstance.Name))
            {
                collector = new RecordCollectingChannelWriter(_context.Channel);
                channel = collector;
            }

            try
            {
                long recordcount;
                if (streaminstance.Dependency != null)
                    recordcount = await ReadSubstream(streaminstance, descriptor, configuredstream, streamsInstances,
                        stateElement, recordlimit);
                else if (cached)
                    recordcount = await ReplayCachedRecords(streaminstance, parentCache!, channel, recordlimit);
                else if (configuredstream.SyncMode == SyncMode.incremental && streaminstance.SupportsIncremental)
                    recordcount = await ReadIncremental(streaminstance, configuredstream, stateElement, recordlimit,
                        channel);
                else
                    recordcount = await streaminstance.ReadRecords(_context.Logger, SyncMode.full_refresh, channel,
                        stateElement, recordlimit, configuredstream.CursorField!, null!);

                if (collector != null)
                    _context.ParentRecords[streaminstance.Name] = collector.Records;

                // Only emit the fallback terminal state on success. A failed stream may already have emitted
                // checkpoints, and emitting a stale state after a failure could move the cursor backwards.
                if (!_context.StateEmitter.HasEmitted(descriptor))
                    await _context.StateEmitter.EmitAsync(descriptor, stateElement, recordcount);

                _context.Logger.TraceStreamStatus(descriptor, AirbyteStreamStatus.COMPLETE);
                _context.Logger.Info($"Read {recordcount} records from {streaminstance.Name} stream");
            }
            catch (Exception)
            {
                _context.Logger.TraceStreamStatus(descriptor, AirbyteStreamStatus.INCOMPLETE);
                throw;
            }
        }

        private async Task<long> ReadIncremental(Stream streaminstance, ConfiguredAirbyteStream configuredstream,
            JsonElement stateElement, long? recordlimit = null, ChannelWriter<AirbyteMessage>? channel = null)
        {
            var slices = streaminstance.StreamSlices(SyncMode.incremental, configuredstream.CursorField!,
                stateElement);
            return await streaminstance.ReadRecords(_context.Logger, SyncMode.incremental,
                channel ?? _context.Channel, stateElement, recordlimit, configuredstream.CursorField, slices);
        }

        /// <summary>
        /// Reads a substream once per record of its parent. The parent key is passed as a stream slice, which the
        /// stream uses to build its request, for example <c>projects/{project_id}/line_items</c>.
        /// </summary>
        private async Task<long> ReadSubstream(Stream streaminstance, StreamDescriptor descriptor,
            ConfiguredAirbyteStream configuredstream, Stream[] streamsInstances, JsonElement stateElement,
            long? recordlimit)
        {
            var dependency = streaminstance.Dependency!;

            if (recordlimit is <= 0)
                return 0;

            var parents = await ParentRecords(streaminstance, dependency, streamsInstances);
            _context.Logger.Info(
                $"Reading substream {streaminstance.Name} for {parents.Count} {dependency.ParentStream} record(s)");

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
                    _context.Logger.Warn(
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

                recordcount += await streaminstance.ReadRecords(_context.Logger,
                    incremental ? SyncMode.incremental : SyncMode.full_refresh, _context.Channel, state, remaining,
                    configuredstream.CursorField, slice);

                if (incremental && _context.StateEmitter.TryGetLastState(descriptor, out var emitted))
                    state = emitted;
            }

            return recordcount;
        }

        /// <summary>
        /// Emits the records of a parent that was already read for one of its substreams, so a catalog that lists
        /// the substream first does not read the parent twice.
        /// </summary>
        private static async Task<long> ReplayCachedRecords(Stream streaminstance,
            IReadOnlyList<JsonElement> records, ChannelWriter<AirbyteMessage> channel, long? recordlimit)
        {
            var replay = recordlimit.HasValue
                ? records.Take((int)Math.Min(recordlimit.Value, int.MaxValue))
                : records;
            long recordcount = 0;

            foreach (var record in replay)
            {
                await channel.WriteAsync(AbstractSource.AsAirbyteMessage(streaminstance.Name,
                    streaminstance.Namespace, record));
                recordcount++;
            }

            return recordcount;
        }

        /// <summary>
        /// Returns the parent records, reading the parent once and reusing the result for every substream that
        /// depends on it. A parent that is not part of the catalog is read here without emitting records.
        /// </summary>
        private async Task<IReadOnlyList<JsonElement>> ParentRecords(Stream substream,
            SubstreamDependency dependency, Stream[] streamsInstances)
        {
            if (_context.ParentRecords.TryGetValue(dependency.ParentStream, out var cached))
                return cached;

            var parent = streamsInstances.FirstOrDefault(x => x.Name == dependency.ParentStream)
                         ?? throw new Exception(
                             $"Stream '{substream.Name}' depends on '{dependency.ParentStream}', which the source does not provide. " +
                             $"Available streams: {string.Join(", ", streamsInstances.Select(x => x.Name))}");

            if (parent.Dependency != null)
                throw new Exception(
                    $"Stream '{parent.Name}' is itself a substream of '{parent.Dependency.ParentStream}'; nested substreams are not supported");

            _context.Logger.Info($"Reading parent stream {parent.Name} for substream {substream.Name}");

            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var collecting = Task.Run(async () =>
            {
                var records = new List<JsonElement>();
                await foreach (var message in channel.Reader.ReadAllAsync())
                    if (message.Type == Type.RECORD && message.Record?.Data.ValueKind == JsonValueKind.Object)
                        records.Add(message.Record.Data);
                return (IReadOnlyList<JsonElement>)records;
            });

            var previousEmitter = parent.StateEmitter;
            parent.StateEmitter = new StateEmitter(channel.Writer);
            try
            {
                await parent.ReadRecords(_context.Logger, SyncMode.full_refresh, channel.Writer,
                    "{}".AsJsonElement(), null, null!, null!);
            }
            finally
            {
                // Completing in a finally keeps the collecting task from waiting forever when the parent read throws.
                channel.Writer.TryComplete();
                parent.StateEmitter = previousEmitter;
            }

            var records = await collecting;
            _context.ParentRecords[dependency.ParentStream] = records;
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
    }
}
