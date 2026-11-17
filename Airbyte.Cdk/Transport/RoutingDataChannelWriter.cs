#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Routes messages for the selected transport. In socket mode records and states go to one of
    /// the Unix domain sockets, states are also copied to stdout for the bookkeeper, and logs,
    /// traces and control messages stay on stdout.
    /// </summary>
    public sealed class RoutingDataChannelWriter : IDataChannelWriter
    {
        private readonly DataChannelSettings _settings;
        private readonly IDataChannelWriter _stdout;
        private readonly IReadOnlyList<SocketDataChannelWriter> _sockets;

        private RoutingDataChannelWriter(DataChannelSettings settings, IDataChannelWriter stdout,
            IReadOnlyList<SocketDataChannelWriter> sockets)
        {
            _settings = settings;
            _stdout = stdout;
            _sockets = sockets;
        }

        public static RoutingDataChannelWriter Create(DataChannelSettings settings, TextWriter stdout)
        {
            if (!settings.IsSocket)
                return new RoutingDataChannelWriter(settings, new StdioDataChannelWriter(stdout),
                    Array.Empty<SocketDataChannelWriter>());

            var sockets = settings.SocketPaths.Select(x => new SocketDataChannelWriter(x)).ToArray();
            foreach (var socket in sockets)
                socket.Start();

            return new RoutingDataChannelWriter(settings, new StdioDataChannelWriter(stdout), sockets);
        }

        public async ValueTask WriteAsync(AirbyteMessage message, CancellationToken cancellationToken = default)
        {
            if (!_settings.IsSocket)
            {
                await _stdout.WriteAsync(message, cancellationToken);
                return;
            }

            switch (message.Type)
            {
                case Type.RECORD:
                    await SocketFor(KeyFor(message.Record?.Namespace, message.Record?.Stream))
                        .WriteAsync(message, cancellationToken);
                    return;
                case Type.STATE:
                    await _stdout.WriteAsync(message, cancellationToken);
                    await SocketFor(StateKey(message.State)).WriteAsync(message, cancellationToken);
                    return;
                default:
                    await _stdout.WriteAsync(message, cancellationToken);
                    return;
            }
        }

        public bool TryWrite(AirbyteMessage message)
        {
            if (!_settings.IsSocket)
                return _stdout.TryWrite(message);

            switch (message.Type)
            {
                case Type.RECORD:
                    return SocketFor(KeyFor(message.Record?.Namespace, message.Record?.Stream))
                        .TryWrite(message);
                case Type.STATE:
                    _stdout.TryWrite(message);
                    return SocketFor(StateKey(message.State)).TryWrite(message);
                default:
                    return _stdout.TryWrite(message);
            }
        }

        public async Task CompleteAsync(Exception? error = null)
        {
            if (!_settings.IsSocket)
            {
                await _stdout.CompleteAsync(error);
                return;
            }

            foreach (var socket in _sockets)
                await socket.CompleteAsync(error);
        }

        private SocketDataChannelWriter SocketFor(StreamKey key)
        {
            if (_sockets.Count == 1)
                return _sockets[0];

            var hash = Fnv1a(key.ToString());
            return _sockets[(int)(hash % (uint)_sockets.Count)];
        }

        private static StreamKey StateKey(AirbyteStateMessage? state)
        {
            if (state?.Stream?.StreamDescriptor is { } descriptor)
                return StreamKey.From(descriptor);

            var first = state?.Global?.StreamStates?.FirstOrDefault()?.StreamDescriptor;
            return first == null ? StreamKey.Empty : StreamKey.From(first);
        }

        private static StreamKey KeyFor(string? ns, string? name) => new(name ?? string.Empty, ns);

        private static uint Fnv1a(string value)
        {
            var hash = 2166136261u;
            foreach (var character in value)
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return hash;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var socket in _sockets)
                await socket.DisposeAsync();
        }
    }
}
