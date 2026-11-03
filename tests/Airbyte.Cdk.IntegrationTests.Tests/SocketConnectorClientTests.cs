#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Io.Airbyte.Protocol.Protobuf;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class SocketConnectorClientTests
    {
        [Fact]
        public async Task Test_ReadAsync_DecodesJsonProtocolAndTypedRecord()
        {
            if (OperatingSystem.IsWindows())
                return;

            var directory = Path.Combine(Path.GetTempPath(), "ab-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "s1.sock");

            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);

            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptAsync();
                await using var stream = new NetworkStream(accepted, ownsSocket: false);
                await WriteDelimitedAsync(stream, new AirbyteMessageProtobuf
                {
                    AirbyteProtocolMessage =
                        "{\"type\":\"STATE\",\"state\":{\"type\":\"STREAM\",\"stream\":{\"stream_descriptor\":{\"name\":\"users\"},\"stream_state\":{\"id\":1}},\"partition_id\":\"p1\",\"id\":0}}"
                });
                await WriteDelimitedAsync(stream, new AirbyteMessageProtobuf
                {
                    Probe = new AirbyteProbeMessageProtobuf()
                });
            });

            try
            {
                var client = new SocketConnectorClient(path);
                var messages = new List<JsonElement>();
                await client.ReadAsync(message =>
                {
                    messages.Add(message);
                    return Task.CompletedTask;
                }, CancellationToken.None);

                messages.Count.ShouldBe(1);
                messages[0].GetProperty("type").GetString().ShouldBe("STATE");
            }
            finally
            {
                await server;
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task Test_ReadAsync_RejectsTypedRecordWithoutSchema()
        {
            if (OperatingSystem.IsWindows())
                return;

            var directory = Path.Combine(Path.GetTempPath(), "ab-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "s1.sock");

            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);

            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptAsync();
                await using var stream = new NetworkStream(accepted, ownsSocket: false);
                await WriteDelimitedAsync(stream, new AirbyteMessageProtobuf
                {
                    Record = new AirbyteRecordMessageProtobuf
                    {
                        StreamName = "users",
                        EmittedAtMs = 5,
                        PartitionId = "p1"
                    }
                });
            });

            try
            {
                var client = new SocketConnectorClient(path);
                await Should.ThrowAsync<NotSupportedException>(() =>
                    client.ReadAsync(_ => Task.CompletedTask, CancellationToken.None));
            }
            finally
            {
                await server;
                Directory.Delete(directory, true);
            }
        }

        private static async Task WriteDelimitedAsync(Stream stream, AirbyteMessageProtobuf message)
        {
            var payload = message.ToByteArray();
            var prefix = new List<byte>();
            var length = (uint)payload.Length;
            while (length >= 0x80)
            {
                prefix.Add((byte)(length | 0x80));
                length >>= 7;
            }

            prefix.Add((byte)length);
            await stream.WriteAsync(prefix.ToArray());
            await stream.WriteAsync(payload);
            await stream.FlushAsync();
        }
    }
}
