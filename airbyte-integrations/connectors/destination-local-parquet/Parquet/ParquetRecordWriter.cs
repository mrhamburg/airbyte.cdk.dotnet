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
        private readonly AirbyteLogger _logger;
        private readonly int _flushRowLimit;
        private readonly Dictionary<(string? Namespace, string Stream), ConfiguredAirbyteStream> _streams = new();
        private readonly Dictionary<(string? Namespace, string Stream), StreamBuffer> _buffers = new();
        private int _sequence;

        public ParquetRecordWriter(DestinationOptions options, ConfiguredAirbyteCatalog catalog,
            AirbyteLogger logger, int flushRowLimit = DefaultFlushRowLimit)
        {
            _options = options;
            _logger = logger;
            _flushRowLimit = flushRowLimit;

            foreach (var configured in catalog.Streams)
            {
                var stream = configured.Stream
                    ?? throw new InvalidOperationException("Catalog stream has no stream definition.");
                if (string.IsNullOrWhiteSpace(stream.Name))
                    throw new InvalidOperationException("Catalog stream has no name.");

                if (configured.DestinationSyncMode is not (DestinationSyncMode.append
                    or DestinationSyncMode.overwrite))
                    throw new NotSupportedException(
                        $"Stream '{stream.Name}' uses destination sync mode " +
                        $"'{configured.DestinationSyncMode}'. Supported modes: append, overwrite.");

                _streams[(stream.Namespace, stream.Name)] = configured;
            }
        }

        public async Task WriteAsync(AirbyteMessage message)
        {
            switch (message.Type)
            {
                case Type.RECORD:
                    await WriteRecordAsync(message);
                    break;
                case Type.STATE:
                    await CommitStateAsync(message.State);
                    break;
            }
        }

        public async Task CompleteAsync()
        {
            foreach (var buffer in _buffers.Values)
                await FlushAsync(buffer);

            foreach (var (key, configured) in _streams)
            {
                var stream = configured.Stream!;
                var directory = _options.StreamDirectory(stream.Namespace, stream.Name!);
                Directory.CreateDirectory(directory);

                if (configured.DestinationSyncMode == DestinationSyncMode.overwrite)
                {
                    var written = _buffers.TryGetValue(key, out var buffer)
                        ? buffer.WrittenFiles
                        : new HashSet<string>(StringComparer.Ordinal);

                    foreach (var file in Directory.GetFiles(directory, "*.parquet"))
                    {
                        if (!written.Contains(Path.GetFullPath(file)))
                            File.Delete(file);
                    }
                }

                foreach (var temporary in Directory.GetFiles(directory, "*.parquet.tmp"))
                    File.Delete(temporary);
            }
        }

        private async Task WriteRecordAsync(AirbyteMessage message)
        {
            var record = message.Record
                ?? throw new InvalidOperationException("RECORD message has no record payload.");

            var key = (record.Namespace, record.Stream ?? string.Empty);
            if (!_streams.TryGetValue(key, out var configured))
                throw new InvalidOperationException(
                    $"Stream '{record.Stream}' in namespace '{record.Namespace}' is not configured.");

            var stream = configured.Stream!;
            if (!_buffers.TryGetValue(key, out var buffer))
            {
                buffer = new StreamBuffer(configured, _options.StreamDirectory(stream.Namespace, stream.Name!));
                _buffers[key] = buffer;
            }

            EnsureDirectory(buffer);
            buffer.Schema ??= ResolveSchema(stream, record.Data);
            buffer.Rows.Add(RecordValueConverter.Convert(buffer.Schema, record.Data, WarningFor(buffer)));

            if (buffer.Rows.Count >= _flushRowLimit)
                await FlushAsync(buffer);
        }

        private Action<string> WarningFor(StreamBuffer buffer)
        {
            return message =>
            {
                if (buffer.WarnedFields.Add(message))
                    _logger.Warn($"[{buffer.Configured.Stream!.Name}] {message}");
            };
        }

        private static void EnsureDirectory(StreamBuffer buffer)
        {
            if (buffer.DirectoryReady)
                return;

            Directory.CreateDirectory(buffer.Directory);
            foreach (var temporary in Directory.GetFiles(buffer.Directory, "*.parquet.tmp"))
                File.Delete(temporary);

            buffer.DirectoryReady = true;
        }

        private StreamSchema ResolveSchema(AirbyteStream stream, JsonElement data)
        {
            var schema = stream.JsonSchema;
            if (schema.ValueKind != JsonValueKind.Object || !schema.EnumerateObject().Any())
                return ParquetSchemaMapper.FromRecord(data, _logger.Warn);

            return ParquetSchemaMapper.FromJsonSchema(schema, _logger.Warn);
        }

        private async Task CommitStateAsync(AirbyteStateMessage? state)
        {
            var keys = StateKeys(state);
            if (keys.Count == 0)
            {
                foreach (var buffer in _buffers.Values)
                    await FlushAsync(buffer);
                return;
            }

            foreach (var key in keys)
                if (_buffers.TryGetValue(key, out var buffer))
                    await FlushAsync(buffer);
        }

        private static List<(string? Namespace, string Stream)> StateKeys(AirbyteStateMessage? state)
        {
            var keys = new List<(string? Namespace, string Stream)>();

            if (state?.Stream?.StreamDescriptor is { } descriptor)
            {
                keys.Add((descriptor.Namespace, descriptor.Name));
                return keys;
            }

            if (state?.Global?.StreamStates is { } streamStates)
                foreach (var streamState in streamStates)
                    if (streamState.StreamDescriptor is { } globalDescriptor)
                        keys.Add((globalDescriptor.Namespace, globalDescriptor.Name));

            return keys;
        }

        private async Task FlushAsync(StreamBuffer buffer)
        {
            if (buffer.Rows.Count == 0)
                return;

            EnsureDirectory(buffer);
            var stream = buffer.Configured.Stream!;

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture);
            var sequence = _sequence++.ToString("D4", CultureInfo.InvariantCulture);
            var finalPath = Path.Combine(buffer.Directory, $"part-{timestamp}-{sequence}.parquet");
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
                    file.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, finalPath);
                buffer.WrittenFiles.Add(Path.GetFullPath(finalPath));
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
            public StreamBuffer(ConfiguredAirbyteStream configured, string directory)
            {
                Configured = configured;
                Directory = directory;
            }

            public ConfiguredAirbyteStream Configured { get; }
            public string Directory { get; }
            public bool DirectoryReady { get; set; }
            public StreamSchema? Schema { get; set; }
            public List<IDictionary<string, object?>> Rows { get; } = new();
            public HashSet<string> WarnedFields { get; } = new(StringComparer.Ordinal);
            public HashSet<string> WrittenFiles { get; } = new(StringComparer.Ordinal);
        }
    }
}
