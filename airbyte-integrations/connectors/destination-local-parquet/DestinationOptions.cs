using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using destination_local_parquet.OpenDal;

namespace destination_local_parquet
{
    internal sealed class DestinationOptions
    {
        private const int DefaultFlushRowLimit = 50_000;
        private const string DefaultFileNameTemplate = "part-{timestamp}-{sequence}.parquet";

        private static readonly HashSet<string> AllowedSettingsKeys = new(StringComparer.Ordinal)
        {
            "service",
            "scheme",
            "options",
            "parquet",
            "flush_row_limit",
            "flush_on_state",
            "file_name_template"
        };

        private static readonly HashSet<string> FileNamePlaceholders = new(StringComparer.Ordinal)
        {
            "timestamp",
            "sequence",
            "uuid",
            "stream",
            "namespace"
        };

        private DestinationOptions(string outputPrefix, int flushRowLimit, bool flushOnState,
            string fileNameTemplate, OpenDalConfig openDal, ParquetSettings parquet)
        {
            OutputPrefix = outputPrefix;
            FlushRowLimit = flushRowLimit;
            FlushOnState = flushOnState;
            FileNameTemplate = fileNameTemplate;
            OpenDal = openDal;
            Parquet = parquet;
        }

        public string OutputPrefix { get; }

        public int FlushRowLimit { get; }

        public bool FlushOnState { get; }

        public string FileNameTemplate { get; }

        public OpenDalConfig OpenDal { get; }

        public ParquetSettings Parquet { get; }

        public static DestinationOptions FromJson(JsonElement config) =>
            FromJson(config, EnvironmentVariables(), ReadFile);

        internal static DestinationOptions FromJson(JsonElement config,
            IReadOnlyDictionary<string, string> environment, Func<string, string?> readFile)
        {
            var settings = LoadSettings(config, environment, readFile);
            var openDal = OpenDalConfig.Resolve(config, settings, environment);
            var parquet = ParquetSettings.Resolve(config, settings);

            var outputPath = ConfigJson.GetString(config, "output_path");
            if (string.IsNullOrWhiteSpace(outputPath))
                throw new ArgumentException("Config property 'output_path' is required.");

            var outputPrefix = ObjectKey.NormalizePrefix(outputPath);
            if (outputPrefix.Length == 0)
                throw new ArgumentException("Config property 'output_path' must not be empty.");

            var flushRowLimit = ConfigJson.GetInt(config, "flush_row_limit")
                                ?? ConfigJson.GetInt(settings, "flush_row_limit")
                                ?? DefaultFlushRowLimit;
            if (flushRowLimit < 1)
                throw new ArgumentException("Config property 'flush_row_limit' must be a positive integer.");

            var flushOnState = ConfigJson.GetBool(config, "flush_on_state")
                               ?? ConfigJson.GetBool(settings, "flush_on_state")
                               ?? true;

            var fileNameTemplate = ConfigJson.GetString(config, "file_name_template")
                                   ?? ConfigJson.GetString(settings, "file_name_template")
                                   ?? DefaultFileNameTemplate;
            ValidateFileNameTemplate(fileNameTemplate);

            return new DestinationOptions(outputPrefix, flushRowLimit, flushOnState, fileNameTemplate,
                openDal, parquet);
        }

        public string StreamPrefix(string? ns, string stream) =>
            ObjectKey.Combine(OutputPrefix,
                string.IsNullOrWhiteSpace(ns) ? null : ObjectKey.SanitizeSegment(ns),
                ObjectKey.SanitizeSegment(stream));

        public string FileName(string? ns, string stream, int sequence, DateTime timestamp)
        {
            var name = FileNameTemplate
                .Replace("{timestamp}", timestamp.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                .Replace("{sequence}", sequence.ToString("D4", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal)
                .Replace("{uuid}", Guid.NewGuid().ToString("N"), StringComparison.Ordinal)
                .Replace("{stream}", ObjectKey.SanitizeSegment(stream), StringComparison.Ordinal)
                .Replace("{namespace}", ObjectKey.SanitizeSegment(ns ?? string.Empty, string.Empty),
                    StringComparison.Ordinal);

            if (name.Length == 0 || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
                throw new InvalidOperationException(
                    $"File name template '{FileNameTemplate}' produced an invalid name '{name}'.");

            return name;
        }

        public string KeyFor(string streamPrefix, string fileName) => ObjectKey.Combine(streamPrefix, fileName);

        private static JsonElement? LoadSettings(JsonElement config, IReadOnlyDictionary<string, string> environment,
            Func<string, string?> readFile)
        {
            var path = ConfigJson.GetString(config, "settings_file");
            if (string.IsNullOrWhiteSpace(path))
                path = environment.TryGetValue("OPENDAL_SETTINGS", out var value) ? value : null;
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var contents = readFile(path);
            if (contents is null)
                throw new ArgumentException($"OpenDAL settings file '{path}' was not found.");

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(contents);
                root = document.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new ArgumentException($"OpenDAL settings file '{path}' is not valid JSON: {e.Message}", e);
            }

            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"OpenDAL settings file '{path}' must contain a JSON object.");

            foreach (var property in root.EnumerateObject())
                if (!AllowedSettingsKeys.Contains(property.Name))
                    throw new ArgumentException(
                        $"OpenDAL settings file '{path}' contains unknown property '{property.Name}'.");

            return root;
        }

        private static void ValidateFileNameTemplate(string template)
        {
            if (string.IsNullOrWhiteSpace(template)
                || template.Contains('/') || template.Contains('\\') || template.Contains("..")
                || !template.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Config property 'file_name_template' must be a file name ending in '.parquet' without directory separators or '..'.");

            foreach (Match match in Regex.Matches(template, @"\{([^}]*)\}"))
                if (!FileNamePlaceholders.Contains(match.Groups[1].Value))
                    throw new ArgumentException(
                        $"Config property 'file_name_template' contains unknown placeholder '{{{match.Groups[1].Value}}}'.");

            if (!template.Contains("{sequence}") && !template.Contains("{uuid}"))
                throw new ArgumentException(
                    "Config property 'file_name_template' must contain {sequence} or {uuid} so flushes do not overwrite each other.");

            var stripped = Regex.Replace(template, @"\{[^}]*\}", string.Empty);
            if (stripped.Contains('{') || stripped.Contains('}'))
                throw new ArgumentException(
                    "Config property 'file_name_template' contains an unterminated placeholder.");
        }

        private static IReadOnlyDictionary<string, string> EnvironmentVariables()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key)
                    result[key] = entry.Value as string ?? string.Empty;
            return result;
        }

        private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
