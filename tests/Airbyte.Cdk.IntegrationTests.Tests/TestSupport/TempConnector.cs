#nullable enable
using System;
using System.IO;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    /// <summary>
    /// Creates a throwaway connector directory with the files the harness reads.
    /// </summary>
    public sealed class TempConnector : IDisposable
    {
        public TempConnector(string name = "source-example")
        {
            Root = Path.Combine(Path.GetTempPath(), "airbyte-int-tests", Guid.NewGuid().ToString("N"), name);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Path_(string relative) => Path.Combine(Root, relative);

        public TempConnector WithDockerfile(string module)
        {
            File.WriteAllText(Path_("Dockerfile"),
                "FROM mcr.microsoft.com/dotnet/runtime:10.0\n" +
                $"ENV AIRBYTE_IMPL_MODULE=\"{module}\"\n");
            return this;
        }

        public TempConnector WithProject(string fileName = "source_example.csproj")
        {
            File.WriteAllText(Path_(fileName), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            return this;
        }

        public TempConnector WithSpec(string yaml)
        {
            File.WriteAllText(Path_("integration_tests.yaml"), yaml);
            return this;
        }

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