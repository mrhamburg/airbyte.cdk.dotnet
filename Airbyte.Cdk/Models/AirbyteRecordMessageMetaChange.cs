#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteRecordMessageMetaChange
    {
        [JsonPropertyName("field")]
        public string Field { get; set; } = string.Empty;

        [JsonPropertyName("change")]
        public AirbyteRecordMessageMetaChangeChange Change { get; set; }

        [JsonPropertyName("reason")]
        public AirbyteRecordMessageMetaChangeReason Reason { get; set; }
    }
}
