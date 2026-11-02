using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Airbyte.Cdk.Sources.Streams.Http;

namespace source_jortt.Tests
{
    /// <summary>
    /// Shared inputs for the connector tests, so every test class parses JSON and builds streams the same way.
    /// </summary>
    internal static class TestData
    {
        public const string CredentialsConfig = @"{""client_id"":""id"",""client_secret"":""secret""}";

        public const string TokenResponse = @"{""access_token"":""token-1"",""expires_in"":7200}";

        public static JsonElement ParseJson(string json) => JsonDocument.Parse(json).RootElement.Clone();

        public static Dictionary<string, Airbyte.Cdk.Sources.Streams.Stream> Streams(
            string config = CredentialsConfig) =>
            new Jortt().Streams(ParseJson(config)).ToDictionary(x => x.Name);

        public static Dictionary<string, HttpStream> HttpStreams(string config = CredentialsConfig) =>
            new Jortt().Streams(ParseJson(config)).ToDictionary(x => x.Name, x => (HttpStream)x);
    }
}
