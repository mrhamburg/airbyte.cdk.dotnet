#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    [Collection("secret-environment")]
    public class SecretSourceTests : IDisposable
    {
        private const string Key = "AIRBYTE_INT_TEST_SECRET";

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Key, null);
            Environment.SetEnvironmentVariable(SecretSourceFactory.JsonSecretsVariable, null);
            Environment.SetEnvironmentVariable(SecretsManagerSecretSource.ConnectionStringVariable, null);
        }

        [Fact]
        public async Task Test_GetAsync_ReturnsEnvironmentValue()
        {
            Environment.SetEnvironmentVariable(Key, "s3cr3t");

            (await new EnvSecretSource().GetAsync(Key, CancellationToken.None)).ShouldBe("s3cr3t");
        }

        [Fact]
        public async Task Test_GetAsync_ReturnsNull_WhenMissing()
        {
            Environment.SetEnvironmentVariable(Key, null);

            (await new EnvSecretSource().GetAsync(Key, CancellationToken.None)).ShouldBeNull();
        }

        [Fact]
        public async Task Test_JsonSecretSource_ReadsValues()
        {
            var source = new JsonSecretSource(@"{""api_key"":""abc"",""ignored"":42}");

            (await source.GetAsync("api_key", CancellationToken.None)).ShouldBe("abc");
            (await source.GetAsync("ignored", CancellationToken.None)).ShouldBeNull();
            (await source.GetAsync("missing", CancellationToken.None)).ShouldBeNull();
        }

        [Fact]
        public void Test_JsonSecretSource_RejectsNonObject()
        {
            Should.Throw<FormatException>(() => new JsonSecretSource("[1,2]"));
        }

        [Fact]
        public void Test_Create_ReturnsEnvSourceByDefault()
        {
            Environment.SetEnvironmentVariable(SecretSourceFactory.JsonSecretsVariable, null);

            SecretSourceFactory.Create().ShouldBeOfType<EnvSecretSource>();
        }

        [Fact]
        public void Test_Create_ReturnsJsonSourceWhenConfigured()
        {
            Environment.SetEnvironmentVariable(SecretSourceFactory.JsonSecretsVariable, @"{""api_key"":""abc""}");

            SecretSourceFactory.Create().ShouldBeOfType<JsonSecretSource>();
        }
    }

    public class RedactionTests
    {
        [Fact]
        public void Test_Apply_MasksEverySecret()
        {
            Redaction.Apply("token=abc and key=def", new[] { "abc", "def" })
                .ShouldBe("token=*** and key=***");
        }

        [Fact]
        public void Test_Apply_LeavesTextWithoutSecrets()
        {
            Redaction.Apply("nothing secret", new[] { "abc" }).ShouldBe("nothing secret");
        }

        [Fact]
        public void Test_Apply_IgnoresEmptySecrets()
        {
            Redaction.Apply("text", new[] { "" }).ShouldBe("text");
        }
    }

    public class DockerfileMetadataTests
    {
        [Fact]
        public void Test_ReadModule_ReadsQuotedEnv()
        {
            using var connector = new TempConnector().WithDockerfile("ExchangeRates");

            DockerfileMetadata.ReadModule(connector.Root).ShouldBe("ExchangeRates");
        }

        [Fact]
        public void Test_ReadModule_ReadsUnquotedEnv()
        {
            using var connector = new TempConnector();
            File.WriteAllText(Path.Combine(connector.Root, "Dockerfile"),
                "FROM scratch\nENV AIRBYTE_IMPL_MODULE=Program\n");

            DockerfileMetadata.ReadModule(connector.Root).ShouldBe("Program");
        }

        [Fact]
        public void Test_ReadModule_ReturnsNullWithoutDockerfile()
        {
            using var connector = new TempConnector();

            DockerfileMetadata.ReadModule(connector.Root).ShouldBeNull();
        }

        [Fact]
        public void Test_ReadModule_ReturnsNullWithoutEnv()
        {
            using var connector = new TempConnector();
            File.WriteAllText(Path.Combine(connector.Root, "Dockerfile"), "FROM scratch\n");

            DockerfileMetadata.ReadModule(connector.Root).ShouldBeNull();
        }
    }

    public class ConnectorLocatorTests
    {
        [Fact]
        public void Test_ResolveProjectFile_UsesTheOnlyProject()
        {
            using var connector = new TempConnector().WithProject("source_x.csproj");

            ConnectorLocator.ResolveProjectFile(connector.Root, null).ShouldEndWith("source_x.csproj");
        }

        [Fact]
        public void Test_ResolveProjectFile_RejectsAmbiguousDirectory()
        {
            using var connector = new TempConnector().WithProject("a.csproj").WithProject("b.csproj");

            Should.Throw<ConnectorSpecException>(() => ConnectorLocator.ResolveProjectFile(connector.Root, null));
        }

        [Fact]
        public void Test_ResolveProjectFile_RejectsMissingConfiguredProject()
        {
            using var connector = new TempConnector().WithProject();

            Should.Throw<ConnectorSpecException>(() =>
                ConnectorLocator.ResolveProjectFile(connector.Root, "nope.csproj"));
        }
    }
}