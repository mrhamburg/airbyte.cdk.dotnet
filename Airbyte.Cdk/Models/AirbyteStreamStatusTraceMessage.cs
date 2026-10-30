#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteStreamStatusTraceMessage
    {
        [JsonPropertyName("stream_descriptor")]
        public StreamDescriptor StreamDescriptor { get; set; } = new();

        [JsonPropertyName("status")]
        public AirbyteStreamStatus Status { get; set; }

        [JsonPropertyName("reasons")]
        public AirbyteStreamStatusReason[]? Reasons { get; set; }
    }
}
