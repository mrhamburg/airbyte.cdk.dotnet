#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Protobuf;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Reads length-delimited Protobuf messages from the Unix domain sockets the source created.
    /// The destination is the client: it waits for each socket file and connects.
    /// </summary>
    public sealed class SocketDataChannelReader : IDataChannelReader
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        private readonly IReadOnlyList<string> _socketPaths;
        private readonly ConfiguredAirbyteCatalog _catalog;

        public SocketDataChannelReader(IReadOnlyList<string> socketPaths, ConfiguredAirbyteCatalog catalog)
        {
            _socketPaths = socketPaths;
            _catalog = catalog;
        }

        public async IAsyncEnumerable<AirbyteMessage> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            var tasks = new List<Task>(_socketPaths.Count);
            foreach (var path in _socketPaths)
                tasks.Add(Task.Run(() => ReadSocketAsync(path, channel.Writer, cancellationToken), cancellationToken));

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

        private async Task ReadSocketAsync(string path, ChannelWriter<AirbyteMessage> writer,
            CancellationToken cancellationToken)
        {
            await WaitForSocketAsync(path, cancellationToken);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await ConnectAsync(socket, path, cancellationToken);
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var protobuf = await ProtobufDelimitedCodec.ReadAsync(stream, cancellationToken);
                if (protobuf == null)
                    return;

                var message = AirbyteProtobufMapper.FromProtobuf(protobuf, _catalog);
                if (message != null)
                    await writer.WriteAsync(message, cancellationToken);
            }
        }

        private static async Task ConnectAsync(Socket socket, string path, CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;
            while (true)
            {
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                    return;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused &&
                                                DateTime.UtcNow - started < ConnectTimeout)
                {
                    await Task.Delay(PollInterval, cancellationToken);
                }
            }
        }

        private static async Task WaitForSocketAsync(string path, CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;

            while (!File.Exists(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - started > ConnectTimeout)
                    throw new TimeoutException($"Socket file {path} was not created within {ConnectTimeout}");

                await Task.Delay(PollInterval, cancellationToken);
            }
        }

        public Task CompleteAsync() => Task.CompletedTask;
    }
}
