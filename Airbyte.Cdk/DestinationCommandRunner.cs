#nullable enable
using System;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Transport;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Runs the <c>write</c> command over either the socket or STDIO data channel.
    /// </summary>
    internal static class DestinationCommandRunner
    {
        public static async Task RunAsync(CliContext context, ConnectorSpecification spec,
            DataChannelSettings dataChannel)
        {
            var destination = context.GetDestination();
            var config = InputFileResolver.GetConfig(context.Options, spec, context.Logger);
            var catalog = InputFileResolver.GetCatalog(context.Options, context.Logger);

            if (dataChannel.IsSocket)
            {
                var reader = new CompositeDataChannelReader(
                    new StdioDataChannelReader(Console.In, context.Logger.Warn),
                    new SocketDataChannelReader(dataChannel.SocketPaths, catalog));
                await AirbyteEntrypoint.RunWriteLoop(reader, Console.Out, context.Logger, destination, config, catalog);
                return;
            }

            await AirbyteEntrypoint.RunWriteLoop(Console.In, Console.Out, context.Logger, destination, config, catalog);
        }
    }
}
