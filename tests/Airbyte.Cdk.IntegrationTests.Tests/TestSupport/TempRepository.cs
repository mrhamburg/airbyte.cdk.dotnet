#nullable enable
using System;
using System.IO;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    /// <summary>
    /// A throwaway directory tree that mimics the connector folder layout.
    /// </summary>
    public sealed class TempRepository : IDisposable
    {
        public TempRepository(params string[] connectorNames)
        {
            Root = Path.Combine(Path.GetTempPath(), "airbyte-int-tests", Guid.NewGuid().ToString("N"), "repo");
            foreach (var connector in connectorNames)
                Directory.CreateDirectory(Path.Combine(Root, "airbyte-integrations", "connectors", connector));
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
                Directory.Delete(Path.GetDirectoryName(Root)!, true);
            }
            catch (IOException)
            {
                // Temp cleanup is best effort.
            }
        }
    }
}