#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Utils;
using Shouldly;
using Xunit;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Tests.Sources
{
    public class TestSubstreams
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public async Task Test_Substream_ReadsChildOncePerParentRecord()
        {
            var parent = new RecordingStream("projects", Json("{\"id\":\"p1\"}"), Json("{\"id\":\"p2\"}"),
                Json("{\"id\":\"p3\"}"));
            var child = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var source = new SubstreamSource(parent, child);

            var messages = await Read(source, Catalog("project_line_items"));

            child.Slices.ShouldBe(new[] { "p1", "p2", "p3" });
            messages.Count(x => x.Type == MessageType.RECORD && x.Record!.Stream == "project_line_items").ShouldBe(3);
            messages.Any(x => x.Type == MessageType.RECORD && x.Record!.Stream == "projects").ShouldBeFalse();
            parent.Reads.ShouldBe(1);
        }

        [Fact]
        public async Task Test_Substream_ConfiguredParentIsReadOnlyOnceAndItsRecordsStillEmitted()
        {
            var parent = new RecordingStream("projects", Json("{\"id\":\"p1\"}"), Json("{\"id\":\"p2\"}"));
            var child = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var source = new SubstreamSource(parent, child);

            var messages = await Read(source, Catalog("projects"), Catalog("project_line_items"));

            parent.Reads.ShouldBe(1);
            messages.Count(x => x.Type == MessageType.RECORD && x.Record!.Stream == "projects").ShouldBe(2);
            child.Slices.ShouldBe(new[] { "p1", "p2" });
        }

        [Fact]
        public async Task Test_Substream_SharesOneParentReadBetweenTwoSubstreams()
        {
            var parent = new RecordingStream("projects", Json("{\"id\":\"p1\"}"));
            var first = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var second = new RecordingSubstream("project_invoices", new SubstreamDependency("projects", "id", "project_id"));
            var source = new SubstreamSource(parent, first, second);

            await Read(source, Catalog("project_line_items"), Catalog("project_invoices"));

            parent.Reads.ShouldBe(1);
            first.Slices.ShouldBe(new[] { "p1" });
            second.Slices.ShouldBe(new[] { "p1" });
        }

        [Fact]
        public async Task Test_Substream_SupportsNestedKeyPath()
        {
            var parent = new RecordingStream("projects", Json("{\"customer\":{\"id\":\"c1\"}}"));
            var child = new RecordingSubstream("customer_notes", new SubstreamDependency("projects", "customer.id", "customer_id"));
            var source = new SubstreamSource(parent, child);

            await Read(source, Catalog("customer_notes"));

            child.Slices.ShouldBe(new[] { "c1" });
        }

        [Fact]
        public async Task Test_Substream_SkipsParentRecordsWithoutTheKey()
        {
            var parent = new RecordingStream("projects", Json("{\"id\":\"p1\"}"), Json("{\"name\":\"no id\"}"),
                Json("{\"id\":\"p2\"}"));
            var child = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var source = new SubstreamSource(parent, child);

            var messages = await Read(source, Catalog("project_line_items"));

            child.Slices.ShouldBe(new[] { "p1", "p2" });
            messages.Count(x => x.Type == MessageType.RECORD).ShouldBe(2);
        }

        [Fact]
        public async Task Test_Substream_HonoursRecordLimitAcrossParents()
        {
            var parent = new RecordingStream("projects", Json("{\"id\":\"p1\"}"), Json("{\"id\":\"p2\"}"),
                Json("{\"id\":\"p3\"}"));
            var child = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var source = new SubstreamSource(parent, child);

            var messages = await Read(source, 2, Catalog("project_line_items"));

            child.Slices.ShouldBe(new[] { "p1", "p2" });
            messages.Count(x => x.Type == MessageType.RECORD).ShouldBe(2);
        }

        [Fact]
        public async Task Test_Substream_RejectsIncrementalSync()
        {
            var parent = new RecordingStream("projects", Json("{\"id\":\"p1\"}"));
            var child = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var source = new SubstreamSource(parent, child);

            var exception = await Should.ThrowAsync<Exception>(() =>
                Read(source, Catalog("project_line_items", SyncMode.incremental)));

            exception.Message.ShouldContain("only supports full_refresh");
        }

        [Fact]
        public async Task Test_Substream_RejectsNestedSubstreams()
        {
            var root = new RecordingStream("projects", Json("{\"id\":\"p1\"}"));
            var middle = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var leaf = new RecordingSubstream("line_item_notes", new SubstreamDependency("project_line_items", "id", "line_item_id"));
            var source = new SubstreamSource(root, middle, leaf);

            var exception = await Should.ThrowAsync<Exception>(() => Read(source, Catalog("line_item_notes")));

            exception.Message.ShouldContain("nested substreams are not supported");
        }

        [Fact]
        public async Task Test_Substream_RejectsUnknownParent()
        {
            var child = new RecordingSubstream("project_line_items", new SubstreamDependency("projects", "id", "project_id"));
            var source = new SubstreamSource(child);

            var exception = await Should.ThrowAsync<Exception>(() => Read(source, Catalog("project_line_items")));

            exception.Message.ShouldContain("projects");
            exception.Message.ShouldContain("project_line_items");
        }

        [Fact]
        public void Test_Substream_BuilderExposesDependency()
        {
            var dependency = new SubstreamDependency("projects", "id", "project_id");

            var stream = "https://api.example.com"
                .HttpStream()
                .Path((_, _, _) => "v1/project_line_items")
                .Dependency(dependency)
                .Create("project_line_items");

            stream.Dependency.ShouldNotBeNull();
            stream.Dependency!.ParentStream.ShouldBe("projects");
            stream.Dependency.ParentKeyField.ShouldBe("id");
            stream.Dependency.SliceParameter.ShouldBe("project_id");
        }

        [Fact]
        public void Test_Substream_BuilderDefaultsToNoDependency()
        {
            var stream = "https://api.example.com".HttpStream().Create("plain");

            stream.Dependency.ShouldBeNull();
        }

        private static async Task<List<AirbyteMessage>> Read(AbstractSource source, params ConfiguredAirbyteCatalog[] catalogs)
            => await Read(source, null, catalogs);

        private static async Task<List<AirbyteMessage>> Read(AbstractSource source, long? limit,
            params ConfiguredAirbyteCatalog[] catalogs)
        {
            var channel = Channel.CreateUnbounded<AirbyteMessage>();
            var merged = new ConfiguredAirbyteCatalog { Streams = catalogs.SelectMany(x => x.Streams).ToArray() };
            var config = limit.HasValue
                ? JsonSerializer.SerializeToElement(new Dictionary<string, object> { { "_limit", limit.Value } })
                : Json("{}");

            await source.Read(new AirbyteLogger(_ => { }), channel.Writer, config, merged, Json("{}"));
            channel.Writer.Complete();

            var messages = new List<AirbyteMessage>();
            await foreach (var message in channel.Reader.ReadAllAsync())
                messages.Add(message);
            return messages;
        }

        private static ConfiguredAirbyteCatalog Catalog(string name, SyncMode syncMode = SyncMode.full_refresh) =>
            new()
            {
                Streams = new[]
                {
                    new ConfiguredAirbyteStream
                    {
                        Stream = new AirbyteStream { Name = name },
                        SyncMode = syncMode,
                        DestinationSyncMode = DestinationSyncMode.append
                    }
                }
            };

        private sealed class SubstreamSource : AbstractSource
        {
            private readonly Stream[] _streams;

            public SubstreamSource(params Stream[] streams) => _streams = streams;

            public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
            {
                exc = null!;
                return true;
            }

            public override Stream[] Streams(JsonElement config) => _streams;
        }

        private sealed class RecordingStream : Stream
        {
            private readonly JsonElement[] _records;

            public RecordingStream(string name, params JsonElement[] records)
            {
                _givenName = name;
                _records = records;
            }

            public int Reads { get; private set; }

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                Reads++;
                foreach (var record in _records)
                    streamchannel.TryWrite(AbstractSource.AsAirbyteMessage(Name, record));

                return Task.FromResult((long)_records.Length);
            }
        }

        private sealed class RecordingSubstream : Stream
        {
            private readonly SubstreamDependency _dependency;

            public RecordingSubstream(string name, SubstreamDependency dependency)
            {
                _givenName = name;
                _dependency = dependency;
            }

            public override SubstreamDependency? Dependency => _dependency;

            public List<string> Slices { get; } = new();

            public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
                ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
                string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
            {
                var key = streamslice![_dependency.SliceParameter].ToString()!;
                Slices.Add(key);
                streamchannel.TryWrite(AbstractSource.AsAirbyteMessage(Name,
                    JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        { _dependency.SliceParameter, key }
                    })));

                return Task.FromResult(1L);
            }
        }
    }
}