#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteControlMessage
    {
        [JsonPropertyName("type")]
        public AirbyteControlType Type { get; set; }

        /// <summary>
        /// The time in milliseconds that the message was emitted.
        /// </summary>
        [JsonPropertyName("emitted_at")]
        public double EmittedAt { get; set; }

        [JsonPropertyName("connectorConfig")]
        public AirbyteControlConnectorConfigMessage? ConnectorConfig { get; set; }
    }
}
