using System.Text.Json.Serialization;

namespace Airbyte.Cdk.Models
{
    public enum AuthType
    {
        [JsonStringEnumMemberName("oauth2.0")]
        oauth2_0
    }
}
