using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using Parquet.Schema;

namespace destination_opendal_parquet.Parquet
{
    internal static class RecordValueConverter
    {
        private static readonly ConcurrentDictionary<Field, Type> IdealTypes =
            new(ReferenceEqualityComparer.Instance);

        public static Dictionary<string, object?> Convert(StreamSchema schema, JsonElement record,
            Action<string>? warn = null)
        {
            if (schema.Shape == RootShape.Struct)
                return ConvertStruct(schema.Properties, schema.ByName, record, warn);

            var mapping = schema.Value!;

            if (schema.Shape == RootShape.Map)
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = ConvertValue(mapping, record, warn)
                };
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [mapping.Name] = record.GetRawText().AsMemory()
            };
        }

        public static object? ConvertValue(FieldMapping mapping, JsonElement value, Action<string>? warn = null)
        {
            return mapping.Shape switch
            {
                FieldShape.Scalar => ConvertScalar(mapping, value),
                FieldShape.Struct => ConvertStruct(mapping.Properties, mapping.ByName, value, warn, mapping.Name),
                FieldShape.Map => ConvertMap(mapping, value, warn),
                FieldShape.List => ConvertList(mapping, value, warn),
                _ => throw new NotSupportedException($"Unsupported field shape '{mapping.Shape}'.")
            };
        }

        public static Type IdealType(Field field) => IdealTypes.GetOrAdd(field, ComputeIdealType);

        private static Type ComputeIdealType(Field field)
        {
            return field switch
            {
                DataField data => data.ClrNullableIfHasNullsType,
                StructField => typeof(Dictionary<string, object>),
                MapField map => typeof(Dictionary<,>).MakeGenericType(IdealType(map.Key), IdealType(map.Value)),
                ListField list => typeof(List<>).MakeGenericType(IdealType(list.Item)),
                _ => throw new NotSupportedException($"Unsupported field type '{field.GetType().Name}'.")
            };
        }

        private static object? ConvertScalar(FieldMapping mapping, JsonElement value)
        {
            var field = (DataField)mapping.Field;

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!field.IsNullable)
                    throw new FormatException($"Field '{mapping.Name}' is required and cannot be null.");

                return null;
            }

            var type = field.ClrType;

            if (type == typeof(ReadOnlyMemory<char>))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString()!.AsMemory(),
                    JsonValueKind.Number => value.GetRawText().AsMemory(),
                    JsonValueKind.True or JsonValueKind.False => value.GetRawText().AsMemory(),
                    JsonValueKind.Object or JsonValueKind.Array => value.GetRawText().AsMemory(),
                    _ => null
                };
            }

            if (type == typeof(long))
            {
                if (value.ValueKind != JsonValueKind.Number)
                    throw Mismatch(mapping.Name, "integer", value.ValueKind);

                if (!value.TryGetInt64(out var number))
                    throw new FormatException(
                        $"Field '{mapping.Name}' value '{value.GetRawText()}' does not fit a 64-bit integer.");

                return number;
            }

            if (type == typeof(double))
            {
                if (value.ValueKind != JsonValueKind.Number)
                    throw Mismatch(mapping.Name, "number", value.ValueKind);

                return value.GetDouble();
            }

            if (type == typeof(bool))
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Mismatch(mapping.Name, "boolean", value.ValueKind);

                return value.GetBoolean();
            }

            throw new FormatException(
                $"Field '{mapping.Name}' has unsupported CLR type '{type.Name}'.");
        }

        private static Dictionary<string, object?> ConvertStruct(
            IReadOnlyList<FieldMapping> properties, IReadOnlyDictionary<string, FieldMapping> byName,
            JsonElement value, Action<string>? warn, string? name = null)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw Mismatch(name ?? "record", "object", value.ValueKind);

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var property in value.EnumerateObject())
            {
                if (!byName.TryGetValue(property.Name, out var mapping))
                {
                    warn?.Invoke($"Ignoring extra field '{property.Name}'.");
                    continue;
                }

                result[property.Name] = ConvertValue(mapping, property.Value, warn);
            }

            foreach (var mapping in properties)
            {
                if (result.ContainsKey(mapping.Name))
                    continue;

                if (mapping.IsRequired)
                    throw new FormatException($"Required field '{mapping.Name}' is missing.");

                result[mapping.Name] = null;
            }

            return result;
        }

        private static object ConvertMap(FieldMapping mapping, JsonElement value, Action<string>? warn)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw Mismatch(mapping.Name, "object", value.ValueKind);

            var map = (MapField)mapping.Field;
            var dictionary = (IDictionary)Activator.CreateInstance(IdealType(map))!;
            var valueMapping = mapping.Value!;

            foreach (var property in value.EnumerateObject())
            {
                var key = property.Name.AsMemory();
                dictionary[key] = ConvertValue(valueMapping, property.Value, warn);
            }

            return dictionary;
        }

        private static object ConvertList(FieldMapping mapping, JsonElement value, Action<string>? warn)
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw Mismatch(mapping.Name, "array", value.ValueKind);

            var list = (IList)Activator.CreateInstance(IdealType(mapping.Field))!;
            var itemMapping = mapping.Value!;

            foreach (var item in value.EnumerateArray())
                list.Add(ConvertValue(itemMapping, item, warn));

            return list;
        }

        private static FormatException Mismatch(string name, string expected, JsonValueKind actual)
        {
            return new FormatException(
                $"Field '{name}' expected {expected} but got {actual}.");
        }
    }
}
