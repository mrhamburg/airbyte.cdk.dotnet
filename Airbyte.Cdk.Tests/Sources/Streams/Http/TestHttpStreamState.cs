using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Streams.Http;
using Airbyte.Cdk.Sources.Utils;
using Flurl.Http;
using Moq;
using Shouldly;
using Xunit;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests.Sources.Streams.Http
{
    public class TestHttpStreamState
    {
        [Fact]
        public async Task Test_RecordLimit_UsesLastReadRecordForTerminalState()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var stream = new FakeHttpStream(null, "{\"id\":1}", "{\"id\":2}");

            await stream.ReadRecords(new AirbyteLogger(_ => { }), SyncMode.incremental, channel.Writer,
                "{}".AsJsonElement(), recordlimit: 1);

            var messages = await ReadAll(channel);
            messages.Count(x => x.Type == MessageType.RECORD).ShouldBe(1);
            var state = messages.Single(x => x.Type == MessageType.STATE).State;
            state.Stream.StreamState.Value.GetProperty("id").GetInt32().ShouldBe(1);
        }

        [Fact]
        public async Task Test_CheckpointInterval_EmitsRecordCounts()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var stream = new FakeHttpStream(1, "{\"id\":1}", "{\"id\":2}");

            await stream.ReadRecords(new AirbyteLogger(_ => { }), SyncMode.incremental, channel.Writer,
                "{}".AsJsonElement());

            var states = (await ReadAll(channel)).Where(x => x.Type == MessageType.STATE).Select(x => x.State).ToArray();
            states.Length.ShouldBe(2);
            states[0].Stream.StreamState.Value.GetProperty("id").GetInt32().ShouldBe(1);
            states[0].SourceStats.RecordCount.ShouldBe(1);
            states[1].Stream.StreamState.Value.GetProperty("id").GetInt32().ShouldBe(2);
            states[1].SourceStats.RecordCount.ShouldBe(1);
        }

        [Fact]
        public async Task Test_SuppressedFinalState_StillReportsCount()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var stream = new FakeHttpStream(1, "{\"id\":1}", "{\"id\":1}");
            stream.UpdatedState = _ => new { id = 1 }.AsJsonElement();

            await stream.ReadRecords(new AirbyteLogger(_ => { }), SyncMode.incremental, channel.Writer,
                "{}".AsJsonElement());

            var states = (await ReadAll(channel)).Where(x => x.Type == MessageType.STATE).Select(x => x.State).ToArray();
            states.Length.ShouldBe(2);
            states[0].SourceStats.RecordCount.ShouldBe(1);
            states[1].SourceStats.RecordCount.ShouldBe(1);
        }

        [Fact]
        public async Task Test_DuplicateStateWithPendingRecords_IsEmitted()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var updateCount = 0;
            var stream = new FakeHttpStream(1, "{\"id\":1}", "{\"id\":1}", "{\"id\":2}");
            stream.UpdatedState = _ => new { id = ++updateCount <= 2 ? 1 : 2 }.AsJsonElement();

            await stream.ReadRecords(new AirbyteLogger(_ => { }), SyncMode.incremental, channel.Writer,
                "{}".AsJsonElement());

            var states = (await ReadAll(channel)).Where(x => x.Type == MessageType.STATE).Select(x => x.State).ToArray();
            states.Length.ShouldBe(3);
            states[0].SourceStats.RecordCount.ShouldBe(1);
            states[1].SourceStats.RecordCount.ShouldBe(1);
            states[2].SourceStats.RecordCount.ShouldBe(1);
        }

        private static async Task<List<AirbyteMessage>> ReadAll(Channel<AirbyteMessage> channel)
        {
            channel.Writer.Complete();
            var messages = new List<AirbyteMessage>();
            await foreach (var message in channel.Reader.ReadAllAsync())
                messages.Add(message);
            return messages;
        }

        private class FakeHttpStream : HttpStream
        {
            private readonly List<JsonElement> _records;

            public FakeHttpStream(int? checkpointInterval, params string[] records)
            {
                _givenName = "users";
                StateCheckpointInterval = checkpointInterval;
                _records = records.Select(x => x.AsJsonElement()).ToList();
            }

            public override string UrlBase() => "https://example.com";

            public override Dictionary<string, object> NextPageToken(IFlurlRequest request, IFlurlResponse response) =>
                new();

            public override IEnumerable<JsonElement> ParseResponse(IFlurlResponse response, JsonElement streamstate,
                Dictionary<string, object> streamslice = null, Dictionary<string, object> nextpagetoken = null) =>
                _records;

            public Func<JsonElement, JsonElement> UpdatedState { get; set; }

            public override JsonElement GetUpdatedState(JsonElement currentstreamstate, JsonElement latestrecord) =>
                UpdatedState?.Invoke(latestrecord) ??
                new { id = latestrecord.GetProperty("id").GetInt32() }.AsJsonElement();

            public override Task<(IFlurlRequest, IFlurlResponse)> Send(string path,
                Dictionary<string, object> headers = null, Dictionary<string, object> parameters = null,
                string json = "", string data = "") =>
                Task.FromResult<(IFlurlRequest, IFlurlResponse)>((null, new Mock<IFlurlResponse>().Object));
        }
    }
}
