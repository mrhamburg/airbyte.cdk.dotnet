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
            var channel = Channel.CreateUnbounded<AirbyteMessage>();

            await source.Read(new AirbyteLogger(), channel.Writer, "{}".AsJsonElement(),
                Catalog("users", SyncMode.full_refresh, "public"), "{}".AsJsonElement());
            channel.Writer.Complete();

            var messages = new List<AirbyteMessage>();
            await foreach (var message in channel.Reader.ReadAllAsync())
                messages.Add(message);

            var stateMessages = messages.Where(x => x.Type == MessageType.STATE).ToArray();
            stateMessages.Length.ShouldBe(1);
            stateMessages[0].State.Type.ShouldBe(AirbyteStateType.STREAM);
            stateMessages[0].State.Stream.StreamDescriptor.Name.ShouldBe("users");
            stateMessages[0].State.Stream.StreamState.Value.ValueKind.ShouldBe(JsonValueKind.Object);
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
                exc = null;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new Stream[] { StreamInstance };
        }

        private class StateEmissionStream : Stream
        {
            public JsonElement ReceivedState { get; private set; }

            public StateEmissionStream() => _givenName = "users";

            public override string Namespace => "public";

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[] cursorfield = null, Dictionary<string, object> streamslice = null)
            {
                ReceivedState = streamstate;
                return Task.FromResult(0L);
            }
        }
    }
}
