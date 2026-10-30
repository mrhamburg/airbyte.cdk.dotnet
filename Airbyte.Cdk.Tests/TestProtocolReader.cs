using System;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;
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
    }
}
