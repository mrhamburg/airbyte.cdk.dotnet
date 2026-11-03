#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Runs a source and a destination in one process, connected by an in-memory channel. Messages
    /// are handed over as objects, so no stdout JSON or socket Protobuf serialization is involved.
    /// Source logs and traces still go to the supplied logger; records and states go to the channel.
    /// </summary>
    public static class InMemoryDataChannel
    {
        public const int DefaultCapacity = 4_096;

        public static async Task RunAsync(Source source, Destination destination, AirbyteLogger logger,
            JsonElement sourceConfig, JsonElement destinationConfig, ConfiguredAirbyteCatalog catalog,
            JsonElement state, int capacity = DefaultCapacity, TextWriter? stateOutput = null)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Channel capacity must be greater than zero");

            var channel = Channel.CreateBounded<AirbyteMessage>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var destinationTask = Task.Run(async () =>
            {
                try
                {
                    var dispatcher = new DestinationMessageDispatcher(destination, logger, destinationConfig,
                        catalog, stateOutput ?? Console.Out);
                    await foreach (var message in channel.Reader.ReadAllAsync())
                        await dispatcher.HandleAsync(message);
                    await dispatcher.CompleteAsync();
                }
                catch (Exception e)
                {
                    // Wake a source blocked on the bounded channel and let the caller observe the
                    // destination failure instead of waiting for a produce that can never finish.
                    channel.Writer.TryComplete(e);
                    throw;
                }
            });

            try
            {
                await source.Read(logger, channel.Writer, sourceConfig, catalog, state);
            }
            catch
            {
                channel.Writer.TryComplete();
                try
                {
                    await destinationTask;
                }
                catch (Exception)
                {
                    // The source exception is the useful one.
                }

                throw;
            }

            channel.Writer.TryComplete();
            await destinationTask;
        }
    }
}
