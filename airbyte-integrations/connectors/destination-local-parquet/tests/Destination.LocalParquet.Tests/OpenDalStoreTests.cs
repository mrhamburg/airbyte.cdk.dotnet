using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using destination_local_parquet;
using destination_local_parquet.OpenDal;
using Shouldly;
using Xunit;
using static Destination.LocalParquet.Tests.TestData;

namespace Destination.LocalParquet.Tests
{
    public class OpenDalStoreTests
    {
        private static OpenDalStore Store(DestinationOptions options) =>
            new(options.OpenDal, options.OutputPrefix);

        [OpenDalAvailableFact]
        public async Task Test_EnsureReadyAsync_WritesReadsListsAndDeletesProbe()
        {
            using var dir = new TempDirectory();
            var options = Options(dir.Path);
            using var store = Store(options);

            await store.EnsureReadyAsync();

            Directory.Exists(dir.Path).ShouldBeTrue();
            Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories).ShouldBeEmpty();
        }

        [OpenDalAvailableFact]
        public async Task Test_WriteReadDelete_RoundTripsBytes()
        {
            using var dir = new TempDirectory();
            var options = Options(dir.Path);
            using var store = Store(options);
            var key = options.KeyFor(options.StreamPrefix(null, "latest"), "test.parquet");
            var payload = Encoding.UTF8.GetBytes("parquet-bytes");

            await store.WriteAsync(key, payload);
            (await store.ReadAsync(key)).ShouldBe(payload);

            await store.DeleteAsync(key);
            (await store.ListFilesAsync(options.StreamPrefix(null, "latest"))).ShouldBeEmpty();
        }

        [OpenDalAvailableFact]
        public async Task Test_ListFilesAsync_ReturnsOnlyFilesUnderPrefix()
        {
            using var dir = new TempDirectory();
            var options = Options(dir.Path);
            using var store = Store(options);
            var prefix = options.StreamPrefix(null, "latest");
            await store.WriteAsync(options.KeyFor(prefix, "a.parquet"), new byte[] { 1 });
            await store.WriteAsync(options.KeyFor(prefix, "nested/b.parquet"), new byte[] { 2 });
            await store.WriteAsync(options.KeyFor(options.StreamPrefix(null, "other"), "c.parquet"),
                new byte[] { 3 });

            var files = await store.ListFilesAsync(prefix);

            files.Count.ShouldBe(2);
            files.ShouldContain(options.KeyFor(prefix, "a.parquet"));
            files.ShouldContain(options.KeyFor(prefix, "nested/b.parquet"));
        }

        [OpenDalAvailableFact]
        public async Task Test_ListFilesAsync_MissingPrefixReturnsEmpty()
        {
            using var dir = new TempDirectory();
            var options = Options(dir.Path);
            using var store = Store(options);

            var files = await store.ListFilesAsync(options.StreamPrefix(null, "missing"));

            files.ShouldBeEmpty();
        }

        [OpenDalAvailableFact]
        public async Task Test_WriteAsync_CreatesMissingParentDirectories()
        {
            using var dir = new TempDirectory();
            var options = Options(dir.Path);
            using var store = Store(options);
            var key = options.KeyFor(options.StreamPrefix("ns", "latest"), "nested/deep/test.parquet");
            var payload = Encoding.UTF8.GetBytes("bytes");

            await store.WriteAsync(key, payload);

            (await store.ReadAsync(key)).ShouldBe(payload);
            Directory.GetFiles(dir.Path, "test.parquet", SearchOption.AllDirectories).ShouldHaveSingleItem();
        }
    }
}
