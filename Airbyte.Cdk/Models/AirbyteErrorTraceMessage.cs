#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteErrorTraceMessage
    {
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("internal_message")]
        public string? InternalMessage { get; set; }

        [JsonPropertyName("stack_trace")]
        public string? StackTrace { get; set; }

        [JsonPropertyName("failure_type")]
        public AirbyteFailureType? FailureType { get; set; }

        [JsonPropertyName("stream_descriptor")]
        public StreamDescriptor? StreamDescriptor { get; set; }
    }
}
