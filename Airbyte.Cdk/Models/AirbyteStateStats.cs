#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteStateStats
    {
        [JsonPropertyName("recordCount")]
        public double? RecordCount { get; set; }

        [JsonPropertyName("rejectedRecordCount")]
        public double? RejectedRecordCount { get; set; }

        [JsonPropertyName("additionalStats")]
        public Dictionary<string, double>? AdditionalStats { get; set; }
    }
}
