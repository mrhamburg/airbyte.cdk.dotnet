using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Models;
using Parquet.Serialization;
using Type = Airbyte.Cdk.Models.Type;

namespace destination_local_parquet.Parquet
{
    internal sealed class ParquetRecordWriter
    {
        private const int DefaultFlushRowLimit = 50_000;
        private const string DestinationName = "local-parquet";

        private readonly DestinationOptions _options;
        private readonly ConfiguredAirbyteCatalog _catalog;
        private readonly AirbyteLogger _logger;
        private readonly int _flushRowLimit;
        private readonly Dictionary<(string? Namespace, string Stream), StreamBuffer> _buffers = new();
        private int _sequence;

        public ParquetRecordWriter(DestinationOptions options, ConfiguredAirbyteCatalog catalog,
            AirbyteLogger logger, int flushRowLimit = DefaultFlushRowLimit)
        {
            _options = options;
            _catalog = catalog;
            _logger = logger;
            _flushRowLimit = flushRowLimit;
        }

        public async Task WriteAsync(AirbyteMessage message)
        {
            switch (message.Type)
            {
                case Type.RECORD:
                    await WriteRecordAsync(message);
                    break;
                case Type.STATE:
                    await FlushAllAsync();
                    break;
            }
        }

        private async Task WriteRecordAsync(AirbyteMessage message)
        {
            var record = message.Record
                ?? throw new InvalidOperationException("RECORD message has no record payload.");

            var configured = FindStream(record);
            var stream = configured.Stream!;
            var buffer = GetBuffer(configured);
            var directory = _options.StreamDirectory(stream.Namespace, stream.Name!);

            Directory.CreateDirectory(directory);
            ApplyOverwrite(configured, directory, buffer);

            buffer.Schema ??= ResolveSchema(stream, record.Data);
            buffer.Rows.Add(RecordValueConverter.Convert(buffer.Schema, record.Data, _logger.Warn));

            if (buffer.Rows.Count >= _flushRowLimit)
                await FlushAsync(buffer);
        }

        private ConfiguredAirbyteStream FindStream(AirbyteRecordMessage record)
        {
            foreach (var configured in _catalog.Streams)
            {
                var stream = configured.Stream;
                if (stream is null)
                    continue;

                if (string.Equals(stream.Name, record.Stream, StringComparison.Ordinal)
                    && string.Equals(stream.Namespace, record.Namespace, StringComparison.Ordinal))
                    return configured;
            }

            throw new InvalidOperationException(
                $"Stream '{record.Stream}' in namespace '{record.Namespace}' is not configured.");
        }

        private StreamBuffer GetBuffer(ConfiguredAirbyteStream configured)
        {
            var stream = configured.Stream!;
            var key = (stream.Namespace, stream.Name!);
            if (!_buffers.TryGetValue(key, out var buffer))
            {
                buffer = new StreamBuffer(configured);
                _buffers[key] = buffer;
            }

            return buffer;
        }

        private static void ApplyOverwrite(ConfiguredAirbyteStream configured, string directory, StreamBuffer buffer)
        {
            if (configured.DestinationSyncMode != DestinationSyncMode.overwrite || buffer.OverwriteApplied)
                return;

            foreach (var file in Directory.GetFiles(directory, "*.parquet"))
                File.Delete(file);

            buffer.OverwriteApplied = true;
        }

        private static StreamSchema ResolveSchema(AirbyteStream stream, JsonElement data)
        {
            var schema = stream.JsonSchema;
            if (schema.ValueKind != JsonValueKind.Object || !schema.EnumerateObject().Any())
                return ParquetSchemaMapper.FromRecord(data);

            return ParquetSchemaMapper.FromJsonSchema(schema);
        }

        private async Task FlushAllAsync()
        {
            foreach (var buffer in _buffers.Values)
                await FlushAsync(buffer);
        }

        private async Task FlushAsync(StreamBuffer buffer)
        {
            if (buffer.Rows.Count == 0)
                return;

            var stream = buffer.Configured.Stream!;
            var directory = _options.StreamDirectory(stream.Namespace, stream.Name!);
            Directory.CreateDirectory(directory);

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
            var sequence = _sequence++.ToString("D4", CultureInfo.InvariantCulture);
            var finalPath = Path.Combine(directory, $"part-{timestamp}-{sequence}.parquet");
            var temporaryPath = finalPath + ".tmp";

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["airbyte.stream"] = stream.Name ?? string.Empty,
                ["airbyte.namespace"] = stream.Namespace ?? string.Empty,
                ["airbyte.sync_mode"] = buffer.Configured.DestinationSyncMode.ToString(),
                ["airbyte.destination"] = DestinationName
            };

            try
            {
                await using (var file = File.Create(temporaryPath))
                {
                    await ParquetSerializer.SerializeUntypedAsync(
                        buffer.Rows, buffer.Schema!.Parquet, file, null, metadata);
                }

                File.Move(temporaryPath, finalPath);
                buffer.Rows.Clear();
            }
            catch
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
                throw;
            }
        }

        private sealed class StreamBuffer
        {
            public StreamBuffer(ConfiguredAirbyteStream configured) => Configured = configured;

            public ConfiguredAirbyteStream Configured { get; }
            public StreamSchema? Schema { get; set; }
            public List<IDictionary<string, object?>> Rows { get; } = new();
            public bool OverwriteApplied { get; set; }
        }
    }
}
