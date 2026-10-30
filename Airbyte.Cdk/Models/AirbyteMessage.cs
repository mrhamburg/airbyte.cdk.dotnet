#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteMessage
    {
        /// <summary>
        /// Message type
        /// </summary>
        [JsonPropertyName("type")]
        public Type Type { get; set; }

        /// <summary>
        /// Log message: any kind of logging you want the platform to know about.
        /// </summary>
        [JsonPropertyName("log")]
        public AirbyteLogMessage? Log { get; set; }

        [JsonPropertyName("spec")]
        public ConnectorSpecification? Spec { get; set; }

        [JsonPropertyName("connectionStatus")]
        public AirbyteConnectionStatus? ConnectionStatus { get; set; }

        /// <summary>
        /// Log message: any kind of logging you want the platform to know about.
        /// </summary>
        [JsonPropertyName("catalog")]
        public AirbyteCatalog? Catalog { get; set; }

        /// <summary>
        /// Record message: the record
        /// </summary>
        [JsonPropertyName("record")]
        public AirbyteRecordMessage? Record { get; set; }

        /// <summary>
        /// schema message: the state. Must be the last message produced. The platform uses this information
        /// </summary>
        [JsonPropertyName("state")]
        public AirbyteStateMessage? State { get; set; }

        /// <summary>
        /// trace message: a message to communicate information about the status and performance of a connector
        /// </summary>
        [JsonPropertyName("trace")]
        public AirbyteTraceMessage? Trace { get; set; }

        /// <summary>
        /// connector config message: a message to communicate an updated configuration from a connector that should be persisted
        /// </summary>
        [JsonPropertyName("control")]
        public AirbyteControlMessage? Control { get; set; }

        /// <summary>
        /// destination catalog message: the catalog
        /// </summary>
        [JsonPropertyName("destination_catalog")]
        public DestinationCatalog? DestinationCatalog { get; set; }
    }
}