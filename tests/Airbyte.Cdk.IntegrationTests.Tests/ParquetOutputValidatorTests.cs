using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Parquet.Serialization;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class ParquetOutputValidatorTests
    {
        private static JsonElement Discovered(string schema) =>
            JsonDocument.Parse($$"""{"streams":[{"name":"latest","json_schema":{{schema}}}]}""")
                .RootElement.Clone();

        private static async Task WriteFile(string directory, string stream, int rows)
        {
            Directory.CreateDirectory(Path.Combine(directory, stream));
            var schema = new ParquetSchema(
                new DataField("amount", typeof(double), isNullable: false),
                new DataField("base", typeof(string), isNullable: false));
            var data = Enumerable.Range(0, rows)
                .Select(_ => (IDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["amount"] = 1.0d,
                    ["base"] = "USD"
                })
                .ToList();
            await using var file = File.Create(Path.Combine(directory, stream, "part.parquet"));
            await ParquetSerializer.SerializeUntypedAsync(data, schema, file);
        }

        [Fact]
        public async Task Test_Validate_CountsRows()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await WriteFile(root, "latest", 3);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest", MinRecords: 1),
                Discovered("""{"type":"object","properties":{"amount":{"type":"number"},"base":{"type":"string"}},"required":["amount","base"]}"""));

            result.Status.ShouldBe(Status.Passed);
            result.RowCount.ShouldBe(3);
            result.FileCount.ShouldBe(1);
        }

        [Fact]
        public async Task Test_Validate_RowCountBelowMinFails()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await WriteFile(root, "latest", 1);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest", MinRecords: 2),
                Discovered("""{"type":"object","properties":{"amount":{"type":"number"},"base":{"type":"string"}},"required":["amount","base"]}"""));

            result.Status.ShouldBe(Status.Failed);
            result.Errors.ShouldNotBeEmpty();
        }

        [Fact]
        public void Test_Validate_MissingDirectoryFails()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest"),
                Discovered("""{"type":"object","properties":{}}"""));

            result.Status.ShouldBe(Status.Failed);
        }

        [Fact]
        public async Task Test_Validate_SeededStaleFileIsIgnored()
        {
            var root = Path.Combine(Path.GetTempPath(), "parquet-validate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "latest"));
            File.WriteAllText(Path.Combine(root, "latest", "stale.parquet"), "old");
            await WriteFile(root, "latest", 1);

            var result = ParquetOutputValidator.Validate(root,
                new StreamSpec("latest", MinRecords: 1),
                Discovered("""{"type":"object","properties":{"amount":{"type":"number"},"base":{"type":"string"}},"required":["amount","base"]}"""));

            result.Status.ShouldBe(Status.Passed);
            result.FileCount.ShouldBe(1);
        }
    }
}
