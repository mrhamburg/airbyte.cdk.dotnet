#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteAnalyticsTraceMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }
}
