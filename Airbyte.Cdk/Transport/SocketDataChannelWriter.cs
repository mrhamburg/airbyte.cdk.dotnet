#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Protobuf;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Writes length-delimited Protobuf messages to one Unix domain socket. The connector binds and
    /// listens, then accepts the single connection the platform/destination creates.
    /// </summary>
    public sealed class SocketDataChannelWriter : IDataChannelWriter
    {
        private readonly string _socketPath;
        private const int ChannelCapacity = 4_096;

        private readonly Channel<AirbyteMessage> _channel = Channel.CreateBounded<AirbyteMessage>(
            new BoundedChannelOptions(ChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        private readonly TaskCompletionSource<NetworkStream> _connection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cancellation = new();
        private Socket? _listener;
        private Task _acceptTask = Task.CompletedTask;
        private Task _writerTask = Task.CompletedTask;
        private bool _started;
        private bool _disposed;

        public SocketDataChannelWriter(string socketPath) => _socketPath = socketPath;

        public string SocketPath => _socketPath;

        public void Start()
        {
            if (_started)
                return;

            _started = true;
            _acceptTask = Task.Run(AcceptAsync);
            _writerTask = Task.Run(WriteLoopAsync);
        }

        public async ValueTask WriteAsync(AirbyteMessage message, CancellationToken cancellationToken = default)
        {
            await _channel.Writer.WriteAsync(message, cancellationToken);
            if (_channel.Reader.Completion.IsCompleted)
                await _channel.Reader.Completion;
        }

        public bool TryWrite(AirbyteMessage message) => _channel.Writer.TryWrite(message);

        public async Task CompleteAsync(Exception? error = null)
        {
            if (!_started)
                return;

            _channel.Writer.TryComplete(error);
            try
            {
                await _writerTask;
            }
            catch (Exception)
            {
                // The writer task records the failure; CompleteAsync must not mask the original error.
            }
            finally
            {
                _cancellation.Cancel();
                try
                {
                    _listener?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }

                try
                {
                    await _acceptTask;
                }
                catch (Exception)
                {
                }

                TryDeleteSocket();
            }
        }

        private async Task AcceptAsync()
        {
            try
            {
                TryDeleteSocket();

                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
                _listener.Listen(1);

                var socket = await _listener.AcceptAsync(_cancellation.Token);
                _listener.Dispose();
                _listener = null;
                _connection.TrySetResult(new NetworkStream(socket, ownsSocket: true));
            }
            catch (OperationCanceledException)
            {
                _connection.TrySetCanceled();
            }
            catch (Exception e)
            {
                _connection.TrySetException(e);
                _channel.Writer.TryComplete(e);
            }
        }

        private async Task WriteLoopAsync()
        {
            NetworkStream? stream = null;
            try
            {
                stream = await _connection.Task;
                await foreach (var message in _channel.Reader.ReadAllAsync(_cancellation.Token))
                {
                    var protobuf = AirbyteProtobufMapper.ToProtobuf(message);
                    await ProtobufDelimitedCodec.WriteAsync(stream, protobuf, _cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _channel.Writer.TryComplete(e);
                throw;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        private void TryDeleteSocket()
        {
            try
            {
                if (System.IO.File.Exists(_socketPath))
                    System.IO.File.Delete(_socketPath);
            }
            catch (System.IO.IOException)
            {
                // Another process may own the path; bind will surface a useful error.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            _cancellation.Cancel();
            await CompleteAsync();
            _cancellation.Dispose();
        }
    }
}
