#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteRecordMessageMeta
    {
        [JsonPropertyName("changes")]
        public AirbyteRecordMessageMetaChange[]? Changes { get; set; }
    }
}
