#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Merges several readers into one stream. Socket mode uses it to read record data from the
    /// sockets while logs, traces and control messages still arrive on stdin.
    /// </summary>
    public sealed class CompositeDataChannelReader : IDataChannelReader
    {
        private readonly IReadOnlyList<IDataChannelReader> _readers;

        public CompositeDataChannelReader(params IDataChannelReader[] readers) => _readers = readers;

        public async IAsyncEnumerable<AirbyteMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var channel = Channel.CreateBounded<AirbyteMessage>(new BoundedChannelOptions(4_096)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            var tasks = new List<Task>(_readers.Count);
            foreach (var reader in _readers)
                tasks.Add(Task.Run(async () =>
                {
                    await foreach (var message in reader.ReadAllAsync(cancellationToken))
                        await channel.Writer.WriteAsync(message, cancellationToken);
                }, cancellationToken));

            var completion = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(tasks);
                    channel.Writer.TryComplete();
                }
                catch (Exception e)
                {
                    channel.Writer.TryComplete(e);
                }
            }, CancellationToken.None);

            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
                yield return message;

            await completion;
        }

        public async Task CompleteAsync()
        {
            foreach (var reader in _readers)
                await reader.CompleteAsync();
        }
    }
}
