#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Io.Airbyte.Protocol.Protobuf;

namespace Airbyte.Cdk.Protobuf
{
    /// <summary>
    /// Length-delimited framing for <see cref="AirbyteMessageProtobuf"/>: a varint byte length
    /// followed by the serialized message, matching the platform's
    /// <c>writeDelimitedTo</c>/<c>readRawVarint32</c> pair.
    /// </summary>
    public static class ProtobufDelimitedCodec
    {
        /// <summary>Upper bound that stops a corrupt length prefix from allocating unbounded memory.</summary>
        public const int MaxMessageSize = 64 * 1024 * 1024;

        public static async Task WriteAsync(Stream stream, AirbyteMessageProtobuf message,
            CancellationToken cancellationToken = default)
        {
            var payload = message.ToByteArray();
            var prefix = new byte[5];
            var length = (uint)payload.Length;
            var index = 0;

            while (length >= 0x80)
            {
                prefix[index++] = (byte)(length | 0x80);
                length >>= 7;
            }

            prefix[index++] = (byte)length;

            await stream.WriteAsync(prefix.AsMemory(0, index), cancellationToken);
            await stream.WriteAsync(payload.AsMemory(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        /// <summary>
        /// Reads one delimited message. Returns null at a clean end of stream.
        /// </summary>
        public static async Task<AirbyteMessageProtobuf?> ReadAsync(Stream stream,
            CancellationToken cancellationToken = default)
        {
            var prefix = new byte[5];
            var prefixLength = 0;
            var length = 0UL;
            var shift = 0;

            while (true)
            {
                var read = await stream.ReadAsync(prefix.AsMemory(prefixLength, 1), cancellationToken);
                if (read == 0)
                {
                    if (prefixLength == 0)
                        return null;

                    throw new EndOfStreamException("Truncated protobuf message length prefix");
                }

                var value = prefix[prefixLength++];
                length |= (ulong)(value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                    break;

                shift += 7;
                if (shift > 28)
                    throw new InvalidDataException("Invalid protobuf message length prefix");
            }

            if (length > MaxMessageSize)
                throw new InvalidDataException($"Protobuf message is too large: {length} bytes");

            var payload = new byte[(int)length];
            var offset = 0;
            while (offset < payload.Length)
            {
                var read = await stream.ReadAsync(payload.AsMemory(offset), cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException("Truncated protobuf message payload");

                offset += read;
            }

            return AirbyteMessageProtobuf.Parser.ParseFrom(payload);
        }
    }
}
