using System;
using System.Text.Json;

namespace destination_opendal_parquet.OpenDal
{
    internal static class ConfigJson
    {
        public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
                return true;
            value = default;
            return false;
        }

        public static string? GetString(JsonElement? element, string name)
        {
            if (element is null || !TryGetProperty(element.Value, name, out var value))
                return null;
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"Config property '{name}' must be a string.");
            return value.GetString();
        }

        public static int? GetInt(JsonElement? element, string name)
        {
            if (element is null || !TryGetProperty(element.Value, name, out var value))
                return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
                throw new ArgumentException($"Config property '{name}' must be an integer.");
            return result;
        }

        public static bool? GetBool(JsonElement? element, string name)
        {
            if (element is null || !TryGetProperty(element.Value, name, out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ArgumentException($"Config property '{name}' must be a boolean.")
            };
        }

        public static JsonElement? GetObject(JsonElement? element, string name)
        {
            if (element is null || !TryGetProperty(element.Value, name, out var value))
                return null;
            if (value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException($"Config property '{name}' must be an object.");
            return value;
        }
    }
}
