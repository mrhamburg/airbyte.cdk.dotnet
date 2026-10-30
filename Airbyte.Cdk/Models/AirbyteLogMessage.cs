#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteLogMessage
    {
        /// <summary>
        /// The type of logging
        /// </summary>
        [JsonPropertyName("level")]
        public Level Level { get; set; }

        /// <summary>
        /// The log message
        /// </summary>
        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// An optional stack trace if the log message corresponds to an exception
        /// </summary>
        [JsonPropertyName("stack_trace")]
        public string? StackTrace { get; set; }
    }
}
