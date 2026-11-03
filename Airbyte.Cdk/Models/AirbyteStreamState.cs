#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteStreamState
    {
        [JsonPropertyName("stream_descriptor")]
        public StreamDescriptor StreamDescriptor { get; set; } = new();

        /// <summary>
        /// The black-box state for this stream. A null value means the state should be reset.
        /// </summary>
        [JsonPropertyName("stream_state")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public JsonElement? StreamState { get; set; }

        /// <summary>
        /// Socket mode partition identifier for a stream inside a GLOBAL state.
        /// </summary>
        [JsonPropertyName("partition_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PartitionId { get; set; }

        /// <summary>
        /// Socket mode state identifier for a stream inside a GLOBAL state.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Id { get; set; }
    }
}
