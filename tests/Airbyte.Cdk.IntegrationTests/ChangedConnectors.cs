#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Maps <c>git diff --name-only</c> output to the connector directories that changed.
    /// </summary>
    public static class ChangedConnectors
    {
        private const string ConnectorsRoot = "airbyte-integrations/connectors/";

        public static IReadOnlyList<string> FromChangedFiles(IEnumerable<string> changedFiles, string repositoryRoot)
        {
            var root = Path.GetFullPath(repositoryRoot);

            return changedFiles
                .Select(Normalize)
                .Where(path => path != null)
                .Select(path => path!)
                .Where(path => path.StartsWith(ConnectorsRoot, StringComparison.Ordinal))
                .Select(path => path.Substring(ConnectorsRoot.Length).Split('/')[0])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => Path.Combine(root, "airbyte-integrations", "connectors", name))
                .Where(Directory.Exists)
                .ToList();
        }

        private static string? Normalize(string path)
        {
            var trimmed = path.Trim().Replace('\\', '/');
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
    }
}