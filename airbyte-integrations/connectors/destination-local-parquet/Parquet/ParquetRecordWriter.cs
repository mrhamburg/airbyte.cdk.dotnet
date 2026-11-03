using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Models;
using destination_local_parquet.OpenDal;
using Parquet.Serialization;
using Type = Airbyte.Cdk.Models.Type;

namespace destination_local_parquet.Parquet
{
    internal sealed class ParquetRecordWriter
    {
        private const string DestinationName = "local-parquet";

        private readonly DestinationOptions _options;
        private readonly AirbyteLogger _logger;
        private readonly IObjectStore _store;
        private readonly Dictionary<(string? Namespace, string Stream), ConfiguredAirbyteStream> _streams = new();
        private readonly Dictionary<(string? Namespace, string Stream), StreamBuffer> _buffers = new();
        private int _sequence;

        public ParquetRecordWriter(DestinationOptions options, ConfiguredAirbyteCatalog catalog,
            AirbyteLogger logger, IObjectStore store)
        {
            _options = options;
            _logger = logger;
            _store = store;

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

            foreach (var configured in _streams.Values)
            {
                if (configured.DestinationSyncMode != DestinationSyncMode.overwrite)
                    continue;

                var stream = configured.Stream!;
                var key = (stream.Namespace, stream.Name!);
                var prefix = _options.StreamPrefix(stream.Namespace, stream.Name!);
                var written = _buffers.TryGetValue(key, out var buffer)
                    ? buffer.WrittenFiles
                    : new HashSet<string>(StringComparer.Ordinal);

                foreach (var file in await _store.ListFilesAsync(prefix))
                    if (file.EndsWith(".parquet.tmp", StringComparison.Ordinal)
                        || (file.EndsWith(".parquet", StringComparison.Ordinal) && !written.Contains(file)))
                        await _store.DeleteAsync(file);
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
                buffer = new StreamBuffer(configured, _options.StreamPrefix(stream.Namespace, stream.Name!));
                _buffers[key] = buffer;
            }

            buffer.Schema ??= ResolveSchema(stream, record.Data);
            buffer.Rows.Add(RecordValueConverter.Convert(buffer.Schema, record.Data, WarningFor(buffer)));

            if (buffer.Rows.Count >= _options.FlushRowLimit)
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

        private StreamSchema ResolveSchema(AirbyteStream stream, JsonElement data)
        {
            var schema = stream.JsonSchema;
            if (schema.ValueKind != JsonValueKind.Object || !schema.EnumerateObject().Any())
                return ParquetSchemaMapper.FromRecord(data, _logger.Warn);

            return ParquetSchemaMapper.FromJsonSchema(schema, _logger.Warn);
        }

        private async Task CommitStateAsync(AirbyteStateMessage? state)
        {
            if (!_options.FlushOnState)
                return;

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

            var stream = buffer.Configured.Stream!;

            var fileName = _options.FileName(stream.Namespace, stream.Name!, _sequence++, DateTime.UtcNow);
            var key = _options.KeyFor(buffer.Prefix, fileName);

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["airbyte.stream"] = stream.Name ?? string.Empty,
                ["airbyte.namespace"] = stream.Namespace ?? string.Empty,
                ["airbyte.sync_mode"] = buffer.Configured.DestinationSyncMode.ToString(),
                ["airbyte.destination"] = DestinationName,
                ["airbyte.opendal.service"] = _options.OpenDal.Service
            };

            using var payload = new MemoryStream();
            await ParquetSerializer.SerializeUntypedAsync(
                buffer.Rows, buffer.Schema!.Parquet, payload, _options.Parquet.ToParquetOptions(), metadata);

            await _store.WriteAsync(key, payload.ToArray());
            buffer.WrittenFiles.Add(key);
            buffer.Rows.Clear();
        }

        private sealed class StreamBuffer
        {
            public StreamBuffer(ConfiguredAirbyteStream configured, string prefix)
            {
                Configured = configured;
                Prefix = prefix;
            }

            public ConfiguredAirbyteStream Configured { get; }

            public string Prefix { get; }

            public StreamSchema? Schema { get; set; }

            public List<IDictionary<string, object?>> Rows { get; } = new();

            public HashSet<string> WarnedFields { get; } = new(StringComparer.Ordinal);

            public HashSet<string> WrittenFiles { get; } = new(StringComparer.Ordinal);
        }
    }
}
