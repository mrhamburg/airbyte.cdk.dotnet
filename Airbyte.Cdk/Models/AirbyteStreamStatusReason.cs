#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteStreamStatusReason
    {
        [JsonPropertyName("type")]
        public AirbyteStreamStatusReasonType Type { get; set; }

        [JsonPropertyName("rate_limited")]
        public AirbyteStreamStatusRateLimitedReason? RateLimited { get; set; }
    }
}
