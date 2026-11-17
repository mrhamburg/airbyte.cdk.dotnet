#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Streams.Http;
using Airbyte.Cdk.Sources.Streams.Http.Auth;
using Airbyte.Cdk.Sources.Utils;
using Airbyte.Cdk.Transport;
using Flurl.Http;
using Json.Schema;
using Moq;
using Shouldly;
using Xunit;
using Stream = Airbyte.Cdk.Sources.Streams.Stream;

namespace Airbyte.Cdk.Tests
{
    public class RegressionFixesTests
    {
        [Fact]
        public void Test_Builder_SourceDefinedCursor_IsAppliedToDiscover()
        {
            var stream = new FluentStreamBuilder()
                .GetJsonSchema(() => JsonSchema.FromText("{}"))
                .CursorField(new[] { "id" })
                .SourceDefinedCursor(false)
                .Create("users");

            stream.AsAirbyteStream().SourceDefinedCursor.ShouldBe(false);
        }

        [Fact]
        public void Test_Builder_TimeOut_IsApplied()
        {
            var stream = new FluentStreamBuilder()
                .TimeOut(TimeSpan.FromSeconds(3))
                .Create("users");

            stream.TimeOut.ShouldBe(TimeSpan.FromSeconds(3));
        }

        [Fact]
        public async Task Test_Builder_ReadRecords_BindsDelegate()
        {
            var stream = new FluentStreamBuilder()
                .ReadRecords((_, _, _, _, _, _, _) => Task.FromResult(42L))
                .Create("users");

            var count = await stream.ReadRecords(new AirbyteLogger(_ => { }), SyncMode.full_refresh,
                Channel.CreateUnbounded<AirbyteMessage>().Writer, "{}".AsJsonElement());

            count.ShouldBe(42L);
        }

        [Fact]
        public void Test_ShouldRetry_RetriesRateLimitsTransientAndServerErrorsOnly()
        {
            HttpStream.IsRetryableStatus(404).ShouldBeFalse();
            HttpStream.IsRetryableStatus(429).ShouldBeTrue();
            HttpStream.IsRetryableStatus(503).ShouldBeTrue();
            HttpStream.IsRetryableStatus(null).ShouldBeTrue();
        }

        [Fact]
        public void Test_BackoffTime_WithNoResponse_FallsBackToExponentialDelay()
        {
            var stream = new TestHttpStream();

            stream.BackoffTime(2, null!).ShouldBe(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public void Test_RequestBody_IsJsonMediaType()
        {
            using var content = HttpStream.CreateBodyContent("{\"id\":1}", true);

            content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        }

        [Fact]
        public void Test_OAuth_ExpiryFromResponse_UsesSeconds()
        {
            var json = JsonDocument.Parse("{\"expires_in\":3600}").RootElement.Clone();
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            OAuth.ExpiryFromResponse(json, "expires_in", now).ShouldBe(now.AddSeconds(3600));
        }

        [Fact]
        public void Test_JsonElement_FromObject_UsesProtocolEnumNames()
        {
            var element = new { mode = SyncMode.incremental }.AsJsonElement();

            element.GetProperty("mode").GetString().ShouldBe("incremental");
        }

        [Fact]
        public void Test_ToType_InvalidPayload_Throws()
        {
            var element = JsonDocument.Parse("\"not-an-object\"").RootElement.Clone();

            Should.Throw<JsonException>(() => element.ToType<Dictionary<string, string>>());
        }

        [Fact]
        public void Test_CheckConnection_WithoutException_ReturnsFailedStatus()
        {
            var source = new NullExceptionSource();

            var result = source.Check(new AirbyteLogger(_ => { }), "{}".AsJsonElement());

            result.Status.ShouldBe(Status.FAILED);
            result.Message.ShouldBe("The connection check failed but did not provide an error.");
        }

        [Fact]
        public void Test_DefaultGetUpdatedState_Throws()
        {
            var stream = new DefaultStateStream();

            Should.Throw<NotSupportedException>(() =>
                stream.GetUpdatedState("{}".AsJsonElement(), "{}".AsJsonElement()));
        }

        [Fact]
        public async Task Test_FullRefreshRead_WithoutStateOverride_DoesNotThrow()
        {
            var stream = new TestHttpStream();

            await stream.ReadRecords(new AirbyteLogger(_ => { }), SyncMode.full_refresh,
                Channel.CreateUnbounded<AirbyteMessage>().Writer, "{}".AsJsonElement());
        }

        [Fact]
        public async Task Test_IncrementalRead_WithNoRecords_DoesNotUpdateState()
        {
            var stream = new StateCountingHttpStream(10, 0);

            await stream.ReadRecords(new AirbyteLogger(_ => { }), SyncMode.incremental,
                Channel.CreateUnbounded<AirbyteMessage>().Writer, "{}".AsJsonElement());

            stream.StateUpdates.ShouldBe(0);
        }

        [Fact]
        public async Task Test_CheckpointInterval_Zero_Throws()
        {
            var stream = new StateCountingHttpStream(0, 1);

            await Should.ThrowAsync<InvalidOperationException>(() => stream.ReadRecords(
                new AirbyteLogger(_ => { }), SyncMode.incremental,
                Channel.CreateUnbounded<AirbyteMessage>().Writer, "{}".AsJsonElement()));
        }

        [Fact]
        public async Task Test_IncrementalRead_PassesConfiguredCursorField()
        {
            var stream = new CursorCaptureStream();
            var source = new SingleStreamSource(stream);

            await source.Read(new AirbyteLogger(_ => { }), Channel.CreateUnbounded<AirbyteMessage>().Writer,
                "{}".AsJsonElement(),
                Catalog("users", SyncMode.incremental, new[] { "id" }), "{}".AsJsonElement());

            stream.ReceivedCursor.ShouldBe(new[] { "id" });
        }

        [Fact]
        public async Task Test_ParentRead_RestoresParentStateEmitter()
        {
            var parent = new ParentRecordingStream();
            var child = new ChildStream();
            var source = new SubstreamSource(child, parent);

            await source.Read(new AirbyteLogger(_ => { }), Channel.CreateUnbounded<AirbyteMessage>().Writer,
                "{}".AsJsonElement(), Catalog("child", SyncMode.full_refresh), "{}".AsJsonElement());

            parent.StateEmitter.ShouldBeNull();
        }

        [Fact]
        public void Test_CatalogCoercion_DoesNotMutateInput()
        {
            var catalog = new AirbyteCatalog
            {
                Streams = new[]
                {
                    new AirbyteStream
                    {
                        Name = "users",
                        SupportedSyncModes = new[] { SyncMode.incremental },
                        SourceDefinedCursor = true
                    }
                }
            };

            var coerced = CatalogHelper.CoerceAirbyteCatalogAsFullRefresh(catalog);

            catalog.Streams[0].SupportedSyncModes.ShouldBe(new[] { SyncMode.incremental });
            catalog.Streams[0].SourceDefinedCursor.ShouldBe(true);
            coerced.Streams[0].SupportedSyncModes.ShouldBe(new[] { SyncMode.full_refresh });
            coerced.Streams[0].SourceDefinedCursor.ShouldBe(false);
        }

        [Fact]
        public async Task Test_SocketWriter_PropagatesBindFailure()
        {
            var path = Path.Combine(Path.GetTempPath(), "airbyte-socket-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);

            var writer = new SocketDataChannelWriter(path);
            writer.Start();

            try
            {
                await Should.ThrowAsync<Exception>(() => writer.CompleteAsync());
            }
            finally
            {
                try
                {
                    await writer.DisposeAsync();
                }
                catch
                {
                    // The bind failure is the behavior under test.
                }

                Directory.Delete(path, true);
            }
        }

        private static ConfiguredAirbyteCatalog Catalog(string name, SyncMode syncMode, string[]? cursor = null) => new()
        {
            Streams = new[]
            {
                new ConfiguredAirbyteStream
                {
                    Stream = new AirbyteStream { Name = name },
                    SyncMode = syncMode,
                    DestinationSyncMode = DestinationSyncMode.append,
                    CursorField = cursor
                }
            }
        };

        private class TestHttpStream : HttpStream
        {
            public override string UrlBase() => "https://example.invalid";

            public override Task<(IFlurlRequest, IFlurlResponse)> Send(string path,
                Dictionary<string, object>? headers = null, Dictionary<string, object>? parameters = null,
                string json = "", string data = "") =>
                Task.FromResult((new Mock<IFlurlRequest>().Object, new Mock<IFlurlResponse>().Object));

            public override Dictionary<string, object> NextPageToken(IFlurlRequest request, IFlurlResponse response) =>
                new();

            public override IEnumerable<JsonElement> ParseResponse(IFlurlResponse response, JsonElement streamstate,
                Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
                Array.Empty<JsonElement>();
        }

        private class StateCountingHttpStream : TestHttpStream
        {
            public StateCountingHttpStream(int interval, int records)
            {
                StateCheckpointInterval = interval;
                Records = records;
            }

            public int StateUpdates { get; private set; }
            public int Records { get; }

            public override JsonElement GetUpdatedState(JsonElement currentstreamstate, JsonElement latestrecord)
            {
                StateUpdates++;
                return currentstreamstate;
            }

            public override IEnumerable<JsonElement> ParseResponse(IFlurlResponse response, JsonElement streamstate,
                Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
                Enumerable.Range(0, Records).Select(x => $"{{\"id\":{x}}}".AsJsonElement()).ToArray();
        }

        private class DefaultStateStream : Stream
        {
            public DefaultStateStream() => _givenName = "users";

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null) =>
                Task.FromResult(0L);
        }

        private class CursorCaptureStream : Stream
        {
            public CursorCaptureStream() => _givenName = "users";

            public string[]? ReceivedCursor { get; private set; }

            public override string[] CursorField => new[] { "id" };

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                ReceivedCursor = cursorfield;
                return Task.FromResult(0L);
            }
        }

        private class ParentRecordingStream : Stream
        {
            public ParentRecordingStream() => _givenName = "parent";

            public StateEmitter? CapturedEmitter { get; private set; }

            public override async Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                CapturedEmitter = StateEmitter;
                await streamchannel.WriteAsync(
                    AbstractSource.AsAirbyteMessage(Name, "{\"id\":1}".AsJsonElement()));
                return 1;
            }
        }

        private class ChildStream : Stream
        {
            public ChildStream() => _givenName = "child";

            public override SubstreamDependency Dependency => new("parent", "id", "parent_id");

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null) =>
                Task.FromResult(0L);
        }

        private class NullExceptionSource : AbstractSource
        {
            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return false;
            }

            public override Stream[] Streams(JsonElement config) => Array.Empty<Stream>();
        }

        private class SingleStreamSource : AbstractSource
        {
            private readonly Stream _stream;

            public SingleStreamSource(Stream stream) => _stream = stream;

            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => new[] { _stream };
        }

        private class SubstreamSource : AbstractSource
        {
            private readonly Stream[] _streams;

            public SubstreamSource(params Stream[] streams) => _streams = streams;

            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => _streams;
        }
    }
}
