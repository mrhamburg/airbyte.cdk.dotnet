#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteStreamStatusRateLimitedReason
    {
        [JsonPropertyName("quota_reset")]
        public long? QuotaReset { get; set; }
    }
}
