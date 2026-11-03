using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Parquet.Schema;

namespace destination_local_parquet.Parquet
{
    internal static class ParquetSchemaMapper
    {
        public static StreamSchema FromJsonSchema(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object)
                return StreamSchema.Fallback();

            if (TryStruct(schema, out var structSchema))
                return structSchema;

            if (TryMapRoot(schema, out var mapSchema))
                return mapSchema;

            return StreamSchema.Fallback();
        }

        public static StreamSchema FromRecord(JsonElement record)
        {
            if (record.ValueKind != JsonValueKind.Object)
                return StreamSchema.Fallback();

            var properties = record.EnumerateObject()
                .Select(x => InferField(x.Name, x.Value))
                .ToArray();

            if (properties.Length == 0)
                return StreamSchema.Fallback();

            return new StreamSchema
            {
                Parquet = new ParquetSchema(properties.Select(x => x.Field)),
                Shape = RootShape.Struct,
                Properties = properties
            };
        }

        private static bool TryStruct(JsonElement schema, out StreamSchema result)
        {
            result = null!;
            if (!schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !properties.EnumerateObject().Any())
                return false;

            var required = RequiredNames(schema);
            var fields = properties.EnumerateObject()
                .Select(x => BuildField(x.Name, x.Value, required.Contains(x.Name)))
                .ToArray();

            result = new StreamSchema
            {
                Parquet = new ParquetSchema(fields.Select(x => x.Field)),
                Shape = RootShape.Struct,
                Properties = fields
            };
            return true;
        }

        private static bool TryMapRoot(JsonElement schema, out StreamSchema result)
        {
            result = null!;
            if (!TryMapValueSchema(schema, out var valueSchema))
                return false;

            var value = BuildField("value", valueSchema, required: false);
            var key = new DataField("key", typeof(string), isNullable: false);
            var map = new MapField("value", key, value.Field);

            result = new StreamSchema
            {
                Parquet = new ParquetSchema(map),
                Shape = RootShape.Map,
                Value = new FieldMapping
                {
                    Name = "value",
                    Field = map,
                    Shape = FieldShape.Map,
                    Value = value
                }
            };
            return true;
        }

        private static FieldMapping BuildField(string name, JsonElement schema, bool required)
        {
            var (type, nullableUnion) = TypeOf(schema);

            if (TryStructField(name, schema, required, out var structField))
                return structField;

            if (TryMapValueSchema(schema, out var mapValueSchema))
                return BuildMapField(name, mapValueSchema, required);

            if (TryListField(name, schema, required, out var listField))
                return listField;

            return BuildScalarField(name, type, required, nullableUnion);
        }

        private static FieldMapping BuildScalarField(string name, string? type, bool required, bool nullableUnion)
        {
            var clrType = type switch
            {
                "integer" => typeof(long),
                "number" => typeof(double),
                "boolean" => typeof(bool),
                _ => typeof(string)
            };

            var field = new DataField(name, clrType, isNullable: !required || nullableUnion);
            return new FieldMapping
            {
                Name = name,
                Field = field,
                Shape = FieldShape.Scalar,
                IsRequired = required
            };
        }

        private static bool TryStructField(string name, JsonElement schema, bool required, out FieldMapping result)
        {
            result = null!;
            if (!schema.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || !properties.EnumerateObject().Any())
                return false;

            var requiredNames = RequiredNames(schema);
            var children = properties.EnumerateObject()
                .Select(x => BuildField(x.Name, x.Value, requiredNames.Contains(x.Name)))
                .ToArray();

            result = new FieldMapping
            {
                Name = name,
                Field = new StructField(name, children.Select(x => x.Field).ToArray()),
                Shape = FieldShape.Struct,
                IsRequired = required,
                Properties = children
            };
            return true;
        }

        private static FieldMapping BuildMapField(string name, JsonElement valueSchema, bool required)
        {
            var value = BuildField("value", valueSchema, required: false);
            var key = new DataField("key", typeof(string), isNullable: false);
            return new FieldMapping
            {
                Name = name,
                Field = new MapField(name, key, value.Field),
                Shape = FieldShape.Map,
                IsRequired = required,
                Value = value
            };
        }

        private static bool TryListField(string name, JsonElement schema, bool required, out FieldMapping result)
        {
            result = null!;
            if (!schema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
                return false;

            var item = BuildField("element", items, required: false);
            result = new FieldMapping
            {
                Name = name,
                Field = new ListField(name, item.Field),
                Shape = FieldShape.List,
                IsRequired = required,
                Value = item
            };
            return true;
        }

        private static bool TryMapValueSchema(JsonElement schema, out JsonElement valueSchema)
        {
            valueSchema = default;
            if (!schema.TryGetProperty("additionalProperties", out var additional))
                return false;

            if (additional.ValueKind == JsonValueKind.Object)
            {
                valueSchema = additional;
                return true;
            }

            if (additional.ValueKind == JsonValueKind.True)
            {
                valueSchema = JsonDocument.Parse("""{"type":"string"}""").RootElement.Clone();
                return true;
            }

            return false;
        }

        private static FieldMapping InferField(string name, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var children = value.EnumerateObject()
                    .Select(x => InferField(x.Name, x.Value))
                    .ToArray();

                if (children.Length == 0)
                    return BuildScalarField(name, "string", required: false, nullableUnion: true);

                return new FieldMapping
                {
                    Name = name,
                    Field = new StructField(name, children.Select(x => x.Field).ToArray()),
                    Shape = FieldShape.Struct,
                    Properties = children
                };
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var first = value.EnumerateArray().FirstOrDefault(x => x.ValueKind != JsonValueKind.Null);
                var item = first.ValueKind == JsonValueKind.Undefined
                    ? BuildScalarField("element", "string", required: false, nullableUnion: true)
                    : InferField("element", first);

                return new FieldMapping
                {
                    Name = name,
                    Field = new ListField(name, item.Field),
                    Shape = FieldShape.List,
                    Value = item
                };
            }

            var type = value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                _ => "string"
            };

            return BuildScalarField(name, type, required: false, nullableUnion: true);
        }

        private static HashSet<string> RequiredNames(JsonElement schema)
        {
            if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
                return new HashSet<string>(StringComparer.Ordinal);

            return required.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }

        private static (string? Type, bool Nullable) TypeOf(JsonElement schema)
        {
            if (!schema.TryGetProperty("type", out var type))
                return (null, false);

            if (type.ValueKind == JsonValueKind.String)
            {
                var value = type.GetString();
                return (value, string.Equals(value, "null", StringComparison.Ordinal));
            }

            if (type.ValueKind != JsonValueKind.Array)
                return (null, false);

            var types = type.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToArray();

            var first = types.FirstOrDefault(x => !string.Equals(x, "null", StringComparison.Ordinal));
            var nullable = types.Any(x => string.Equals(x, "null", StringComparison.Ordinal));
            return (first, nullable);
        }
    }
}
