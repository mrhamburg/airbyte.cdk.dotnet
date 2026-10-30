using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Shared JSON settings for serializing and deserializing Airbyte protocol messages.
    /// Protocol enums are always written as their wire names (for example <c>full_refresh</c> or
    /// <c>SUCCEEDED</c>) and null properties are omitted.
    /// </summary>
    public static class AirbyteProtocolJson
    {
        public static JsonSerializerOptions Options { get; } = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
