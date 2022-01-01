namespace Airbyte.Cdk
{
    public class SourceConnectorType
    {
        public const string SOURCE_API = "source-dotnet-http-api";
        public const string SOURCE_GENERIC = "source-dotnet-generic";
        public const string DESTINATION_GENERIC = "destination-dotnet-generic";

        public static string[] GetAll() => new[] {SOURCE_API, SOURCE_GENERIC, DESTINATION_GENERIC};
    }
}