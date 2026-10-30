#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    /// <summary>
    /// Identifies a stream by its name and optional namespace.
    /// </summary>
    public class StreamDescriptor
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("namespace")]
        public string? Namespace { get; set; }
    }
}
