using CommandLine;

namespace Airbyte.Cdk
{
    [Verb("publish", HelpText = "Publish a connector from this repo. Uses the last commit details for getting the connector path.")]
    public class PublishOptions:Options
    {
        [Option('p', "push", Required = false, HelpText = "Push the end result or not")]
        public bool Push { get; set; } = true;
    }
}