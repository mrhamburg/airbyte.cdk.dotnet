using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using destination_opendal_parquet;
using destination_opendal_parquet.OpenDal;
using Parquet;
using Shouldly;
using Xunit;

namespace Destination.OpenDalParquet.Tests
{
    public class DestinationOptionsTests
    {
        private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        private static DestinationOptions Parse(string json,
            IReadOnlyDictionary<string, string>? environment = null, Func<string, string?>? readFile = null) =>
            DestinationOptions.FromJson(Json(json), environment ?? TestData.EmptyEnvironment, readFile ?? (_ => null));

        private static Func<string, string?> File(string path, string contents) =>
            requested => string.Equals(requested, path, StringComparison.Ordinal) ? contents : null;

        [Fact]
        public void Test_FromJson_ReadsOutputPath()
        {
            var options = Parse("""{"output_path":"/tmp/parquet-out"}""");

            options.OutputPrefix.ShouldBe("tmp/parquet-out");
        }

        [Fact]
        public void Test_FromJson_MissingOutputPath_Throws()
        {
            var error = Should.Throw<ArgumentException>(() => Parse("{}"));

            error.Message.ShouldContain("output_path");
        }

        [Fact]
        public void Test_FromJson_ReadsFlushRowLimit()
        {
            var options = Parse("""{"output_path":"/tmp/parquet-out","flush_row_limit":10}""");

            options.FlushRowLimit.ShouldBe(10);
        }

        [Fact]
        public void Test_FromJson_InvalidFlushRowLimit_Throws()
        {
            Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"/tmp/parquet-out","flush_row_limit":0}"""));
        }

        [Fact]
        public void Test_FromJson_DefaultServiceIsFsWithRoot()
        {
            var options = Parse("""{"output_path":"out"}""");

            options.OpenDal.Service.ShouldBe("fs");
            options.OpenDal.Options["root"].ShouldBe("/");
        }

        [Fact]
        public void Test_FromJson_ReadsInlineServiceAndOptions()
        {
            var options = Parse(
                """{"output_path":"out","service":"s3","options":{"bucket":"b","region":"us-east-1"}}""");

            options.OpenDal.Service.ShouldBe("s3");
            options.OpenDal.Options["bucket"].ShouldBe("b");
            options.OpenDal.Options["region"].ShouldBe("us-east-1");
            options.OpenDal.Options.ShouldNotContainKey("root");
        }

        [Fact]
        public void Test_FromJson_SettingsFileProvidesServiceOptionsAndParquetSettings()
        {
            const string settings = """
                {
                  "service": "s3",
                  "options": { "bucket": "file", "region": "eu" },
                  "parquet": { "compression": "zstd", "row_group_size": 1234 },
                  "flush_row_limit": 7,
                  "flush_on_state": false,
                  "file_name_template": "x-{sequence}.parquet"
                }
                """;
            var options = Parse("""{"output_path":"out","settings_file":"/settings.json"}""",
                readFile: File("/settings.json", settings));

            options.OpenDal.Service.ShouldBe("s3");
            options.OpenDal.Options["bucket"].ShouldBe("file");
            options.OpenDal.Options["region"].ShouldBe("eu");
            options.FlushRowLimit.ShouldBe(7);
            options.FlushOnState.ShouldBeFalse();
            options.FileNameTemplate.ShouldBe("x-{sequence}.parquet");
            options.Parquet.Compression.ShouldBe(CompressionMethod.Zstd);
            options.Parquet.RowGroupSize.ShouldBe(1234);
        }

        [Fact]
        public void Test_FromJson_EnvironmentOverridesSettingsFile()
        {
            const string settings = """{"service":"fs","options":{"region":"file"}}""";
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENDAL_SETTINGS"] = "/settings.json",
                ["OPENDAL_SERVICE"] = "s3",
                ["OPENDAL_OPTIONS"] = """{"region":"eu"}""",
                ["OPENDAL_OPTION_ENDPOINT"] = "https://generic",
                ["OPENDAL_S3_BUCKET"] = "native",
                ["OPENDAL_S3_ENDPOINT"] = "https://native"
            };

            var options = Parse("""{"output_path":"out"}""", environment,
                File("/settings.json", settings));

            options.OpenDal.Service.ShouldBe("s3");
            options.OpenDal.Options["region"].ShouldBe("eu");
            options.OpenDal.Options["bucket"].ShouldBe("native");
            options.OpenDal.Options["endpoint"].ShouldBe("https://native");
            options.OpenDal.Options.ShouldNotContainKey("root");
        }

        [Fact]
        public void Test_FromJson_InlineOverridesEnvironment()
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENDAL_SERVICE"] = "s3",
                ["OPENDAL_S3_BUCKET"] = "env"
            };

            var options = Parse(
                """{"output_path":"out","service":"gcs","options":{"bucket":"inline"}}""", environment);

            options.OpenDal.Service.ShouldBe("gcs");
            options.OpenDal.Options["bucket"].ShouldBe("inline");
        }

        [Fact]
        public void Test_FromJson_NullOptionRemovesInheritedValue()
        {
            const string settings = """{"service":"s3","options":{"bucket":"file","region":"eu"}}""";
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENDAL_OPTIONS"] = """{"region":null}"""
            };

            var options = Parse("""{"output_path":"out","settings_file":"/settings.json"}""",
                environment, File("/settings.json", settings));

            options.OpenDal.Options["bucket"].ShouldBe("file");
            options.OpenDal.Options.ShouldNotContainKey("region");
        }

        [Fact]
        public void Test_FromJson_NumericAndBooleanOptionsBecomeStrings()
        {
            var options = Parse(
                """{"output_path":"out","service":"memory","options":{"threads":4,"enabled":true,"missing":null}}""");

            options.OpenDal.Options["threads"].ShouldBe("4");
            options.OpenDal.Options["enabled"].ShouldBe("true");
            options.OpenDal.Options.ShouldNotContainKey("missing");
        }

        [Fact]
        public void Test_FromJson_MissingSettingsFile_Throws()
        {
            var error = Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"out","settings_file":"/missing.json"}"""));

            error.Message.ShouldContain("not found");
        }

        [Fact]
        public void Test_FromJson_UnknownSettingsProperty_Throws()
        {
            var error = Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"out","settings_file":"/settings.json"}""",
                    readFile: File("/settings.json", """{"service":"fs","unknown":1}""")));

            error.Message.ShouldContain("unknown property");
        }

        [Fact]
        public void Test_FromJson_InvalidCompression_Throws()
        {
            var error = Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"out","parquet":{"compression":"bogus"}}"""));

            error.Message.ShouldContain("compression");
        }

        [Fact]
        public void Test_FromJson_InvalidFileNameTemplate_Throws()
        {
            Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"out","file_name_template":"dir/file.parquet"}"""));
            Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"out","file_name_template":"file.txt"}"""));
            Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"out","file_name_template":"file-{bad}.parquet"}"""));
            Should.Throw<ArgumentException>(() =>
                Parse("""{"output_path":"out","file_name_template":"part-{timestamp}.parquet"}"""));
        }

        [Fact]
        public void Test_FromJson_ReadsFlushOnState()
        {
            var options = Parse("""{"output_path":"out","flush_on_state":false}""");

            options.FlushOnState.ShouldBeFalse();
        }

        [Fact]
        public void Test_StreamPrefix_SanitizesTraversal()
        {
            var options = Parse("""{"output_path":"/tmp/parquet-out"}""");

            var escape = options.StreamPrefix(null, "../escape");
            var dotdot = options.StreamPrefix(null, "..");

            escape.ShouldStartWith(options.OutputPrefix + "/");
            escape.Split('/').Last().ShouldNotBe("..");
            dotdot.Split('/').Last().ShouldNotBe("..");
        }

        [Fact]
        public void Test_StreamPrefix_DoesNotCollideForEscapedNames()
        {
            var options = Parse("""{"output_path":"/tmp/parquet-out"}""");

            options.StreamPrefix(null, "a/b").ShouldNotBe(options.StreamPrefix(null, "a_b"));
        }

        [Fact]
        public void Test_FileName_ExpandsTemplate()
        {
            var options = Parse("""{"output_path":"out","file_name_template":"x-{sequence}-{stream}.parquet"}""");

            var name = options.FileName(null, "latest", 3, new DateTime(2026, 11, 3, 1, 2, 3, 4, DateTimeKind.Utc));

            name.ShouldBe("x-0003-latest.parquet");
        }

        [Fact]
        public void Test_Check_ReturnsSucceeded()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-check-" + Guid.NewGuid().ToString("N"));
            var connector = TestData.Connector(new InMemoryObjectStore());

            var status = connector.Check(new Airbyte.Cdk.AirbyteLogger(_ => { }),
                Json($$"""{"output_path":"{{root}}"}"""));

            status.Status.ShouldBe(Airbyte.Cdk.Models.Status.SUCCEEDED);
        }

        [Fact]
        public void Test_Check_FailsWhenStoreFails()
        {
            var connector = new global::destination_opendal_parquet.OpenDalParquet
            {
                OptionsFactory = TestData.OptionsFromJson,
                StoreFactory = _ => throw new IOException("output_path is a file")
            };

            var status = connector.Check(new Airbyte.Cdk.AirbyteLogger(_ => { }),
                Json("""{"output_path":"/tmp/parquet-out"}"""));

            status.Status.ShouldBe(Airbyte.Cdk.Models.Status.FAILED);
            (status.Message ?? string.Empty).ShouldContain("output_path");
        }
    }
}
