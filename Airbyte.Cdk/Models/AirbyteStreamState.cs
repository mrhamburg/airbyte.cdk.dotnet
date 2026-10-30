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
    }
}
