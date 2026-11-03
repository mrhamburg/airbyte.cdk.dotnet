using Stream = Airbyte.Cdk.Sources.Streams.Stream;
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Utils;
using Airbyte.Cdk.Transport;
using Shouldly;
using Xunit;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests.Transport
{
    public class TestInMemoryDataChannel
    {
        [Fact]
        public async Task Test_RunAsync_PassesRecordsAndStateWithoutSerialization()
        {
            var source = new TwoRecordSource();
            var destination = new RecordingDestination();
            var catalog = Catalog("users");

            await InMemoryDataChannel.RunAsync(source, destination, new AirbyteLogger(_ => { }),
                "{}".AsJsonElement(), "{}".AsJsonElement(), catalog, "{}".AsJsonElement(),
                capacity: 8, TextWriter.Null);

            destination.Messages.Count(x => x.Type == Type.RECORD).ShouldBe(2);
            var state = destination.Messages.Single(x => x.Type == Type.STATE);
            state.State!.SourceStats!.RecordCount.ShouldBe(2);
            destination.Messages.IndexOf(state).ShouldBe(2);
        }

        [Fact]
        public async Task Test_RunAsync_DestinationFailureAbortsABlockedSource()
        {
            var source = new ManyRecordSource();
            var destination = new ThrowingDestination();

            await Should.ThrowAsync<InvalidOperationException>(() =>
                InMemoryDataChannel.RunAsync(source, destination, new AirbyteLogger(_ => { }),
                    "{}".AsJsonElement(), "{}".AsJsonElement(), Catalog("users"), "{}".AsJsonElement(),
                    capacity: 1, TextWriter.Null));
        }

        [Fact]
        public async Task Test_RunAsync_RejectsNonPositiveCapacity()
        {
            await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
                InMemoryDataChannel.RunAsync(new TwoRecordSource(), new RecordingDestination(),
                    new AirbyteLogger(_ => { }), "{}".AsJsonElement(), "{}".AsJsonElement(), Catalog("users"),
                    "{}".AsJsonElement(), capacity: 0, TextWriter.Null));
        }

        private static ConfiguredAirbyteCatalog Catalog(string name) => new()
        {
            Streams = new[]
            {
                new ConfiguredAirbyteStream
                {
                    Stream = new AirbyteStream { Name = name },
                    SyncMode = SyncMode.full_refresh,
                    DestinationSyncMode = DestinationSyncMode.append
                }
            }
        };

        private class StreamBase : Stream
        {
            public StreamBase() => _givenName = "users";

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null) =>
                throw new NotImplementedException();
        }

        private sealed class TwoRecordSource : AbstractSource
        {
            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { new TwoRecordStream() };
        }

        private sealed class TwoRecordStream : StreamBase
        {
            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                await streamchannel.WriteAsync(AbstractSource.AsAirbyteMessage(Name, null,
                    JsonDocument.Parse("{\"id\":1}").RootElement.Clone()));
                await streamchannel.WriteAsync(AbstractSource.AsAirbyteMessage(Name, null,
                    JsonDocument.Parse("{\"id\":2}").RootElement.Clone()));
                return 2;
            }
        }

        private sealed class ManyRecordSource : AbstractSource
        {
            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { new ManyRecordStream() };
        }

        private sealed class ManyRecordStream : StreamBase
        {
            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                for (var index = 0; index < 100; index++)
                    await streamchannel.WriteAsync(AbstractSource.AsAirbyteMessage(Name, null,
                        JsonDocument.Parse("{\"id\":1}").RootElement.Clone()));
                return 100;
            }
        }

        private sealed class RecordingDestination : Destination
        {
            public List<AirbyteMessage> Messages { get; } = new();

            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };

            public override Task Write(AirbyteLogger logger, JsonElement config,
                ConfiguredAirbyteCatalog catalog, AirbyteMessage msg)
            {
                Messages.Add(msg);
                return Task.CompletedTask;
            }
        }

        private sealed class ThrowingDestination : Destination
        {
            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };

            public override Task Write(AirbyteLogger logger, JsonElement config,
                ConfiguredAirbyteCatalog catalog, AirbyteMessage msg) =>
                throw new InvalidOperationException("destination failed");
        }
    }
}
