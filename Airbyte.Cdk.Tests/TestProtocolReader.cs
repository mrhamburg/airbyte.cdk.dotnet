using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests
{
    public class TestProtocolReader
    {
        [Fact]
        public void Test_TryParseMessage_DeserializesStringEnums()
        {
            const string json =
                "{\"type\":\"RECORD\",\"record\":{\"stream\":\"users\",\"data\":{\"id\":1},\"emitted_at\":1}}";

            var parsed = AirbyteProtocolReader.TryParseMessage(json, out var message);

            parsed.ShouldBeTrue();
            message.Type.ShouldBe(MessageType.RECORD);
            message.Record.Stream.ShouldBe("users");
            message.Record.Data.GetProperty("id").GetInt32().ShouldBe(1);
        }

        [Fact]
        public void Test_TryParseMessage_RejectsInvalidJson()
        {
            AirbyteProtocolReader.TryParseMessage("not-json", out _).ShouldBeFalse();
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("{\"foo\":\"bar\"}")]
        [InlineData("{\"type\":\"RECORD\"}")]
        [InlineData("{\"type\":\"UNKNOWN\"}")]
        public void Test_TryParseMessage_RejectsStructurallyInvalidMessages(string json)
        {
            AirbyteProtocolReader.TryParseMessage(json, out _).ShouldBeFalse();
        }

        [Fact]
        public void Test_TryParseMessage_RejectsEmptyInput()
        {
            AirbyteProtocolReader.TryParseMessage(null, out _).ShouldBeFalse();
            AirbyteProtocolReader.TryParseMessage(string.Empty, out _).ShouldBeFalse();
        }

        [Fact]
        public void Test_ReadCatalog_ReadsFileAndStringEnums()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path,
                "{\"streams\":[{\"stream\":{\"name\":\"users\",\"json_schema\":{},\"supported_sync_modes\":[\"full_refresh\"]},\"sync_mode\":\"full_refresh\",\"destination_sync_mode\":\"overwrite\"}]}");

            try
            {
                var catalog = AirbyteProtocolReader.ReadCatalog(path);

                catalog.Streams.Length.ShouldBe(1);
                catalog.Streams[0].Stream.Name.ShouldBe("users");
                catalog.Streams[0].SyncMode.ShouldBe(SyncMode.full_refresh);
                catalog.Streams[0].DestinationSyncMode.ShouldBe(DestinationSyncMode.overwrite);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Test_ToConsole_WritesOneJsonLine()
        {
            var writer = new StringWriter();

            AirbyteEntrypoint.ToConsole(new AirbyteMessage
            {
                Type = MessageType.SPEC,
                Spec = new ConnectorSpecification
                {
                    ConnectionSpecification = JsonDocument.Parse("{}")
                }
            }, writer);

            var output = writer.ToString().TrimEnd('\r', '\n');
            output.ShouldNotContain("\n");
            using var document = JsonDocument.Parse(output);
            document.RootElement.GetProperty("type").GetString().ShouldBe("SPEC");
        }

        [Fact]
        public async Task Test_RunWriteLoop_ReturnsWhenDestinationFails()
        {
            var input = new StringReader(
                "{\"type\":\"RECORD\",\"record\":{\"stream\":\"users\",\"data\":{\"id\":1},\"emitted_at\":1}}\n");
            var output = new StringWriter();
            var logger = new AirbyteLogger(_ => { });
            var destination = new ThrowingDestination();

            await Should.ThrowAsync<InvalidOperationException>(() => AirbyteEntrypoint.RunWriteLoop(
                input, output, logger, destination, "{}".AsJsonElement(), EmptyCatalog()));
        }

        [Fact]
        public async Task Test_RunWriteLoop_EchoesStateInOrder()
        {
            var input = new StringReader(
                "{\"type\":\"STATE\",\"state\":{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\"},\"stream_state\":{\"id\":1}}}}\n");
            var output = new StringWriter();
            var logger = new AirbyteLogger(_ => { });
            var destination = new RecordingDestination();

            await AirbyteEntrypoint.RunWriteLoop(input, output, logger, destination, "{}".AsJsonElement(),
                EmptyCatalog());

            destination.Messages.Count.ShouldBe(1);
            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(1);
            using var document = JsonDocument.Parse(lines[0]);
            document.RootElement.GetProperty("type").GetString().ShouldBe("STATE");
            document.RootElement.GetProperty("state").GetProperty("stream").GetProperty("stream_descriptor")
                .GetProperty("name").GetString().ShouldBe("users");
        }

        private static ConfiguredAirbyteCatalog EmptyCatalog() => new()
        {
            Streams = Array.Empty<ConfiguredAirbyteStream>()
        };

        private class ThrowingDestination : Destination
        {
            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };

            public override Task Write(AirbyteLogger logger, JsonElement config, ConfiguredAirbyteCatalog catalog,
                AirbyteMessage msg) => throw new InvalidOperationException("destination failed");
        }

        private class RecordingDestination : Destination
        {
            public List<AirbyteMessage> Messages { get; } = new();

            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };

            public override Task Write(AirbyteLogger logger, JsonElement config, ConfiguredAirbyteCatalog catalog,
                AirbyteMessage msg)
            {
                Messages.Add(msg);
                return Task.CompletedTask;
            }
        }
    }
}
