#nullable enable
using System;
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class DestinationCatalog
    {
        [JsonPropertyName("operations")]
        public DestinationOperation[] Operations { get; set; } = Array.Empty<DestinationOperation>();
    }
}
