#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteTraceMessage
    {
        [JsonPropertyName("type")]
        public AirbyteTraceType Type { get; set; }

        /// <summary>
        /// The time in milliseconds that the message was emitted.
        /// </summary>
        [JsonPropertyName("emitted_at")]
        public double EmittedAt { get; set; }

        [JsonPropertyName("error")]
        public AirbyteErrorTraceMessage? Error { get; set; }

        [JsonPropertyName("estimate")]
        public AirbyteEstimateTraceMessage? Estimate { get; set; }

        [JsonPropertyName("stream_status")]
        public AirbyteStreamStatusTraceMessage? StreamStatus { get; set; }

        [JsonPropertyName("analytics")]
        public AirbyteAnalyticsTraceMessage? Analytics { get; set; }
    }
}
