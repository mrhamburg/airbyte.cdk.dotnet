using CommandLine;

namespace Airbyte.Cdk
{
    [Verb("publish", HelpText = "Publish a connector from this repo. Uses the last commit details for getting the connector path.")]
    public class PublishOptions:Options
    {
        [Option('b', "build-only", Required = false, HelpText = "Build only, so do not push")]
        public bool IsBuildOnly { get; set; }
    }
}