#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteEstimateTraceMessage
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public AirbyteEstimateType Type { get; set; }

        [JsonPropertyName("namespace")]
        public string? Namespace { get; set; }

        [JsonPropertyName("row_estimate")]
        public long? RowEstimate { get; set; }

        [JsonPropertyName("byte_estimate")]
        public long? ByteEstimate { get; set; }
    }
}
