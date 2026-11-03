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
                    await SocketFor(StreamKey(message.Record?.Namespace, message.Record?.Stream))
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

        private SocketDataChannelWriter SocketFor(string key)
        {
            if (_sockets.Count == 1)
                return _sockets[0];

            var hash = Fnv1a(key);
            return _sockets[(int)(hash % (uint)_sockets.Count)];
        }

        private static string StateKey(AirbyteStateMessage? state)
        {
            if (state?.Stream?.StreamDescriptor is { } descriptor)
                return StreamKey(descriptor.Namespace, descriptor.Name);

            var first = state?.Global?.StreamStates?.FirstOrDefault()?.StreamDescriptor;
            return first == null ? string.Empty : StreamKey(first.Namespace, first.Name);
        }

        private static string StreamKey(string? ns, string? name) => $"{ns}\u0000{name}";

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
