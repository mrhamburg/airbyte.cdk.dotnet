using System;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteCatalog
    {
        [JsonPropertyName("streams")]
        public AirbyteStream[] Streams { get; set; } = Array.Empty<AirbyteStream>();
    }
}