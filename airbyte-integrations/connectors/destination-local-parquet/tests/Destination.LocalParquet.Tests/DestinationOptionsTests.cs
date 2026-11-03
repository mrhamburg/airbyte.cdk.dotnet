using System;
using System.IO;
using System.Text.Json;
using destination_local_parquet;
using Shouldly;
using Xunit;

namespace Destination.LocalParquet.Tests
{
    public class DestinationOptionsTests
    {
        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        [Fact]
        public void Test_FromJson_ReadsOutputPath()
        {
            var options = DestinationOptions.FromJson(Json("""{"output_path":"/tmp/parquet-out"}"""));

            options.OutputPath.ShouldBe(Path.GetFullPath("/tmp/parquet-out"));
        }

        [Fact]
        public void Test_FromJson_MissingOutputPath_Throws()
        {
            var error = Should.Throw<ArgumentException>(() => DestinationOptions.FromJson(Json("{}")));

            error.Message.ShouldContain("output_path");
        }

        [Fact]
        public void Test_EnsureReady_CreatesDirectoryAndRemovesProbe()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            var options = DestinationOptions.FromJson(Json($$"""{"output_path":"{{root}}"}"""));

            options.EnsureReady();

            Directory.Exists(root).ShouldBeTrue();
            Directory.GetFiles(root).ShouldBeEmpty();
        }

        [Fact]
        public void Test_FromJson_ReadsFlushRowLimit()
        {
            var options = DestinationOptions.FromJson(Json("""{"output_path":"/tmp/parquet-out","flush_row_limit":10}"""));

            options.FlushRowLimit.ShouldBe(10);
        }

        [Fact]
        public void Test_FromJson_InvalidFlushRowLimit_Throws()
        {
            Should.Throw<ArgumentException>(() =>
                DestinationOptions.FromJson(Json("""{"output_path":"/tmp/parquet-out","flush_row_limit":0}""")));
        }

        [Fact]
        public void Test_Check_ReturnsSucceeded()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            var connector = new global::destination_local_parquet.LocalParquet();

            var status = connector.Check(new Airbyte.Cdk.AirbyteLogger(m => { }),
                Json($$"""{"output_path":"{{root}}"}"""));

            status.Status.ShouldBe(Airbyte.Cdk.Models.Status.SUCCEEDED);
        }

        [Fact]
        public void Test_Check_FailsWhenOutputPathIsAFile()
        {
            var file = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(file, "x");
            var connector = new global::destination_local_parquet.LocalParquet();

            var status = connector.Check(new Airbyte.Cdk.AirbyteLogger(m => { }),
                Json($$"""{"output_path":"{{file}}"}"""));

            status.Status.ShouldBe(Airbyte.Cdk.Models.Status.FAILED);
            status.Message.ShouldNotBeNullOrWhiteSpace();
        }

        [Fact]
        public void Test_StreamDirectory_SanitizesTraversal()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            var options = DestinationOptions.FromJson(Json($$"""{"output_path":"{{root}}"}"""));

            var escape = options.StreamDirectory(null, "../escape");
            var dotdot = options.StreamDirectory(null, "../..");

            Path.GetFullPath(escape).ShouldStartWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            Path.GetFullPath(dotdot).ShouldStartWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar);
            Path.GetFileName(dotdot).ShouldNotBe("..");
        }

        [Fact]
        public void Test_StreamDirectory_DoesNotCollideForEscapedNames()
        {
            var options = DestinationOptions.FromJson(Json("""{"output_path":"/tmp/parquet-out"}"""));

            options.StreamDirectory(null, "a/b").ShouldNotBe(options.StreamDirectory(null, "a_b"));
        }
    }
}
