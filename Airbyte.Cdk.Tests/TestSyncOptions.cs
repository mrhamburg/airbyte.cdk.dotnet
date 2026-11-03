using CommandLine;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.Tests
{
    public class TestSyncOptions
    {
        [Fact]
        public void Test_Parse_SyncUsesSourceDefaults()
        {
            var result = Parser.Default.ParseArguments<SyncOptions>(new[]
            {
                "sync", "--config", "source.json", "--catalog", "catalog.json",
                "--destination-impl-module", "MyDestination"
            });

            result.WithParsed(options =>
            {
                options.Command.ShouldBe("sync");
                options.DestinationImplModule.ShouldBe("MyDestination");
                options.DestinationImplPath.ShouldBeNull();
                options.DestinationConfig.ShouldBeNull();
                options.ChannelCapacity.ShouldBe(4096);
            });
        }
    }
}
