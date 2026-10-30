#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class DestinationOperation
    {
        [JsonPropertyName("object_name")]
        public string ObjectName { get; set; } = string.Empty;

        [JsonPropertyName("sync_mode")]
        public DestinationSyncMode SyncMode { get; set; }

        [JsonPropertyName("json_schema")]
        public JsonElement JsonSchema { get; set; }

        [JsonPropertyName("matching_keys")]
        public string[][]? MatchingKeys { get; set; }
    }
}
