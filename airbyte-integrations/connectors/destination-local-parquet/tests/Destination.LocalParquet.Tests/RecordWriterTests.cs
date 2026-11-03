using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Models;
using destination_local_parquet.Parquet;
using Parquet;
using Shouldly;
using Xunit;
using static Destination.LocalParquet.Tests.TestData;

namespace Destination.LocalParquet.Tests
{
    public class RecordWriterTests
    {
        [Fact]
        public async Task Test_WriteAsync_StateFlushesAndWritesOneFile()
        {
            using var dir = new TempDirectory();
            var writer = new ParquetRecordWriter(Options(dir.Path),
                Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").ShouldHaveSingleItem();
        }

        [Fact]
        public async Task Test_WriteAsync_OverwriteDeletesExistingParquetFiles()
        {
            using var dir = new TempDirectory();
            var streamDir = Path.Combine(dir.Path, "latest");
            Directory.CreateDirectory(streamDir);
            var stale = Path.Combine(streamDir, "stale.parquet");
            File.WriteAllText(stale, "old");
            var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            File.Exists(stale).ShouldBeFalse();
        }

        [Fact]
        public async Task Test_WriteAsync_AppendKeepsExistingParquetFiles()
        {
            using var dir = new TempDirectory();
            var streamDir = Path.Combine(dir.Path, "latest");
            Directory.CreateDirectory(streamDir);
            var stale = Path.Combine(streamDir, "stale.parquet");
            File.WriteAllText(stale, "old");
            var writer = new ParquetRecordWriter(Options(dir.Path),
                Catalog("latest", TestData.Schema, DestinationSyncMode.append), new AirbyteLogger(_ => { }));

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            File.Exists(stale).ShouldBeTrue();
        }

        [Fact]
        public async Task Test_WriteAsync_DoesNotLeaveTmpFile()
        {
            using var dir = new TempDirectory();
            var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            Directory.GetFiles(dir.Path, "*.tmp", SearchOption.AllDirectories).ShouldBeEmpty();
        }

        [Fact]
        public async Task Test_WriteAsync_WritesStreamMetadata()
        {
            using var dir = new TempDirectory();
            var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            var path = Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").Single();
            await using var stream = File.OpenRead(path);
            await using var reader = await ParquetReader.CreateAsync(stream);
            reader.CustomMetadata["airbyte.stream"].ShouldBe("latest");
        }

        [Fact]
        public async Task Test_WriteAsync_FlushesAtRowLimit()
        {
            using var dir = new TempDirectory();
            var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema),
                new AirbyteLogger(_ => { }), flushRowLimit: 1);

            await writer.WriteAsync(Record("latest", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));

            Directory.GetFiles(Path.Combine(dir.Path, "latest"), "*.parquet").ShouldHaveSingleItem();
        }

        [Fact]
        public async Task Test_WriteAsync_UnknownStreamThrows()
        {
            using var dir = new TempDirectory();
            var writer = new ParquetRecordWriter(Options(dir.Path), Catalog("latest", TestData.Schema), new AirbyteLogger(_ => { }));

            await Should.ThrowAsync<InvalidOperationException>(() =>
                writer.WriteAsync(Record("other", """{"a":1}""")));
        }

        [Fact]
        public async Task Test_WriteAsync_StreamNameWithTraversalStaysInsideOutputPath()
        {
            using var dir = new TempDirectory();
            var writer = new ParquetRecordWriter(Options(dir.Path),
                Catalog("../escape", TestData.Schema), new AirbyteLogger(_ => { }));

            await writer.WriteAsync(Record("../escape", """{"amount":1,"base":"USD","date":"2026-11-01","rates":{}}"""));
            await writer.WriteAsync(State());

            Directory.GetDirectories(dir.Path).ShouldHaveSingleItem();
            Path.GetFileName(Directory.GetDirectories(dir.Path).Single()).ShouldBe(".._escape");
        }
    }
}
