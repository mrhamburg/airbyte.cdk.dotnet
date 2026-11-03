#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Protobuf;
using Airbyte.Cdk.Sources.Utils;
using Airbyte.Cdk.Transport;
using Google.Protobuf;
using Io.Airbyte.Protocol.Protobuf;
using Shouldly;
using Xunit;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests.Transport
{
    public class TestDataChannelSettings
    {
        [Fact]
        public void Test_Parse_DefaultsToStdioJsonl()
        {
            var settings = DataChannelSettings.Parse(null, null, null);

            settings.Medium.ShouldBe(DataChannelMedium.Stdio);
            settings.Format.ShouldBe(DataChannelFormat.Jsonl);
            settings.IsSocket.ShouldBeFalse();
        }

        [Fact]
        public void Test_Parse_SocketProtobufSplitsPaths()
        {
            var settings = DataChannelSettings.Parse("socket", "protobuf", "/tmp/a.sock, /tmp/b.sock");

            settings.Medium.ShouldBe(DataChannelMedium.Socket);
            settings.Format.ShouldBe(DataChannelFormat.Protobuf);
            settings.SocketPaths.ShouldBe(new[] { "/tmp/a.sock", "/tmp/b.sock" });
        }

        [Theory]
        [InlineData("socket", "jsonl", "/tmp/a.sock")]
        [InlineData("socket", "protobuf", "")]
        [InlineData("carrier-pigeon", "protobuf", "/tmp/a.sock")]
        public void Test_Parse_RejectsUnsupportedCombinations(string medium, string format, string paths)
        {
            Should.Throw<InvalidOperationException>(() => DataChannelSettings.Parse(medium, format, paths));
        }
    }

    public class TestProtobufFraming
    {
        [Fact]
        public async Task Test_WriteAndRead_RoundTripsMultipleMessages()
        {
            var stream = new MemoryStream();
            var first = AirbyteProtobufMapper.ToProtobuf(Record("users", 1));
            var second = AirbyteProtobufMapper.ToProtobuf(State("users", 1, 1));

            await ProtobufDelimitedCodec.WriteAsync(stream, first);
            await ProtobufDelimitedCodec.WriteAsync(stream, second);
            stream.Position = 0;

            var readFirst = await ProtobufDelimitedCodec.ReadAsync(stream);
            var readSecond = await ProtobufDelimitedCodec.ReadAsync(stream);
            var end = await ProtobufDelimitedCodec.ReadAsync(stream);

            readFirst.ShouldNotBeNull();
            readSecond.ShouldNotBeNull();
            end.ShouldBeNull();

            var record = AirbyteProtobufMapper.FromProtobuf(readFirst!);
            var state = AirbyteProtobufMapper.FromProtobuf(readSecond!);
            record!.Record!.Data.GetProperty("id").GetInt32().ShouldBe(1);
            state!.State!.Stream!.StreamDescriptor.Name.ShouldBe("users");
        }

        [Fact]
        public async Task Test_Read_ReturnsNullOnEmptyStream()
        {
            (await ProtobufDelimitedCodec.ReadAsync(new MemoryStream())).ShouldBeNull();
        }

        [Fact]
        public async Task Test_Read_RejectsTruncatedPayload()
        {
            var payload = AirbyteProtobufMapper.ToProtobuf(Record("users", 1)).ToByteArray();
            var framed = new MemoryStream();
            framed.WriteByte((byte)payload.Length);
            framed.Write(payload.AsSpan(0, payload.Length - 1));
            framed.Position = 0;

            await Should.ThrowAsync<EndOfStreamException>(async () =>
                await ProtobufDelimitedCodec.ReadAsync(framed));
        }

        [Fact]
        public void Test_FromProtobuf_DecodesTypedRecordWithCatalogSchema()
        {
            var record = new AirbyteRecordMessageProtobuf
            {
                StreamName = "users",
                EmittedAtMs = 10,
                PartitionId = "p1"
            };
            record.Data.Add(new AirbyteValueProtobuf { Integer = 42 });
            record.Data.Add(new AirbyteValueProtobuf { String = "alice" });
            var message = new AirbyteMessageProtobuf { Record = record };
            var catalog = new ConfiguredAirbyteCatalog
            {
                Streams = new[]
                {
                    new ConfiguredAirbyteStream
                    {
                        Stream = new AirbyteStream
                        {
                            Name = "users",
                            JsonSchema = JsonDocument.Parse(
                                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"id\":{\"type\":\"integer\"}}}")
                                .RootElement.Clone()
                        }
                    }
                }
            };

            var decoded = AirbyteProtobufMapper.FromProtobuf(message, catalog);

            decoded!.Record!.Stream.ShouldBe("users");
            decoded.Record.PartitionId.ShouldBe("p1");
            decoded.Record.Data.GetProperty("name").GetString().ShouldBe("alice");
            decoded.Record.Data.GetProperty("id").GetInt32().ShouldBe(42);
        }

        private static AirbyteMessage Record(string stream, int id) => new()
        {
            Type = Type.RECORD,
            Record = new AirbyteRecordMessage
            {
                Stream = stream,
                EmittedAt = 1,
                Data = JsonDocument.Parse($"{{\"id\":{id}}}").RootElement.Clone()
            }
        };

        private static AirbyteMessage State(string stream, int count, int id) => new()
        {
            Type = Type.STATE,
            State = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState
                {
                    StreamDescriptor = new StreamDescriptor { Name = stream },
                    StreamState = JsonDocument.Parse("{\"id\":1}").RootElement.Clone()
                },
                SourceStats = new AirbyteStateStats { RecordCount = count },
                PartitionId = "p1",
                Id = id
            }
        };
    }

    public class TestPartitionTracker
    {
        [Fact]
        public void Test_DisabledTracker_ChangesNothing()
        {
            var tracker = new PartitionTracker(false);
            var record = Record("users");
            var descriptor = new StreamDescriptor { Name = "users" };

            tracker.ObserveRecord(record, descriptor);

            record.PartitionId.ShouldBeNull();
        }

        [Fact]
        public void Test_RecordsSharePartitionAndStateCountsThem()
        {
            var tracker = new PartitionTracker(true);
            var descriptor = new StreamDescriptor { Name = "users" };
            tracker.BeginStream(descriptor);

            var first = Record("users");
            var second = Record("users");
            tracker.ObserveRecord(first, descriptor);
            tracker.ObserveRecord(second, descriptor);
            first.PartitionId.ShouldBe(second.PartitionId);

            var state = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState { StreamDescriptor = descriptor }
            };
            tracker.PrepareState(state, descriptor);

            state.PartitionId.ShouldBe(first.PartitionId);
            state.Id.ShouldBe(1);
            state.SourceStats!.RecordCount.ShouldBe(2);

            var after = Record("users");
            tracker.ObserveRecord(after, descriptor);
            after.PartitionId.ShouldNotBe(first.PartitionId);

            var nextState = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState { StreamDescriptor = descriptor }
            };
            tracker.PrepareState(nextState, descriptor);
            nextState.Id.ShouldBe(2);
            nextState.SourceStats!.RecordCount.ShouldBe(1);
            nextState.PartitionId.ShouldBe(after.PartitionId);
        }

        private static AirbyteRecordMessage Record(string stream) => new()
        {
            Stream = stream,
            Data = JsonDocument.Parse("{}").RootElement.Clone()
        };
    }

    public class TestDestinationMessageDispatcher
    {
        [Fact]
        public async Task Test_DefersStateUntilAllRecordsAreWritten()
        {
            var destination = new RecordingDestination();
            var dispatcher = Dispatcher(destination, TextWriter.Null);

            await dispatcher.HandleAsync(State(count: 2, id: 1, partition: "p1"));
            destination.Messages.ShouldBeEmpty();

            await dispatcher.HandleAsync(Record(partition: "p1"));
            destination.Messages.Count(x => x.Type == Type.STATE).ShouldBe(0);

            await dispatcher.HandleAsync(Record(partition: "p1"));
            destination.Messages.Count(x => x.Type == Type.STATE).ShouldBe(1);
            await dispatcher.CompleteAsync();
        }

        [Fact]
        public async Task Test_CommitsStatesInIdOrderAcrossPartitions()
        {
            var destination = new RecordingDestination();
            var dispatcher = Dispatcher(destination, TextWriter.Null);

            await dispatcher.HandleAsync(Record(partition: "p2"));
            await dispatcher.HandleAsync(State(count: 1, id: 2, partition: "p2"));
            await dispatcher.HandleAsync(Record(partition: "p1"));
            await dispatcher.HandleAsync(State(count: 1, id: 1, partition: "p1"));

            destination.Messages.Where(x => x.Type == Type.STATE).Select(x => x.State!.Id)
                .ShouldBe(new int?[] { 1, 2 });
        }

        [Fact]
        public async Task Test_CompleteFailsOnUnfinishedState()
        {
            var dispatcher = Dispatcher(new RecordingDestination(), TextWriter.Null);
            await dispatcher.HandleAsync(State(count: 1, id: 1, partition: "p1"));

            await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.CompleteAsync());
        }

        private static DestinationMessageDispatcher Dispatcher(Destination destination, TextWriter output) =>
            new(destination, new AirbyteLogger(_ => { }), JsonDocument.Parse("{}").RootElement.Clone(),
                new ConfiguredAirbyteCatalog { Streams = Array.Empty<ConfiguredAirbyteStream>() }, output);

        private static AirbyteMessage Record(string partition) => new()
        {
            Type = Type.RECORD,
            Record = new AirbyteRecordMessage
            {
                Stream = "users",
                EmittedAt = 1,
                Data = JsonDocument.Parse("{}").RootElement.Clone(),
                PartitionId = partition
            }
        };

        private static AirbyteMessage State(int count, int id, string partition) => new()
        {
            Type = Type.STATE,
            State = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState { StreamDescriptor = new StreamDescriptor { Name = "users" } },
                SourceStats = new AirbyteStateStats { RecordCount = count },
                PartitionId = partition,
                Id = id
            }
        };

        private sealed class RecordingDestination : Destination
        {
            public List<AirbyteMessage> Messages { get; } = new();

            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };

            public override Task Write(AirbyteLogger logger, JsonElement config,
                ConfiguredAirbyteCatalog catalog, AirbyteMessage msg)
            {
                Messages.Add(msg);
                return Task.CompletedTask;
            }
        }
    }
}

namespace Airbyte.Cdk.Tests.Transport
{
    public class TestSocketTransport
    {
        [Fact]
        public async Task Test_SourceSocketWriter_StreamsDelimitedProtobufToClient()
        {
            if (OperatingSystem.IsWindows())
                return;

            var path = Path.Combine(Path.GetTempPath(), "ab-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
            var writer = new SocketDataChannelWriter(path);
            writer.Start();
            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);

                var started = DateTime.UtcNow;
                while (true)
                {
                    try
                    {
                        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
                        break;
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(10))
                            throw;
                        await Task.Delay(20);
                    }
                }

                await using var stream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: false);
                await writer.WriteAsync(Record("users", 1));
                await writer.WriteAsync(State("users"));
                await writer.CompleteAsync();

                var first = await ProtobufDelimitedCodec.ReadAsync(stream);
                var second = await ProtobufDelimitedCodec.ReadAsync(stream);
                var end = await ProtobufDelimitedCodec.ReadAsync(stream);

                AirbyteProtobufMapper.FromProtobuf(first!)!.Record!.Data.GetProperty("id").GetInt32().ShouldBe(1);
                AirbyteProtobufMapper.FromProtobuf(second!)!.State!.PartitionId.ShouldBe("p1");
                end.ShouldBeNull();
            }
            finally
            {
                await writer.DisposeAsync();
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        private static AirbyteMessage Record(string stream, int id) => new()
        {
            Type = Type.RECORD,
            Record = new AirbyteRecordMessage
            {
                Stream = stream,
                EmittedAt = 1,
                Data = JsonDocument.Parse($"{{\"id\":{id}}}").RootElement.Clone()
            }
        };

        private static AirbyteMessage State(string stream) => new()
        {
            Type = Type.STATE,
            State = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState { StreamDescriptor = new StreamDescriptor { Name = stream } },
                SourceStats = new AirbyteStateStats { RecordCount = 1 },
                PartitionId = "p1",
                Id = 0
            }
        };
    }
}

namespace Airbyte.Cdk.Tests.Transport
{
    public class TestSocketRouting
    {
        [Fact]
        public async Task Test_RoutesRecordAndStateToSocketAndLogAndStateToStdout()
        {
            if (OperatingSystem.IsWindows())
                return;

            var path = Path.Combine(Path.GetTempPath(), "ab-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
            var stdout = new StringWriter();
            var settings = DataChannelSettings.Parse("SOCKET", "PROTOBUF", path);
            await using var sink = RoutingDataChannelWriter.Create(settings, stdout);

            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Unspecified);
                var started = DateTime.UtcNow;
                while (true)
                {
                    try
                    {
                        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
                        break;
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(10))
                            throw;
                        await Task.Delay(20);
                    }
                }

                await using var stream = new System.Net.Sockets.NetworkStream(socket, ownsSocket: false);

                await sink.WriteAsync(new AirbyteMessage
                {
                    Type = Type.RECORD,
                    Record = new AirbyteRecordMessage
                    {
                        Stream = "users", EmittedAt = 1,
                        Data = JsonDocument.Parse("{\"id\":1}").RootElement.Clone(),
                        PartitionId = "p1"
                    }
                });
                await sink.WriteAsync(new AirbyteMessage
                {
                    Type = Type.LOG,
                    Log = new AirbyteLogMessage { Message = "hello" }
                });
                await sink.WriteAsync(new AirbyteMessage
                {
                    Type = Type.STATE,
                    State = new AirbyteStateMessage
                    {
                        Type = AirbyteStateType.STREAM,
                        Stream = new AirbyteStreamState
                        {
                            StreamDescriptor = new StreamDescriptor { Name = "users" }
                        },
                        PartitionId = "p1",
                        Id = 0,
                        SourceStats = new AirbyteStateStats { RecordCount = 1 }
                    }
                });
                await sink.CompleteAsync();

                var first = await ProtobufDelimitedCodec.ReadAsync(stream);
                var second = await ProtobufDelimitedCodec.ReadAsync(stream);
                var end = await ProtobufDelimitedCodec.ReadAsync(stream);

                AirbyteProtobufMapper.FromProtobuf(first!)!.Type.ShouldBe(Type.RECORD);
                AirbyteProtobufMapper.FromProtobuf(second!)!.Type.ShouldBe(Type.STATE);
                end.ShouldBeNull();

                var lines = stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                lines.Length.ShouldBe(2);
                lines.Select(x => JsonDocument.Parse(x).RootElement.GetProperty("type").GetString())
                    .ShouldBe(new[] { "LOG", "STATE" });
            }
            finally
            {
                await sink.DisposeAsync();
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    public class TestPartitionedStateEmission
    {
        [Fact]
        public async Task Test_StateEmitterAndTrackingWriterStampAndRotatePartitions()
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var tracker = new PartitionTracker(true);
            var emitter = new StateEmitter(channel.Writer, tracker);
            var tracking = new StateTrackingChannelWriter(channel.Writer, emitter, tracker)
            {
                CurrentDescriptor = new StreamDescriptor { Name = "users" }
            };
            tracker.BeginStream(new StreamDescriptor { Name = "users" });

            await tracking.WriteAsync(new AirbyteMessage
            {
                Type = Type.RECORD,
                Record = new AirbyteRecordMessage
                {
                    Stream = "users",
                    Data = JsonDocument.Parse("{\"id\":1}").RootElement.Clone()
                }
            });
            await emitter.EmitAsync(new StreamDescriptor { Name = "users" },
                JsonDocument.Parse("{\"id\":1}").RootElement.Clone(), 0);
            await tracking.WriteAsync(new AirbyteMessage
            {
                Type = Type.RECORD,
                Record = new AirbyteRecordMessage
                {
                    Stream = "users",
                    Data = JsonDocument.Parse("{\"id\":2}").RootElement.Clone()
                }
            });
            channel.Writer.Complete();

            var messages = new List<AirbyteMessage>();
            await foreach (var message in channel.Reader.ReadAllAsync())
                messages.Add(message);

            var records = messages.Where(x => x.Type == Type.RECORD).ToArray();
            var state = messages.Single(x => x.Type == Type.STATE);

            records[0].Record!.PartitionId.ShouldBe(state.State!.PartitionId);
            records[1].Record!.PartitionId.ShouldNotBe(state.State!.PartitionId);
            state.State!.Id.ShouldBe(1);
            state.State!.SourceStats!.RecordCount.ShouldBe(1);
        }
    }
}

namespace Airbyte.Cdk.Tests.Transport
{
    public class TestSocketDestinationLoop
    {
        [Fact]
        public async Task Test_RunWriteLoop_ReadsSocketsAndCommitsStatesInOrder()
        {
            if (OperatingSystem.IsWindows())
                return;

            var directory = Path.Combine(Path.GetTempPath(), "ab-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            var paths = new[] { Path.Combine(directory, "s1.sock"), Path.Combine(directory, "s2.sock") };
            var first = new SocketDataChannelWriter(paths[0]);
            var second = new SocketDataChannelWriter(paths[1]);
            first.Start();
            second.Start();

            try
            {
                var catalog = new ConfiguredAirbyteCatalog { Streams = Array.Empty<ConfiguredAirbyteStream>() };
                var reader = new SocketDataChannelReader(paths, catalog);
                var destination = new RecordingDestination();
                var loop = AirbyteEntrypoint.RunWriteLoop(reader, TextWriter.Null,
                    new AirbyteLogger(_ => { }), destination, JsonDocument.Parse("{}").RootElement.Clone(),
                    catalog);

                await first.WriteAsync(Record("p1"));
                await first.WriteAsync(Record("p1"));
                await first.WriteAsync(State(2, 1, "p1"));
                await second.WriteAsync(Record("p2"));
                await second.WriteAsync(State(1, 2, "p2"));
                await first.CompleteAsync();
                await second.CompleteAsync();
                await loop;

                destination.Messages.Where(x => x.Type == Type.STATE).Select(x => x.State!.Id)
                    .ShouldBe(new int?[] { 1, 2 });
                destination.Messages.Count(x => x.Type == Type.RECORD).ShouldBe(3);
            }
            finally
            {
                await first.DisposeAsync();
                await second.DisposeAsync();
                Directory.Delete(directory, true);
            }
        }

        private static AirbyteMessage Record(string partition) => new()
        {
            Type = Type.RECORD,
            Record = new AirbyteRecordMessage
            {
                Stream = "users",
                EmittedAt = 1,
                Data = JsonDocument.Parse("{\"id\":1}").RootElement.Clone(),
                PartitionId = partition
            }
        };

        private static AirbyteMessage State(int count, int id, string partition) => new()
        {
            Type = Type.STATE,
            State = new AirbyteStateMessage
            {
                Type = AirbyteStateType.STREAM,
                Stream = new AirbyteStreamState { StreamDescriptor = new StreamDescriptor { Name = "users" } },
                SourceStats = new AirbyteStateStats { RecordCount = count },
                PartitionId = partition,
                Id = id
            }
        };

        private sealed class RecordingDestination : Destination
        {
            public List<AirbyteMessage> Messages { get; } = new();

            public override AirbyteConnectionStatus Check(AirbyteLogger logger, JsonElement config) =>
                new() { Status = Status.SUCCEEDED };

            public override Task Write(AirbyteLogger logger, JsonElement config,
                ConfiguredAirbyteCatalog catalog, AirbyteMessage msg)
            {
                Messages.Add(msg);
                return Task.CompletedTask;
            }
        }
    }
}
