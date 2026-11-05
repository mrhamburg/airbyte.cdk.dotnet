using CommandLine;

namespace Airbyte.Cdk
{
    [Verb("sync", HelpText = "Reads a source and writes to a destination in one process over an in-memory channel")]
    public class SyncOptions : Options
    {
        public override string Command { get; set; } = "sync";

        [Option('s', "state", Required = false, HelpText = "path to the json-encoded state file")]
        public string State { get; set; }

        [Option("destination-impl-path", Required = false,
            HelpText = "Destination assembly path; defaults to the source assembly")]
        public string DestinationImplPath { get; set; }

        [Option("destination-impl-module", Required = true,
            HelpText = "Name of the destination Connector subclass")]
        public string DestinationImplModule { get; set; }

        [Option("destination-config", Required = false,
            HelpText = "Destination config file; defaults to the source config")]
        public string DestinationConfig { get; set; }

        [Option("channel-capacity", Required = false, Default = 4096,
            HelpText = "Number of messages buffered between source and destination")]
        public int ChannelCapacity { get; set; }
    }
}
