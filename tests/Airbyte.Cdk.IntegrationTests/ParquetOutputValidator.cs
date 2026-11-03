#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Parquet;
using Parquet.Schema;
using Parquet.Serialization;

namespace Airbyte.Cdk.IntegrationTests
{
    public sealed record ParquetStreamResult(
        string Name,
        Status Status,
        int FileCount,
        long RowCount,
        IReadOnlyList<string> Errors);

    /// <summary>
    /// Validates the Parquet files a destination wrote for one configured stream against the
    /// stream's discovered JSON schema.
    /// </summary>
    public static class ParquetOutputValidator
    {
        private const string StaleFileName = "stale.parquet";
        private const int SampleRows = 100;

        public static ParquetStreamResult Validate(string parquetRoot, StreamSpec spec, JsonElement discoveredSchema)
        {
            var directory = Path.Combine(parquetRoot, spec.Name);
            if (!Directory.Exists(directory))
                return Failed(spec.Name, 0, 0,
                    $"stream directory not found: {directory}");

            var temporaryFiles = Directory.GetFiles(directory, "*.parquet.tmp");
            if (temporaryFiles.Length > 0)
                return Failed(spec.Name, 0, 0,
                    $"stream '{spec.Name}' left temporary parquet file(s): {string.Join(", ", temporaryFiles.Select(Path.GetFileName))}");

            var files = Directory.GetFiles(directory, "*.parquet")
                .Where(x => !string.Equals(Path.GetFileName(x), StaleFileName, StringComparison.Ordinal))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            if (files.Count == 0)
                return Failed(spec.Name, 0, 0, $"stream '{spec.Name}' wrote no parquet files.");

            var jsonSchema = FindJsonSchema(discoveredSchema, spec.Name);
            if (jsonSchema == null)
                return Failed(spec.Name, files.Count, 0,
                    $"discover did not report a json_schema for stream '{spec.Name}'.");

            var shape = RootShapeOf(jsonSchema.Value);
            var errors = new List<string>();
            long totalRows = 0;

            foreach (var file in files)
                totalRows += ValidateFile(file, shape, errors);

            if (totalRows < spec.MinRecords)
                errors.Add($"stream '{spec.Name}' wrote {totalRows} row(s) but needs at least {spec.MinRecords}.");

            ValidateSample(files[0], shape, errors);

            return new ParquetStreamResult(spec.Name,
                errors.Count == 0 ? Status.Passed : Status.Failed, files.Count, totalRows, errors);
        }

        private static long ValidateFile(string file, RootShape shape, List<string> errors)
        {
            try
            {
                var reader = ParquetReader.CreateAsync(file).GetAwaiter().GetResult();
                try
                {
                    var fields = reader.Schema.Fields;
                    ValidateSchema(Path.GetFileName(file), fields, shape, errors);
                    return reader.Metadata?.RowGroups?.Sum(x => (long)x.NumRows) ?? 0;
                }
                finally
                {
                    reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception e)
            {
                errors.Add($"{Path.GetFileName(file)} could not be read as parquet: {e.Message}");
                return 0;
            }
        }

        private static void ValidateSchema(string fileName, IReadOnlyList<Field> fields, RootShape shape,
            List<string> errors)
        {
            if (shape.Properties != null)
            {
                var names = fields.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var property in shape.Properties)
                    if (!names.Contains(property))
                        errors.Add($"{fileName} has no field for discovered property '{property}'.");

                return;
            }

            if (shape.IsMap)
            {
                if (fields.Count != 1
                    || !string.Equals(fields[0].Name, "value", StringComparison.Ordinal)
                    || fields[0].SchemaType != SchemaType.Map)
                    errors.Add($"{fileName} must contain one map field named 'value'.");
                return;
            }

            if (fields.Count != 1 || !string.Equals(fields[0].Name, "value", StringComparison.Ordinal))
                errors.Add($"{fileName} must contain one field named 'value'.");
        }

        private static void ValidateSample(string file, RootShape shape, List<string> errors)
        {
            DeserializationResult<Dictionary<string, object>> result;
            try
            {
                using var stream = File.OpenRead(file);
                result = ParquetSerializer.DeserializeUntypedAsync(stream).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                errors.Add($"{Path.GetFileName(file)} could not be sampled: {e.Message}");
                return;
            }

            var rows = result.Data.Take(SampleRows).ToList();

            if (shape.IsMap)
            {
                if (!rows.Any(x => x.TryGetValue("value", out var value) && value != null))
                    errors.Add($"{file} has no row with a non-null 'value' entry.");
                return;
            }

            foreach (var property in shape.Required)
                if (!rows.Any(x => x.TryGetValue(property, out var value) && value != null))
                    errors.Add($"{file} has no row with a non-null '{property}' value.");
        }

        private static RootShape RootShapeOf(JsonElement schema)
        {
            if (schema.TryGetProperty("properties", out var properties)
                && properties.ValueKind == JsonValueKind.Object)
                return new RootShape
                {
                    Properties = properties.EnumerateObject().Select(x => x.Name).ToList(),
                    Required = RequiredNames(schema)
                };

            if (IsMap(schema))
                return new RootShape { IsMap = true };

            return new RootShape();
        }

        private static bool IsMap(JsonElement schema)
        {
            if (!schema.TryGetProperty("additionalProperties", out var additional))
                return false;

            return additional.ValueKind is JsonValueKind.Object or JsonValueKind.True;
        }

        private static HashSet<string> RequiredNames(JsonElement schema)
        {
            if (!schema.TryGetProperty("required", out var required)
                || required.ValueKind != JsonValueKind.Array)
                return new HashSet<string>(StringComparer.Ordinal);

            return required.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static JsonElement? FindJsonSchema(JsonElement discoveredCatalog, string streamName)
        {
            if (!discoveredCatalog.TryGetProperty("streams", out var streams)
                || streams.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("name", out var name)
                    || !string.Equals(name.GetString(), streamName, StringComparison.Ordinal))
                    continue;

                return stream.TryGetProperty("json_schema", out var schema) ? schema : null;
            }

            return null;
        }

        private static ParquetStreamResult Failed(string name, int fileCount, long rowCount, string error) =>
            new(name, Status.Failed, fileCount, rowCount, new[] { error });

        private sealed class RootShape
        {
            public IReadOnlyList<string>? Properties { get; init; }
            public HashSet<string> Required { get; init; } = new(StringComparer.Ordinal);
            public bool IsMap { get; init; }
        }
    }
}
