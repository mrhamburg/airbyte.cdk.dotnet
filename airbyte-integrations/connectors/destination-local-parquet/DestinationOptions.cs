using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace destination_local_parquet
{
    internal sealed class DestinationOptions
    {
        private const string ProbeFileName = ".airbyte-write-test";
        private const int DefaultFlushRowLimit = 50_000;

        private DestinationOptions(string outputPath, int flushRowLimit)
        {
            OutputPath = outputPath;
            FlushRowLimit = flushRowLimit;
        }

        public string OutputPath { get; }

        public int FlushRowLimit { get; }

        public static DestinationOptions FromJson(JsonElement config)
        {
            if (config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("output_path", out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ArgumentException("Config property 'output_path' is required.");

            var flushRowLimit = DefaultFlushRowLimit;
            if (config.TryGetProperty("flush_row_limit", out var limit))
            {
                if (limit.ValueKind != JsonValueKind.Number
                    || !limit.TryGetInt32(out flushRowLimit)
                    || flushRowLimit < 1)
                    throw new ArgumentException("Config property 'flush_row_limit' must be a positive integer.");
            }

            return new DestinationOptions(Path.GetFullPath(value.GetString()!), flushRowLimit);
        }

        public void EnsureReady()
        {
            if (File.Exists(OutputPath))
                throw new IOException($"output_path '{OutputPath}' is a file, not a directory.");

            Directory.CreateDirectory(OutputPath);
            var probe = Path.Combine(OutputPath, $"{ProbeFileName}-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllText(probe, "airbyte");
            }
            finally
            {
                if (File.Exists(probe))
                    File.Delete(probe);
            }
        }

        public string StreamDirectory(string? ns, string stream)
        {
            var safeStream = Sanitize(stream);
            return string.IsNullOrWhiteSpace(ns)
                ? Path.Combine(OutputPath, safeStream)
                : Path.Combine(OutputPath, Sanitize(ns!), safeStream);
        }

        private static string Sanitize(string value)
        {
            var result = Uri.EscapeDataString(value).Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(result) || result == "." || result == ".." ? "stream" : result;
        }
    }
}
