#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteGlobalState
    {
        [JsonPropertyName("shared_state")]
        public JsonElement? SharedState { get; set; }

        [JsonPropertyName("stream_states")]
        public AirbyteStreamState[] StreamStates { get; set; } = Array.Empty<AirbyteStreamState>();
    }
}
