#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteControlConnectorConfigMessage
    {
        [JsonPropertyName("config")]
        public JsonElement? Config { get; set; }
    }
}
