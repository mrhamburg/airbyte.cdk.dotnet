#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteStateMessage
    {
        /// <summary>
        /// The type of state represented by this message. If not set, LEGACY is assumed.
        /// </summary>
        [JsonPropertyName("type")]
        public AirbyteStateType? Type { get; set; }

        /// <summary>
        /// Per-stream state. Populated when <see cref="Type"/> is <see cref="AirbyteStateType.STREAM"/>.
        /// </summary>
        [JsonPropertyName("stream")]
        public AirbyteStreamState? Stream { get; set; }

        /// <summary>
        /// Global state. Populated when <see cref="Type"/> is <see cref="AirbyteStateType.GLOBAL"/>.
        /// </summary>
        [JsonPropertyName("global")]
        public AirbyteGlobalState? Global { get; set; }

        /// <summary>
        /// Legacy state data. Populated when <see cref="Type"/> is <see cref="AirbyteStateType.LEGACY"/>.
        /// </summary>
        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonElement Data { get; set; }

        [JsonPropertyName("sourceStats")]
        public AirbyteStateStats? SourceStats { get; set; }

        [JsonPropertyName("destinationStats")]
        public AirbyteStateStats? DestinationStats { get; set; }

        /// <summary>
        /// Socket mode partition identifier. The state covers the records stamped with this value.
        /// </summary>
        [JsonPropertyName("partition_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PartitionId { get; set; }

        /// <summary>
        /// Socket mode state identifier. Sources assign this from a monotonically increasing
        /// counter so destinations can commit states in order across parallel sockets.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Id { get; set; }

        /// <summary>
        /// The protocol documentation describes this discriminator as <c>state_type</c>; the protocol
        /// schema (and the platform) use <c>type</c>. Accept the documentation spelling on input.
        /// </summary>
        [JsonPropertyName("state_type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public AirbyteStateType? StateTypeAlias
        {
            get => null;
            set => Type = value;
        }
    }
}
