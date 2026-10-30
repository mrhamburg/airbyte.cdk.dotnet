#nullable enable
using System.IO;
using System.Linq;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Finds the project file to build for a connector directory.
    /// </summary>
    public static class ConnectorLocator
    {
        public static string ResolveProjectFile(string connectorDirectory, string? configuredFileName)
        {
            if (!string.IsNullOrWhiteSpace(configuredFileName))
            {
                var configured = Path.Combine(connectorDirectory, configuredFileName!);
                if (!File.Exists(configured))
                    throw new ConnectorSpecException($"Connector project not found: {configured}");
                return configured;
            }

            var candidates = Directory.GetFiles(connectorDirectory, "*.csproj");
            if (candidates.Length == 1)
                return candidates[0];

            throw new ConnectorSpecException(candidates.Length == 0
                ? $"No .csproj found in {connectorDirectory}; set connector.project in integration_tests.yaml."
                : $"{connectorDirectory} contains several project files; set connector.project in integration_tests.yaml.");
        }

        /// <summary>
        /// Folder name used to identify a connector in logs and CI matrices.
        /// </summary>
        public static string ConnectorName(string connectorDirectory) =>
            new DirectoryInfo(connectorDirectory).Name;
    }
}