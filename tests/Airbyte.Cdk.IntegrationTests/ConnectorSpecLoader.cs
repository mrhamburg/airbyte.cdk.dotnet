#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Reads and validates <c>integration_tests.yaml</c>.
    /// </summary>
    public static class ConnectorSpecLoader
    {
        private const string FileName = "integration_tests.yaml";

        private static readonly string[] SupportedSyncModes = { "full_refresh", "incremental" };

        public static ConnectorSpec Load(string connectorDirectory)
        {
            var directory = Path.GetFullPath(connectorDirectory);
            if (!Directory.Exists(directory))
                throw new ConnectorSpecException($"Connector directory not found: {directory}");

            var specPath = Path.Combine(directory, FileName);
            if (!File.Exists(specPath))
                throw new ConnectorSpecException(
                    $"{specPath} is missing. Every connector must declare its integration test scenarios.");

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            YamlRoot root;
            try
            {
                root = deserializer.Deserialize<YamlRoot>(File.ReadAllText(specPath)) ?? new YamlRoot();
            }
            catch (YamlException e)
            {
                throw new ConnectorSpecException($"{specPath}:{e.Start.Line}: {e.Message}");
            }

            var module = root.Connector?.Module;
            if (string.IsNullOrWhiteSpace(module))
                module = DockerfileMetadata.ReadModule(directory);
            if (string.IsNullOrWhiteSpace(module))
                throw new ConnectorSpecException(
                    $"{specPath}: set connector.module or declare ENV AIRBYTE_IMPL_MODULE in the connector Dockerfile.");

            var projectFile = ConnectorLocator.ResolveProjectFile(directory, root.Connector?.Project);

            var secrets = (root.Secrets ?? new Dictionary<string, string>())
                .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
                .ToDictionary(x => x.Key, x => x.Value);

            if (root.Tests == null || root.Tests.Count == 0)
                throw new ConnectorSpecException($"{specPath}: declare at least one test case.");

            var tests = new List<TestCaseSpec>();
            for (var index = 0; index < root.Tests.Count; index++)
                tests.Add(ToTest(specPath, index, root.Tests[index]));

            return new ConnectorSpec(directory, module!.Trim(), projectFile, secrets, tests);
        }

        private static TestCaseSpec ToTest(string specPath, int index, YamlTest test)
        {
            var name = string.IsNullOrWhiteSpace(test.Name) ? $"test-{index + 1}" : test.Name!.Trim();
            var location = $"{specPath}: test '{name}'";

            if (test.Streams == null || test.Streams.Count == 0)
                throw new ConnectorSpecException($"{location} declares no streams.");

            var streams = new List<StreamSpec>();
            foreach (var stream in test.Streams)
            {
                if (string.IsNullOrWhiteSpace(stream.Name))
                    throw new ConnectorSpecException($"{location} has a stream without a name.");

                var syncMode = string.IsNullOrWhiteSpace(stream.SyncMode)
                    ? "full_refresh"
                    : stream.SyncMode!.Trim().ToLowerInvariant();
                if (!SupportedSyncModes.Contains(syncMode))
                    throw new ConnectorSpecException(
                        $"{location}: unknown sync_mode '{stream.SyncMode}'. Use full_refresh or incremental.");

                var minRecords = stream.MinRecords ?? 1;
                if (minRecords < 0)
                    throw new ConnectorSpecException($"{location}: min_records cannot be negative.");

                if (streams.Any(x => x.Name == stream.Name))
                    throw new ConnectorSpecException($"{location} lists stream '{stream.Name}' twice.");

                streams.Add(new StreamSpec(stream.Name!.Trim(), syncMode,
                    stream.State == null ? null : YamlJson.ToJsonElement(stream.State), minRecords,
                    stream.CursorField?.ToArray()));
            }

            return new TestCaseSpec(name,
                test.Config == null ? EmptyObject() : YamlJson.ToJsonElement(test.Config),
                test.Limit,
                test.TimeoutSeconds ?? 600,
                streams);
        }

        private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

        private sealed class YamlRoot
        {
            public YamlConnector? Connector { get; set; }
            public Dictionary<string, string>? Secrets { get; set; }
            public List<YamlTest>? Tests { get; set; }
        }

        private sealed class YamlConnector
        {
            public string? Module { get; set; }
            public string? Project { get; set; }
        }

        private sealed class YamlTest
        {
            public string? Name { get; set; }
            public Dictionary<string, object?>? Config { get; set; }
            public int? Limit { get; set; }
            public int? TimeoutSeconds { get; set; }
            public List<YamlStream>? Streams { get; set; }
        }

        private sealed class YamlStream
        {
            public string? Name { get; set; }
            public string? SyncMode { get; set; }
            public object? State { get; set; }
            public int? MinRecords { get; set; }
            public List<string>? CursorField { get; set; }
        }
    }

    /// <summary>
    /// Converts the loose object graph YamlDotNet produces into a JSON element.
    /// </summary>
    internal static class YamlJson
    {
        public static JsonElement ToJsonElement(object? value)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                Write(writer, value);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }

        private static void Write(Utf8JsonWriter writer, object? value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case string text:
                    writer.WriteStringValue(text);
                    return;
                case bool flag:
                    writer.WriteBooleanValue(flag);
                    return;
                case DateTime dateTime when dateTime.TimeOfDay == TimeSpan.Zero:
                    writer.WriteStringValue(dateTime.ToString("yyyy-MM-dd"));
                    return;
                case DateTime dateTime:
                    writer.WriteStringValue(dateTime.ToString("o"));
                    return;
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    writer.WriteRawValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
                    return;
                case IDictionary dictionary:
                    writer.WriteStartObject();
                    foreach (DictionaryEntry entry in dictionary)
                        WriteProperty(writer, entry.Key?.ToString() ?? string.Empty, entry.Value);
                    writer.WriteEndObject();
                    return;
                case IEnumerable enumerable:
                    writer.WriteStartArray();
                    foreach (var item in enumerable)
                        Write(writer, item);
                    writer.WriteEndArray();
                    return;
                default:
                    writer.WriteStringValue(value.ToString());
                    return;
            }
        }

        private static void WriteProperty(Utf8JsonWriter writer, string name, object? value)
        {
            writer.WritePropertyName(name);
            Write(writer, value);
        }
    }
}