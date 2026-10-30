#nullable enable
using System.Text.Json;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Parses the JSON lines a connector writes to stdout.
    /// </summary>
    public static class MessageParser
    {
        /// <summary>
        /// Accepts a line only when it is a JSON object, so build noise and connector stack traces are ignored.
        /// </summary>
        public static bool TryParse(string line, out JsonElement message)
        {
            message = default;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            var trimmed = line.TrimStart();
            if (trimmed[0] != '{')
                return false;

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                message = document.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public static string? TypeOf(JsonElement message) =>
            message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;

        public static bool IsErrorTrace(JsonElement message)
        {
            if (TypeOf(message) != "TRACE")
                return false;

            return message.TryGetProperty("trace", out var trace)
                   && trace.ValueKind == JsonValueKind.Object
                   && trace.TryGetProperty("type", out var traceType)
                   && traceType.GetString() == "ERROR";
        }
    }
}