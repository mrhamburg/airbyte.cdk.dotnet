#nullable enable
using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public class AirbyteRecordMessageFileReference
    {
        [JsonPropertyName("staging_file_url")]
        public string? StagingFileUrl { get; set; }

        [JsonPropertyName("source_file_relative_path")]
        public string? SourceFileRelativePath { get; set; }

        [JsonPropertyName("file_size_bytes")]
        public long? FileSizeBytes { get; set; }
    }
}
