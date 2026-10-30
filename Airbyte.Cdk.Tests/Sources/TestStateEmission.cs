#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
    public class TestStateEmission
    {
        [Fact]
        public async Task Test_FullRefreshRead_EmitsTerminalStreamState()
        {
            var source = new StateEmissionSource();
            source.StreamInstance.RecordCount = 3;
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await source.Read(new AirbyteLogger(_ => { }), channel.Writer, "{}".AsJsonElement(),
                Catalog("users", SyncMode.full_refresh, "public"), "{}".AsJsonElement());
            channel.Writer.Complete();

            var messages = new List<AirbyteMessage>();
            await foreach (var message in channel.Reader.ReadAllAsync())
                messages.Add(message);

            var stateMessages = messages.Where(x => x.Type == MessageType.STATE).ToArray();
            stateMessages.Length.ShouldBe(1);
            stateMessages[0].State!.Type.ShouldBe(AirbyteStateType.STREAM);
            stateMessages[0].State!.Stream!.StreamDescriptor.Name.ShouldBe("users");
            stateMessages[0].State!.Stream!.StreamState!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
            stateMessages[0].State!.SourceStats!.RecordCount.ShouldBe(3);
        }

        [Fact]
        public async Task Test_ReadEmitsStreamStatusTraces()
        {
            var source = new StateEmissionSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await source.Read(new AirbyteLogger(message => channel.Writer.TryWrite(message)), channel.Writer,
                "{}".AsJsonElement(), Catalog("users", SyncMode.full_refresh, "public"), "{}".AsJsonElement());
            channel.Writer.Complete();

            var statuses = (await ReadAll(channel))
                .Where(x => x.Type == MessageType.TRACE && x.Trace!.Type == AirbyteTraceType.STREAM_STATUS)
                .Select(x => x.Trace!.StreamStatus!.Status)
                .ToArray();

            statuses.ShouldContain(AirbyteStreamStatus.STARTED);
            statuses.ShouldContain(AirbyteStreamStatus.COMPLETE);
        }

        [Fact]
        public async Task Test_ReadEmitsIncompleteStreamStatusOnFailure()
        {
            var source = new FailingSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await Should.ThrowAsync<InvalidOperationException>(() => source.Read(
                new AirbyteLogger(message => channel.Writer.TryWrite(message)),
                channel.Writer, "{}".AsJsonElement(), Catalog("users", SyncMode.full_refresh), "{}".AsJsonElement()));
            channel.Writer.Complete();

            var messages = await ReadAll(channel);

            messages.Count(x => x.Type == MessageType.TRACE &&
                                x.Trace!.Type == AirbyteTraceType.STREAM_STATUS &&
                                x.Trace!.StreamStatus!.Status == AirbyteStreamStatus.INCOMPLETE).ShouldBe(1);
            messages.Any(x => x.Type == MessageType.STATE).ShouldBeFalse();
        }

        [Fact]
        public async Task Test_ReadOrdersTraceRecordAndStateMessages()
        {
            var source = new OrderedSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await source.Read(new AirbyteLogger(message => channel.Writer.TryWrite(message)), channel.Writer,
                "{}".AsJsonElement(), Catalog("users", SyncMode.full_refresh), "{}".AsJsonElement());
            channel.Writer.Complete();

            var messages = await ReadAll(channel);
            var started = messages.FindIndex(x => IsStatus(x, AirbyteStreamStatus.STARTED));
            var record = messages.FindIndex(x => x.Type == MessageType.RECORD);
            var state = messages.FindIndex(x => x.Type == MessageType.STATE);
            var complete = messages.FindIndex(x => IsStatus(x, AirbyteStreamStatus.COMPLETE));

            started.ShouldBeGreaterThanOrEqualTo(0);
            record.ShouldBeGreaterThan(started);
            state.ShouldBeGreaterThan(record);
            complete.ShouldBeGreaterThan(state);
        }

        [Fact]
        public async Task Test_ReadUsesCallerProvidedLogger()
        {
            var source = new OrderedSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var log = new List<AirbyteMessage>();

            await source.Read(new AirbyteLogger(log.Add), channel.Writer, "{}".AsJsonElement(),
                Catalog("users", SyncMode.full_refresh), "{}".AsJsonElement());
            channel.Writer.Complete();

            log.Any(x => IsStatus(x, AirbyteStreamStatus.STARTED)).ShouldBeTrue();
            log.Any(x => IsStatus(x, AirbyteStreamStatus.RUNNING)).ShouldBeTrue();
            log.Any(x => IsStatus(x, AirbyteStreamStatus.COMPLETE)).ShouldBeTrue();
            log.Any(x => x.Type == MessageType.LOG && x.Log!.Message == "inside stream").ShouldBeTrue();
        }

        [Fact]
        public async Task Test_ReadEmitsRunningForCustomStream()
        {
            var source = new OrderedSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await source.Read(new AirbyteLogger(message => channel.Writer.TryWrite(message)), channel.Writer,
                "{}".AsJsonElement(), Catalog("users", SyncMode.full_refresh), "{}".AsJsonElement());
            channel.Writer.Complete();

            (await ReadAll(channel)).Any(x => IsStatus(x, AirbyteStreamStatus.RUNNING)).ShouldBeTrue();
        }

        [Fact]
        public async Task Test_CustomLegacyState_DoesNotAppendFallbackState()
        {
            var source = new LegacyStateSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await source.Read(new AirbyteLogger(_ => { }), channel.Writer, "{}".AsJsonElement(),
                Catalog("users", SyncMode.full_refresh), "{}".AsJsonElement());
            channel.Writer.Complete();

            var states = (await ReadAll(channel)).Where(x => x.Type == MessageType.STATE).ToArray();
            states.Length.ShouldBe(1);
            states[0].State!.Type.ShouldBeNull();
            states[0].State!.Data.GetProperty("legacy").GetBoolean().ShouldBeTrue();
        }

        [Fact]
        public void Test_AsAirbyteMessage_BackwardCompatibleOverload()
        {
            var message = AbstractSource.AsAirbyteMessage("users", "{\"id\":1}".AsJsonElement());

            message.Record!.Stream.ShouldBe("users");
            message.Record.Namespace.ShouldBeNull();
        }

        [Fact]
        public async Task Test_ReadMatchesEmptyCatalogNamespaceToNullStreamNamespace()
        {
            var source = new NullNamespaceSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await source.Read(new AirbyteLogger(_ => { }), channel.Writer, "{}".AsJsonElement(),
                Catalog("users", SyncMode.full_refresh, string.Empty), "{}".AsJsonElement());
            channel.Writer.Complete();

            (await ReadAll(channel)).Count(x => x.Type == MessageType.STATE).ShouldBe(1);
        }

        [Fact]
        public async Task Test_ReadPassesPerStreamStateToStream()
        {
            var source = new StateEmissionSource();
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            const string state =
                "[{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\",\"namespace\":\"public\"},\"stream_state\":{\"id\":7}}}]";

            await source.Read(new AirbyteLogger(), channel.Writer, "{}".AsJsonElement(),
                Catalog("users", SyncMode.incremental, "public"), state.AsJsonElement());
            channel.Writer.Complete();

            source.StreamInstance.ReceivedState.GetProperty("id").GetInt32().ShouldBe(7);
        }

        private static async Task<List<AirbyteMessage>> ReadAll(Channel<AirbyteMessage> channel)
        {
            var messages = new List<AirbyteMessage>();
            await foreach (var message in channel.Reader.ReadAllAsync())
                messages.Add(message);
            return messages;
        }

        private static bool IsStatus(AirbyteMessage message, AirbyteStreamStatus status) =>
            message.Type == MessageType.TRACE &&
            message.Trace?.Type == AirbyteTraceType.STREAM_STATUS &&
            message.Trace.StreamStatus?.Status == status;

        private static ConfiguredAirbyteCatalog Catalog(string name, SyncMode syncMode, string? ns = null) =>
            new()
            {
                Streams = new[]
                {
                    new ConfiguredAirbyteStream
                    {
                        Stream = new AirbyteStream { Name = name, Namespace = ns },
                        SyncMode = syncMode,
                        DestinationSyncMode = DestinationSyncMode.append
                    }
                }
            };

        private class StateEmissionSource : AbstractSource
        {
            public StateEmissionStream StreamInstance { get; } = new();

            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { StreamInstance };
        }

        private class StateEmissionStream : Stream
        {
            public JsonElement ReceivedState { get; private set; }
            public long RecordCount { get; set; }

            public StateEmissionStream() => _givenName = "users";

            public override string Namespace => "public";

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                ReceivedState = streamstate;
                return Task.FromResult(RecordCount);
            }
        }

        private class NullNamespaceSource : AbstractSource
        {
            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { new NullNamespaceStream() };
        }

        private class NullNamespaceStream : Stream
        {
            public NullNamespaceStream() => _givenName = "users";

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null) =>
                Task.FromResult(0L);
        }

        private class OrderedSource : AbstractSource
        {
            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { new OrderedStream() };
        }

        private class OrderedStream : Stream
        {
            public OrderedStream() => _givenName = "users";

            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                await streamchannel.WriteAsync(
                    AbstractSource.AsAirbyteMessage(Name, Namespace, "{\"id\":1}".AsJsonElement()));
                logger.Info("inside stream");
                return 1;
            }
        }

        private class LegacyStateSource : AbstractSource
        {
            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { new LegacyStateStream() };
        }

        private class LegacyStateStream : Stream
        {
            public LegacyStateStream() => _givenName = "users";

            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                await streamchannel.WriteAsync(new AirbyteMessage
                {
                    Type = MessageType.STATE,
                    State = new AirbyteStateMessage { Data = "{\"legacy\":true}".AsJsonElement() }
                });
                return 1;
            }
        }

        private class FailingSource : AbstractSource
        {
            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { new FailingStream() };
        }

        private class FailingStream : Stream
        {
            public FailingStream() => _givenName = "users";

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null) =>
                throw new InvalidOperationException("boom");
        }
    }
}
