using CommandLine;

namespace Airbyte.Cdk
{
    public abstract class Options
    {
        [Option('f', "config", HelpText = "Config file location")]
        public string Config { get; set; } = string.Empty;

        [Option('l', "catalog", HelpText = "Catalog file location")]
        public string Catalog { get; set; } = string.Empty;

        public virtual string Command { get; set; } = string.Empty;
    }
}
