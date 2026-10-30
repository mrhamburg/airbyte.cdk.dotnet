namespace Airbyte.Cdk.Models
{
    /// <summary>
    /// Airbyte protocol versions understood by this CDK.
    /// </summary>
    public static class AirbyteProtocolVersion
    {
        /// <summary>
        /// Latest protocol version implemented by the STDIO/JSON transport. This is the
        /// airbyte-protocol package release (2025-10-10); the schema file's own `version`
        /// field is stale metadata and is not the connector protocol version.
        /// </summary>
        public const string Latest = "0.19.0";
    }
}
