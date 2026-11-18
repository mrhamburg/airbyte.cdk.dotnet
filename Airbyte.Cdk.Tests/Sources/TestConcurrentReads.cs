#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests.Sources
{
    public class TestConcurrentReads
    {
        [Fact]
        public async Task Test_DefaultRead_RemainsSequential()
        {
            var probe = new ConcurrencyProbe();
            var source = new ConcurrentSource(1,
                new ProbingStream("first", probe, TimeSpan.FromMilliseconds(50)),
                new ProbingStream("second", probe, TimeSpan.FromMilliseconds(50)));

            await Read(source, "{}", Catalog("first"), Catalog("second"));

            probe.MaxActive.ShouldBe(1);
        }

        [Fact]
        public async Task Test_ParallelRead_RunsIndependentStreamsAtTheSameTime()
        {
            var rendezvous = new Rendezvous(2);
            var probe = new ConcurrencyProbe();
            var source = new ConcurrentSource(2,
                new ProbingStream("first", probe, rendezvous),
                new ProbingStream("second", probe, rendezvous));

            await Read(source, "{}", Catalog("first"), Catalog("second"));

            probe.MaxActive.ShouldBe(2);
        }

        [Fact]
        public async Task Test_ParallelRead_CanBeEnabledByInternalConfig()
        {
            var rendezvous = new Rendezvous(2);
            var probe = new ConcurrencyProbe();
            var source = new ConcurrentSource(1,
                new ProbingStream("first", probe, rendezvous),
                new ProbingStream("second", probe, rendezvous));

            await Read(source, "{\"_concurrency\":2}", Catalog("first"), Catalog("second"));

            probe.MaxActive.ShouldBe(2);
        }

        [Fact]
        public async Task Test_ParallelRead_RespectsMaxConcurrency()
        {
            var rendezvous = new Rendezvous(2);
            var probe = new ConcurrencyProbe();
            var source = new ConcurrentSource(2,
                new ProbingStream("first", probe, rendezvous),
                new ProbingStream("second", probe, rendezvous),
                new ProbingStream("third", probe, rendezvous),
                new ProbingStream("fourth", probe, rendezvous));

            await Read(source, "{}", Catalog("first"), Catalog("second"), Catalog("third"), Catalog("fourth"));

            probe.MaxActive.ShouldBe(2);
        }

        [Fact]
        public async Task Test_ParallelRead_KeepsParentBeforeSubstreamAndReadsItOnce()
        {
            var parent = new RecordingStream("projects", Json("{\"id\":\"p1\"}"), Json("{\"id\":\"p2\"}"));
            var child = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var source = new ConcurrentSource(2, parent, child);

            var messages = await Read(source, "{}", Catalog("projects"), Catalog("project_line_items"));

            parent.Reads.ShouldBe(1);
            child.Slices.ShouldBe(new[] { "p1", "p2" });
            messages.Count(x => x.Type == MessageType.RECORD && x.Record!.Stream == "projects").ShouldBe(2);
            messages.Count(x => x.Type == MessageType.RECORD && x.Record!.Stream == "project_line_items").ShouldBe(2);
        }

        [Fact]
        public async Task Test_ParallelRead_KeepsStateAndTraceAttributionPerStream()
        {
            var rendezvous = new Rendezvous(2);
            var source = new ConcurrentSource(2,
                new ProbingStream("first", new ConcurrencyProbe(), rendezvous),
                new ProbingStream("second", new ConcurrencyProbe(), rendezvous));

            var messages = await Read(source, "{}", Catalog("first"), Catalog("second"));

            foreach (var stream in new[] { "first", "second" })
            {
                messages.Count(x => IsStatus(x, stream, AirbyteStreamStatus.STARTED)).ShouldBe(1);
                messages.Count(x => IsStatus(x, stream, AirbyteStreamStatus.RUNNING)).ShouldBe(1);
                messages.Count(x => IsStatus(x, stream, AirbyteStreamStatus.COMPLETE)).ShouldBe(1);
                messages.Count(x => x.Type == MessageType.STATE &&
                                    x.State!.Stream!.StreamDescriptor!.Name == stream).ShouldBe(1);
            }
        }

        [Fact]
        public async Task Test_ParallelRead_PropagatesStreamFailure()
        {
            var source = new ConcurrentSource(2,
                new ThrowingStream("broken"),
                new ProbingStream("healthy", new ConcurrencyProbe(), new Rendezvous(1)));

            var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
                Read(source, "{}", Catalog("broken"), Catalog("healthy")));

            exception.Message.ShouldBe("stream failed");
        }

        private static async Task<List<AirbyteMessage>> Read(AbstractSource source, string config,
            params ConfiguredAirbyteCatalog[] catalogs)
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var merged = new ConfiguredAirbyteCatalog { Streams = catalogs.SelectMany(x => x.Streams).ToArray() };

            await source.Read(new AirbyteLogger(message => channel.Writer.TryWrite(message)), channel.Writer,
                config.AsJsonElement(), merged, "{}".AsJsonElement());
            channel.Writer.Complete();

            var messages = new List<AirbyteMessage>();
            await foreach (var message in channel.Reader.ReadAllAsync())
                messages.Add(message);

            return messages;
        }

        private static bool IsStatus(AirbyteMessage message, string stream, AirbyteStreamStatus status) =>
            message.Type == MessageType.TRACE &&
            message.Trace?.Type == AirbyteTraceType.STREAM_STATUS &&
            message.Trace.StreamStatus?.Status == status &&
            message.Trace.StreamStatus.StreamDescriptor?.Name == stream;

        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static ConfiguredAirbyteCatalog Catalog(string name, SyncMode syncMode = SyncMode.full_refresh) =>
            new()
            {
                Streams = new[]
                {
                    new ConfiguredAirbyteStream
                    {
                        Stream = new AirbyteStream { Name = name },
                        SyncMode = syncMode,
                        DestinationSyncMode = DestinationSyncMode.append
                    }
                }
            };

        private sealed class ConcurrentSource : AbstractSource
        {
            private readonly Stream[] _streams;
            private readonly int _maxConcurrency;

            public ConcurrentSource(int maxConcurrency, params Stream[] streams)
            {
                _maxConcurrency = maxConcurrency;
                _streams = streams;
            }

            public override int MaxConcurrency => _maxConcurrency;

            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => _streams;
        }

        private sealed class ConcurrencyProbe
        {
            private int _active;
            private int _maxActive;

            public int MaxActive => Volatile.Read(ref _maxActive);

            public void Enter()
            {
                var active = Interlocked.Increment(ref _active);
                var observed = Volatile.Read(ref _maxActive);
                while (active > observed &&
                       Interlocked.CompareExchange(ref _maxActive, active, observed) != observed)
                    observed = Volatile.Read(ref _maxActive);
            }

            public void Exit() => Interlocked.Decrement(ref _active);
        }

        /// <summary>
        /// Holds every participant until the expected number have arrived. A sequential read cannot satisfy
        /// the rendezvous, so the timeout makes that failure explicit instead of hanging the test run.
        /// </summary>
        private sealed class Rendezvous
        {
            private readonly int _participants;
            private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _arrived;

            public Rendezvous(int participants) => _participants = participants;

            public async Task Arrive()
            {
                if (Interlocked.Increment(ref _arrived) == _participants)
                    _released.TrySetResult();

                var completed = await Task.WhenAny(_released.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed != _released.Task)
                    throw new TimeoutException("The streams did not run at the same time.");
            }
        }

        private sealed class ProbingStream : Stream
        {
            private readonly ConcurrencyProbe _probe;
            private readonly TimeSpan? _delay;
            private readonly Rendezvous? _rendezvous;

            public ProbingStream(string name, ConcurrencyProbe probe, TimeSpan delay)
            {
                _givenName = name;
                _probe = probe;
                _delay = delay;
            }

            public ProbingStream(string name, ConcurrencyProbe probe, Rendezvous rendezvous)
            {
                _givenName = name;
                _probe = probe;
                _rendezvous = rendezvous;
            }

            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                _probe.Enter();
                try
                {
                    if (_rendezvous != null)
                        await _rendezvous.Arrive();
                    if (_delay.HasValue)
                        await Task.Delay(_delay.Value);

                    await streamchannel.WriteAsync(AbstractSource.AsAirbyteMessage(Name,
                        JsonSerializer.SerializeToElement(new Dictionary<string, int> { { "id", 1 } })));
                    return 1;
                }
                finally
                {
                    _probe.Exit();
                }
            }
        }

        private sealed class ThrowingStream : Stream
        {
            public ThrowingStream(string name) => _givenName = name;

            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                await Task.Yield();
                throw new InvalidOperationException("stream failed");
            }
        }

        private sealed class RecordingStream : Stream
        {
            private readonly JsonElement[] _records;

            public RecordingStream(string name, params JsonElement[] records)
            {
                _givenName = name;
                _records = records;
            }

            public int Reads { get; private set; }

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                Reads++;
                foreach (var record in _records)
                    streamchannel.TryWrite(AbstractSource.AsAirbyteMessage(Name, record));

                return Task.FromResult((long)_records.Length);
            }
        }

        private sealed class RecordingSubstream : Stream
        {
            private readonly SubstreamDependency _dependency;

            public RecordingSubstream(string name, SubstreamDependency dependency)
            {
                _givenName = name;
                _dependency = dependency;
            }

            public override SubstreamDependency? Dependency => _dependency;

            public List<string> Slices { get; } = new();

            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                var key = streamslice![_dependency.SliceParameter].ToString()!;
                Slices.Add(key);
                await streamchannel.WriteAsync(AbstractSource.AsAirbyteMessage(Name,
                    JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        { _dependency.SliceParameter, key }
                    })));
                return 1;
            }
        }
    }
}
