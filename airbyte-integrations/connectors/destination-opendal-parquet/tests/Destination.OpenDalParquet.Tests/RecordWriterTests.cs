using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Models;
using destination_opendal_parquet;
using destination_opendal_parquet.Parquet;
using Parquet;
using Shouldly;
using Xunit;
using static Destination.OpenDalParquet.Tests.TestData;

namespace Destination.OpenDalParquet.Tests
{
    public class RecordWriterTests
    {
        private static ParquetRecordWriter Writer(DestinationOptions options, ConfiguredAirbyteCatalog catalog,
            InMemoryObjectStore store) =>
            new(options, catalog, new AirbyteLogger(_ => { }), store);

        [Fact]
        public async Task Test_WriteAsync_StateFlushesAndWritesOneObject()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path);
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            store.Keys.ShouldHaveSingleItem();
            store.Keys[0].ShouldStartWith(options.StreamPrefix(null, "latest") + "/");
        }

        [Fact]
        public async Task Test_WriteAsync_OverwriteDeletesExistingParquetFiles()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path);
            var stale = options.StreamPrefix(null, "latest") + "/stale.parquet";
            await store.WriteAsync(stale, new byte[] { 1 });
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());
            store.Objects.ShouldContainKey(stale);

            await writer.CompleteAsync();

            store.Objects.ShouldNotContainKey(stale);
        }

        [Fact]
        public async Task Test_CompleteAsync_EmptyOverwriteDeletesExistingParquetFiles()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path);
            var stale = options.StreamPrefix(null, "latest") + "/stale.parquet";
            await store.WriteAsync(stale, new byte[] { 1 });
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.CompleteAsync();

            store.Objects.ShouldNotContainKey(stale);
        }

        [Fact]
        public async Task Test_WriteAsync_AppendKeepsExistingParquetFiles()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path);
            var stale = options.StreamPrefix(null, "latest") + "/stale.parquet";
            await store.WriteAsync(stale, new byte[] { 1 });
            var writer = Writer(options, Catalog("latest", Schema, DestinationSyncMode.append), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());
            await writer.CompleteAsync();

            store.Objects.ShouldContainKey(stale);
        }

        [Fact]
        public async Task Test_CompleteAsync_RemovesOrphanedTemporaryObject()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path);
            var orphan = options.StreamPrefix(null, "latest") + "/orphan.parquet.tmp";
            await store.WriteAsync(orphan, new byte[] { 1 });
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());
            await writer.CompleteAsync();

            store.Objects.ShouldNotContainKey(orphan);
        }

        [Fact]
        public async Task Test_WriteAsync_WritesStreamMetadata()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path);
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            var bytes = await store.ReadAsync(store.Keys.Single());
            await using var stream = new MemoryStream(bytes);
            await using var reader = await ParquetReader.CreateAsync(stream);
            reader.CustomMetadata["airbyte.stream"].ShouldBe("latest");
            reader.CustomMetadata["airbyte.opendal.service"].ShouldBe("fs");
        }

        [Fact]
        public async Task Test_WriteAsync_FlushesAtRowLimit()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path, flushRowLimit: 1);
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));

            store.Keys.ShouldHaveSingleItem();
        }

        [Fact]
        public async Task Test_WriteAsync_DoesNotFlushOnStateWhenDisabled()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path, flushOnState: false);
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            store.Keys.ShouldBeEmpty();

            await writer.CompleteAsync();

            store.Keys.ShouldHaveSingleItem();
        }

        [Fact]
        public async Task Test_WriteAsync_UsesConfiguredFileNameTemplate()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path, fileNameTemplate: "data-{sequence}.parquet");
            var writer = Writer(options, Catalog("latest", Schema), store);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            store.Keys.Single().ShouldEndWith("/data-0000.parquet");
        }

        [Fact]
        public async Task Test_WriteAsync_UnknownStreamThrows()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var writer = Writer(Options(dir.Path), Catalog("latest", Schema), store);

            await Should.ThrowAsync<InvalidOperationException>(() =>
                writer.WriteAsync(Record("other", """{"a":1}""")));
        }

        [Fact]
        public async Task Test_WriteAsync_StreamNameWithTraversalStaysInsideOutputPath()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();
            var options = Options(dir.Path);
            var writer = Writer(options, Catalog("../escape", Schema), store);

            await writer.WriteAsync(Record("../escape", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            store.Keys.ShouldNotBeEmpty();
            foreach (var key in store.Keys)
            {
                key.ShouldStartWith(options.OutputPrefix + "/");
                key.Split('/').ShouldNotContain("..");
            }
        }

        [Fact]
        public async Task Test_WriteAsync_UnsupportedSyncModeThrows()
        {
            using var dir = new TempDirectory();
            var store = new InMemoryObjectStore();

            var error = Should.Throw<NotSupportedException>(() =>
                Writer(Options(dir.Path), Catalog("latest", Schema, DestinationSyncMode.append_dedup), store));

            error.Message.ShouldContain("append_dedup");
        }

    }
}
