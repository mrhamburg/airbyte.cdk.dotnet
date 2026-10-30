using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests.Sources.Utils
{
    public class TestStateHandling
    {
        [Fact]
        public void Test_StateReader_ParsesLegacyStreamMap()
        {
            var state = "{\"exchange_rates\":{\"date\":\"2021-05-18\"}}".AsJsonElement();

            var messages = StateReader.Parse(state);

            messages.Count.ShouldBe(1);
            messages[0].Type.ShouldBe(AirbyteStateType.STREAM);
            messages[0].Stream.StreamDescriptor.Name.ShouldBe("exchange_rates");
            messages[0].Stream.StreamState.Value.GetProperty("date").GetString().ShouldBe("2021-05-18");
        }

        [Fact]
        public void Test_StateReader_ParsesStateMessageList()
        {
            const string json =
                "[{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\",\"namespace\":\"public\"},\"stream_state\":{\"id\":1}}}]";

            var messages = StateReader.Parse(json.AsJsonElement());

            messages.Count.ShouldBe(1);
            messages[0].Type.ShouldBe(AirbyteStateType.STREAM);
            messages[0].Stream.StreamDescriptor.Name.ShouldBe("users");
            messages[0].Stream.StreamDescriptor.Namespace.ShouldBe("public");
        }

        [Fact]
        public void Test_StateReader_ParsesGlobalState()
        {
            const string json =
                "{\"type\":\"GLOBAL\",\"global\":{\"shared_state\":{\"cursor\":\"a\"},\"stream_states\":[{\"stream_descriptor\":{\"name\":\"users\"},\"stream_state\":{\"id\":1}}]}}";

            var messages = StateReader.Parse(json.AsJsonElement());

            messages.Count.ShouldBe(1);
            messages[0].Type.ShouldBe(AirbyteStateType.GLOBAL);
            messages[0].Global.StreamStates[0].StreamDescriptor.Name.ShouldBe("users");
        }

        [Fact]
        public void Test_StateReader_ParsesEnvelope()
        {
            const string json =
                "{\"type\":\"STATE\",\"state\":{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\"},\"stream_state\":{}}}}";

            var messages = StateReader.Parse(json.AsJsonElement());

            messages.Count.ShouldBe(1);
            messages[0].Stream.StreamDescriptor.Name.ShouldBe("users");
        }

        [Fact]
        public void Test_StateReader_TreatsEmptyObjectAsNoState()
        {
            StateReader.Parse("{}".AsJsonElement()).ShouldBeEmpty();
        }

        [Theory]
        [InlineData("data")]
        [InlineData("stream")]
        [InlineData("global")]
        public void Test_StateReader_TreatsReservedNamesAsLegacyStreams(string name)
        {
            var messages = StateReader.Parse(("{\"" + name + "\":{\"id\":1}}").AsJsonElement());

            messages.Count.ShouldBe(1);
            messages[0].Stream.StreamDescriptor.Name.ShouldBe(name);
        }

        [Fact]
        public void Test_StateReader_IgnoresMalformedEnvelope()
        {
            StateReader.Parse("{\"type\":\"STATE\"}".AsJsonElement()).ShouldBeEmpty();
        }

        [Fact]
        public void Test_StateManager_MapsStreamAndNamespace()
        {
            const string json =
                "[{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\",\"namespace\":\"public\"},\"stream_state\":{\"id\":1}}}]";
            var manager = new ConnectorStateManager(StateReader.Parse(json.AsJsonElement()));

            manager.GetStreamState("users", "public").GetProperty("id").GetInt32().ShouldBe(1);
            manager.GetStreamState("other", null).ValueKind.ShouldBe(JsonValueKind.Object);
            manager.GetStreamState("other", null).EnumerateObject().Any().ShouldBeFalse();
        }

        [Fact]
        public void Test_StateManager_FallsBackToNamespacedStateForUnnamespacedStream()
        {
            const string json =
                "[{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\",\"namespace\":\"public\"},\"stream_state\":{\"id\":1}}}]";
            var manager = new ConnectorStateManager(StateReader.Parse(json.AsJsonElement()));

            manager.GetStreamState("users", null).GetProperty("id").GetInt32().ShouldBe(1);
        }

        [Fact]
        public void Test_StateManager_DoesNotCrossWireAmbiguousNamespaces()
        {
            const string json =
                "[{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\",\"namespace\":\"a\"},\"stream_state\":{\"id\":1}}},{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\",\"namespace\":\"b\"},\"stream_state\":{\"id\":2}}}]";
            var manager = new ConnectorStateManager(StateReader.Parse(json.AsJsonElement()));

            manager.GetStreamState("users", null).EnumerateObject().Any().ShouldBeFalse();
        }

        [Fact]
        public void Test_StateManager_RejectsGlobalSharedState()
        {
            var messages = StateReader.Parse(
                "{\"type\":\"GLOBAL\",\"global\":{\"shared_state\":{\"cursor\":\"a\"},\"stream_states\":[]}}"
                    .AsJsonElement());

            Should.Throw<NotSupportedException>(() => new ConnectorStateManager(messages));
        }

        [Fact]
        public void Test_StateManager_FallsBackToNameOnlyState()
        {
            var manager = new ConnectorStateManager(StateReader.Parse(
                "{\"users\":{\"id\":1}}".AsJsonElement()));

            manager.GetStreamState("users", "public").GetProperty("id").GetInt32().ShouldBe(1);
        }

        [Fact]
        public void Test_StateManager_CreatesPerStreamStateMessage()
        {
            var manager = new ConnectorStateManager(Array.Empty<AirbyteStateMessage>());

            var message = manager.CreateStateMessage(
                new StreamDescriptor { Name = "users", Namespace = "public" },
                "{\"id\":1}".AsJsonElement(), 5);

            message.Type.ShouldBe(AirbyteStateType.STREAM);
            message.Stream.StreamDescriptor.Name.ShouldBe("users");
            message.Stream.StreamDescriptor.Namespace.ShouldBe("public");
            message.Stream.StreamState.Value.GetProperty("id").GetInt32().ShouldBe(1);
            message.SourceStats.RecordCount.ShouldBe(5);
        }

        [Fact]
        public void Test_StateManager_CreatesResetState()
        {
            var manager = new ConnectorStateManager(Array.Empty<AirbyteStateMessage>());

            var message = manager.CreateStateMessage(new StreamDescriptor { Name = "users" }, null, 0);

            message.Stream.StreamState.ShouldBeNull();
        }

        [Fact]
        public async Task Test_StateEmitter_DeduplicatesSameState()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var emitter = new StateEmitter(channel.Writer);
            var descriptor = new StreamDescriptor { Name = "users" };

            await emitter.EmitAsync(descriptor, "{\"id\":1}".AsJsonElement(), 1);
            await emitter.EmitAsync(descriptor, "{\"id\":1}".AsJsonElement(), 1);

            channel.Reader.Count.ShouldBe(1);
        }

        [Fact]
        public async Task Test_StateEmitter_EmitsChangedState()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var emitter = new StateEmitter(channel.Writer);
            var descriptor = new StreamDescriptor { Name = "users" };

            await emitter.EmitAsync(descriptor, "{\"id\":1}".AsJsonElement(), 1);
            await emitter.EmitAsync(descriptor, "{\"id\":2}".AsJsonElement(), 1);

            channel.Reader.Count.ShouldBe(2);
        }

        [Fact]
        public async Task Test_StateEmitter_EmitsNullStateForReset()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var emitter = new StateEmitter(channel.Writer);

            await emitter.EmitAsync(new StreamDescriptor { Name = "users" }, null, 0);

            channel.Reader.TryRead(out var message).ShouldBeTrue();
            message.Type.ShouldBe(MessageType.STATE);
            message.State.Type.ShouldBe(AirbyteStateType.STREAM);
            message.State.Stream.StreamState.ShouldBeNull();
        }

        [Fact]
        public void Test_ResetState_SerializesExplicitNull()
        {
            var message = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState
                {
                    StreamDescriptor = new StreamDescriptor { Name = "users" },
                    StreamState = null
                }
            };

            var json = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options);
            using var document = JsonDocument.Parse(json);
            var streamState = document.RootElement.GetProperty("stream").GetProperty("stream_state");

            streamState.ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }
}
