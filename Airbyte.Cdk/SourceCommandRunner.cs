#nullable enable
using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Utils;
using Airbyte.Cdk.Transport;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Runs the <c>read</c> command over either the socket or STDIO data channel.
    /// </summary>
    internal static class SourceCommandRunner
    {
        public static async Task RunAsync(CliContext context, ConnectorSpecification spec,
            DataChannelSettings dataChannel)
        {
            var readOptions = (ReadOptions)context.Options;
            var source = context.GetSource();

            if (dataChannel.IsSocket)
            {
                source.PartitionTracker = new PartitionTracker(true);
                await using var sink = RoutingDataChannelWriter.Create(dataChannel, Console.Out);
                var writer = new SinkChannelWriter(sink);
                var sourceLogger = new AirbyteLogger(message => writer.TryWrite(message));
                try
                {
                    await source.Read(sourceLogger, writer,
                        InputFileResolver.GetConfig(context.Options, spec, context.Logger),
                        InputFileResolver.GetCatalog(context.Options, context.Logger),
                        InputFileResolver.GetState(source, readOptions, context.Logger));
                }
                finally
                {
                    await sink.CompleteAsync();
                }

                return;
            }

            Channel<AirbyteMessage> readerChannel = Channel.CreateUnbounded<AirbyteMessage>();
            var readtasks = new[]
            {
                Task.Run(async () =>
                {
                    var writer = readerChannel.Writer;
                    try
                    {
                        var state = InputFileResolver.GetState(source, readOptions, context.Logger);
                        var config = InputFileResolver.GetConfig(context.Options, spec, context.Logger);
                        var catalog = InputFileResolver.GetCatalog(context.Options, context.Logger);

                        // Keep source logs/traces ordered with records and state.
                        var sourceLogger = new AirbyteLogger(message => writer.TryWrite(message));
                        await source.Read(sourceLogger, writer, config, catalog, state);
                    }
                    catch (Exception e)
                    {
                        context.Logger.Exception(e);
                        if (!e.Data.Contains(AirbyteLogger.TraceEmittedDataKey))
                            context.Logger.TraceError("The connector failed",
                                $"{e.GetType().Name}: {e.Message}", e.StackTrace,
                                AirbyteFailureType.system_error);
                        context.Logger.Fatal("Could not process data due to exception: " + e.Message);
                        Environment.ExitCode = 1;
                    }
                    finally
                    {
                        writer.Complete();
                    }
                }),
                Task.Run(async () =>
                {
                    await foreach (var msg in readerChannel.Reader.ReadAllAsync())
                        AirbyteEntrypoint.ToConsole(msg);
                })
            };

            await Task.WhenAll(readtasks);
        }
    }
}
