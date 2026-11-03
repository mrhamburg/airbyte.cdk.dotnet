#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Airbyte.Cdk.Models;
using Io.Airbyte.Protocol.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Protobuf
{
    /// <summary>
    /// Maps between the CDK protocol models and the generated Airbyte Protobuf types.
    ///
    /// The .NET CDK writes every message through <c>airbyte_protocol_message</c>, the JSON
    /// fallback defined by the official proto. This keeps record values schema-correct for all
    /// JSON types while still using the socket transport and length-delimited framing. Typed
    /// records sent by another CDK are decoded when the configured catalog provides the schema.
    /// </summary>
    public static class AirbyteProtobufMapper
    {
        public static AirbyteMessageProtobuf ToProtobuf(AirbyteMessage message) =>
            new()
            {
                AirbyteProtocolMessage = JsonSerializer.Serialize(message, AirbyteProtocolJson.Options)
            };

        public static AirbyteMessage? FromProtobuf(AirbyteMessageProtobuf message,
            ConfiguredAirbyteCatalog? catalog = null)
        {
            switch (message.TypeCase)
            {
                case AirbyteMessageProtobuf.TypeOneofCase.AirbyteProtocolMessage:
                    if (!AirbyteProtocolReader.TryParseMessage(message.AirbyteProtocolMessage, out var parsed))
                        throw new InvalidDataException("Protobuf message contains invalid Airbyte protocol JSON");
                    return parsed;
                case AirbyteMessageProtobuf.TypeOneofCase.Record:
                    return FromRecord(message.Record, catalog);
                case AirbyteMessageProtobuf.TypeOneofCase.Probe:
                case AirbyteMessageProtobuf.TypeOneofCase.None:
                    return null;
                default:
                    throw new InvalidDataException($"Unsupported protobuf message type: {message.TypeCase}");
            }
        }

        private static AirbyteMessage FromRecord(AirbyteRecordMessageProtobuf record,
            ConfiguredAirbyteCatalog? catalog)
        {
            var stream = catalog?.Streams.FirstOrDefault(x =>
                x.Stream?.Name == record.StreamName &&
                string.Equals(x.Stream?.Namespace ?? string.Empty, record.StreamNamespace ?? string.Empty,
                    StringComparison.Ordinal));

            var fieldNames = FieldNames(stream?.Stream?.JsonSchema);
            var data = new JsonObject();
            for (var index = 0; index < record.Data.Count; index++)
            {
                var name = index < fieldNames.Count ? fieldNames[index] : $"field_{index}";
                data[name] = ToJsonNode(record.Data[index]);
            }

            return new AirbyteMessage
            {
                Type = Type.RECORD,
                Record = new AirbyteRecordMessage
                {
                    Stream = record.StreamName,
                    Namespace = string.IsNullOrEmpty(record.StreamNamespace) ? null : record.StreamNamespace,
                    EmittedAt = checked((long)record.EmittedAtMs),
                    PartitionId = string.IsNullOrEmpty(record.PartitionId) ? null : record.PartitionId,
                    Data = JsonSerializer.SerializeToElement(data)
                }
            };
        }

        /// <summary>
        /// Typed records are ordered alphabetically by field name, matching the Kotlin CDK.
        /// </summary>
        private static IReadOnlyList<string> FieldNames(JsonElement? schema)
        {
            if (schema is not { ValueKind: JsonValueKind.Object } ||
                !schema.Value.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
                return Array.Empty<string>();

            return properties.EnumerateObject().Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();
        }

        private static JsonNode? ToJsonNode(AirbyteValueProtobuf value) => value.ValueCase switch
        {
            AirbyteValueProtobuf.ValueOneofCase.Boolean => JsonValue.Create(value.Boolean),
            AirbyteValueProtobuf.ValueOneofCase.String => JsonValue.Create(value.String),
            AirbyteValueProtobuf.ValueOneofCase.Integer => JsonValue.Create(value.Integer),
            AirbyteValueProtobuf.ValueOneofCase.BigInteger => RawNumber(value.BigInteger),
            AirbyteValueProtobuf.ValueOneofCase.Number => JsonValue.Create(value.Number),
            AirbyteValueProtobuf.ValueOneofCase.BigDecimal => RawNumber(value.BigDecimal),
            AirbyteValueProtobuf.ValueOneofCase.Date => JsonValue.Create(
                DateTime.UnixEpoch.AddDays(value.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            AirbyteValueProtobuf.ValueOneofCase.TimeWithTimezone => JsonValue.Create(FormatOffsetTime(value.TimeWithTimezone)),
            AirbyteValueProtobuf.ValueOneofCase.TimeWithoutTimezone => JsonValue.Create(FormatLocalTime(value.TimeWithoutTimezone)),
            AirbyteValueProtobuf.ValueOneofCase.TimestampWithTimezone => JsonValue.Create(
                DateTimeOffset.FromUnixTimeSeconds(value.TimestampWithTimezone.EpochSecond)
                    .AddTicks(value.TimestampWithTimezone.Nano / 100)
                    .ToOffset(TimeSpan.FromSeconds(value.TimestampWithTimezone.OffsetSeconds))
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture)),
            AirbyteValueProtobuf.ValueOneofCase.TimestampWithoutTimezone => JsonValue.Create(
                DateTime.UnixEpoch.AddDays(value.TimestampWithoutTimezone.DateDaysSinceEpoch)
                    .AddTicks((long)(value.TimestampWithoutTimezone.NanosOfDay / 100))
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)),
            AirbyteValueProtobuf.ValueOneofCase.Json => JsonNode.Parse(value.Json.ToStringUtf8()),
            AirbyteValueProtobuf.ValueOneofCase.Null => null,
            _ => null
        };

        private static JsonNode? RawNumber(string text) =>
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                ? JsonNode.Parse(text)
                : JsonValue.Create(text);

        private static string FormatLocalTime(ulong nanosOfDay)
        {
            var ticks = (long)(nanosOfDay / 100);
            var time = TimeSpan.FromTicks(ticks);
            return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}{time.Ticks % TimeSpan.TicksPerSecond / 10:D4}";
        }

        private static string FormatOffsetTime(OffsetTime value)
        {
            var ticks = (long)(value.NanosOfDay / 100);
            var time = TimeSpan.FromTicks(ticks);
            var offset = TimeSpan.FromSeconds(value.OffsetSeconds);
            var suffix = offset < TimeSpan.Zero ? "-" : "+";
            var absolute = offset.Duration();
            return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}{time.Ticks % TimeSpan.TicksPerSecond / 10:D4}{suffix}{absolute.Hours:D2}:{absolute.Minutes:D2}";
        }
    }
}
