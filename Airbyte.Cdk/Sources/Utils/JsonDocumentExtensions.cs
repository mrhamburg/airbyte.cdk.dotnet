#nullable enable
using System.Text.Json;

namespace Airbyte.Cdk.Sources.Utils
{
    public static class JsonDocumentExtensions
    {
        public static T ToType<T>(this JsonElement json) =>
            JsonSerializer.Deserialize<T>(json.GetRawText(), AirbyteProtocolJson.Options)!;

        public static bool TryToType<T>(this JsonElement json, out T? value)
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(json.GetRawText(), AirbyteProtocolJson.Options);
                return true;
            }
            catch (JsonException)
            {
                value = default;
                return false;
            }
        }

        /// <summary>
        /// Converts an object to its jsonelement form
        /// </summary>
        public static JsonElement AsJsonElement(this object obj) =>
            JsonDocument.Parse(JsonSerializer.Serialize(obj, AirbyteProtocolJson.Options)).RootElement.Clone();

        /// <summary>
        /// Converts a json string to its jsonelement form
        /// </summary>
        public static JsonElement AsJsonElement(this string str) =>
            (string.IsNullOrWhiteSpace(str) ? JsonDocument.Parse("{}") : JsonDocument.Parse(str)).RootElement.Clone();
    }
}
