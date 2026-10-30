using System;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests.Sources
{
    public class TestSource
    {
        [Fact]
        public void Test_ReadCatalog_UsesProtocolStringEnums()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path,
                "{\"streams\":[{\"stream\":{\"name\":\"users\",\"json_schema\":{},\"supported_sync_modes\":[\"full_refresh\"]},\"sync_mode\":\"full_refresh\",\"destination_sync_mode\":\"append\"}]}");

            try
            {
                var catalog = new TestSourceImplementation().ReadCatalog(path);

                catalog.Streams[0].SyncMode.ShouldBe(SyncMode.full_refresh);
                catalog.Streams[0].DestinationSyncMode.ShouldBe(DestinationSyncMode.append);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private class TestSourceImplementation : Source
        {
            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };

            public override Task Read(AirbyteLogger logger, ChannelWriter<AirbyteMessage> channel,
                JsonElement config, ConfiguredAirbyteCatalog catalog, JsonElement state) =>
                Task.CompletedTask;

            public override AirbyteCatalog Discover(AirbyteLogger logger, JsonElement config) =>
                new() { Streams = Array.Empty<AirbyteStream>() };
        }
    }
}
