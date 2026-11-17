#nullable enable
using System;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Transport;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Dispatches the selected CLI command to the matching runner.
    /// </summary>
    internal static class CommandDispatcher
    {
        public static async Task LaunchAsync(CliContext context)
        {
            var spec = context.Connector.Spec();
            var dataChannel = DataChannelSettings.FromEnvironment();

            switch (context.Options.Command.ToLowerInvariant())
            {
                case "spec":
                    AirbyteEntrypoint.ToConsole(new AirbyteMessage
                    {
                        Type = Type.SPEC,
                        Spec = spec
                    });
                    break;
                case "check":
                    var result = context.Connector.Check(context.Logger,
                        InputFileResolver.GetConfig(context.Options, spec, context.Logger));
                    if (result.Status == Status.SUCCEEDED)
                        context.Logger.Info("Check succeeded");
                    else
                        context.Logger.Error("Check failed");

                    AirbyteEntrypoint.ToConsole(new AirbyteMessage
                    {
                        Type = Type.CONNECTION_STATUS,
                        ConnectionStatus = result
                    });
                    break;
                case "discover":
                    AirbyteEntrypoint.ToConsole(new AirbyteMessage
                    {
                        Type = Type.CATALOG,
                        Catalog = context.GetSource().Discover(context.Logger,
                            InputFileResolver.GetConfig(context.Options, spec, context.Logger))
                    });
                    break;
                case "read":
                    await SourceCommandRunner.RunAsync(context, spec, dataChannel);
                    break;
                case "write":
                    await DestinationCommandRunner.RunAsync(context, spec, dataChannel);
                    break;
                default:
                    throw new ArgumentException($"Unexpected command: {context.Options.Command}",
                        nameof(context.Options.Command));
            }
        }
    }
}
