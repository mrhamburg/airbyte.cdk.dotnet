#nullable enable
using System.IO;
using System.Text.RegularExpressions;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Reads the runtime contract of a connector from its Dockerfile, which is the single place the
    /// connector image declares how the implementation assembly is loaded.
    /// </summary>
    public static class DockerfileMetadata
    {
        private static readonly Regex ImplModule = new(
            "AIRBYTE_IMPL_MODULE\\s*=\\s*\"?(?<module>[A-Za-z0-9_.]+)\"?",
            RegexOptions.Compiled);

        /// <summary>
        /// Returns the value of <c>ENV AIRBYTE_IMPL_MODULE</c>, or null when the Dockerfile does not declare it.
        /// </summary>
        public static string? ReadModule(string connectorDirectory)
        {
            var dockerfile = Path.Combine(connectorDirectory, "Dockerfile");
            if (!File.Exists(dockerfile))
                return null;

            var match = ImplModule.Match(File.ReadAllText(dockerfile));
            return match.Success ? match.Groups["module"].Value : null;
        }
    }
}