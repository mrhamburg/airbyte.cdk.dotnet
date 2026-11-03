#nullable enable
using System;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Io.Airbyte.Protocol.Protobuf;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Reads the length-delimited Protobuf stream the connector writes to a Unix domain socket.
    /// The harness is the client; the connector binds and listens.
    /// </summary>
    public sealed class SocketConnectorClient
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(15);

        private readonly string _socketPath;

        public SocketConnectorClient(string socketPath) => _socketPath = socketPath;

        public async Task ReadAsync(Func<JsonElement, Task> onMessage, CancellationToken cancellationToken)
        {
            await WaitForSocketAsync(cancellationToken);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await ConnectAsync(socket, cancellationToken);
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var protobuf = await ReadDelimitedAsync(stream, cancellationToken);
                if (protobuf == null)
                    return;

                if (TryConvert(protobuf, out var message))
                    await onMessage(message);
            }
        }

        private async Task WaitForSocketAsync(CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;
            while (!File.Exists(_socketPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - started > ConnectTimeout)
                    throw new TimeoutException($"Socket file {_socketPath} was not created within {ConnectTimeout}");

                await Task.Delay(100, cancellationToken);
            }
        }

        private async Task ConnectAsync(Socket socket, CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;
            while (true)
            {
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken);
                    return;
                }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused &&
                                                DateTime.UtcNow - started < ConnectTimeout)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }

        private static async Task<AirbyteMessageProtobuf?> ReadDelimitedAsync(Stream stream,
            CancellationToken cancellationToken)
        {
            ulong length = 0;
            var shift = 0;
            var one = new byte[1];

            while (true)
            {
                var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken);
                if (read == 0)
                    return shift == 0 ? null : throw new EndOfStreamException("Truncated protobuf length prefix");

                var value = one[0];
                length |= (ulong)(value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                    break;

                shift += 7;
                if (shift > 63)
                    throw new InvalidDataException("Invalid protobuf length prefix");
            }

            if (length > int.MaxValue)
                throw new InvalidDataException($"Protobuf message is too large: {length}");

            var payload = new byte[(int)length];
            var offset = 0;
            while (offset < payload.Length)
            {
                var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException("Truncated protobuf payload");

                offset += read;
            }

            return AirbyteMessageProtobuf.Parser.ParseFrom(payload);
        }

        private static bool TryConvert(AirbyteMessageProtobuf message, out JsonElement result)
        {
            result = default;
            if (message.TypeCase == AirbyteMessageProtobuf.TypeOneofCase.AirbyteProtocolMessage)
            {
                using var document = JsonDocument.Parse(message.AirbyteProtocolMessage);
                result = document.RootElement.Clone();
                return true;
            }

            if (message.TypeCase != AirbyteMessageProtobuf.TypeOneofCase.Record)
                return false;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "RECORD");
                writer.WriteStartObject("record");
                writer.WriteString("stream", message.Record.StreamName);
                if (!string.IsNullOrEmpty(message.Record.StreamNamespace))
                    writer.WriteString("namespace", message.Record.StreamNamespace);
                writer.WriteNumber("emitted_at", message.Record.EmittedAtMs);
                if (!string.IsNullOrEmpty(message.Record.PartitionId))
                    writer.WriteString("partition_id", message.Record.PartitionId);
                writer.WriteStartObject("data");
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            using var parsed = JsonDocument.Parse(buffer.ToArray());
            result = parsed.RootElement.Clone();
            return true;
        }
    }
}
