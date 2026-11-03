using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace destination_local_parquet
{
    internal sealed class DestinationOptions
    {
        private const string ProbeFileName = ".airbyte-write-test";

        private DestinationOptions(string outputPath) => OutputPath = outputPath;

        public string OutputPath { get; }

        public static DestinationOptions FromJson(JsonElement config)
        {
            if (config.ValueKind != JsonValueKind.Object
                || !config.TryGetProperty("output_path", out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ArgumentException("Config property 'output_path' is required.");

            return new DestinationOptions(Path.GetFullPath(value.GetString()!));
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
            var characters = value.Replace('/', '_').Replace('\\', '_').ToArray();
            foreach (var invalid in Path.GetInvalidFileNameChars())
                for (var index = 0; index < characters.Length; index++)
                    if (characters[index] == invalid)
                        characters[index] = '_';

            var result = new string(characters).Trim();
            return string.IsNullOrWhiteSpace(result) || result == ".." || result == "." ? "stream" : result;
        }
    }
}
