#nullable enable
using System.Collections.Generic;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    public class SecretEnvironmentTests
    {
        [Fact]
        public void Test_RemoveStoreVariables_KeepsOnlyWhatTheConnectorNeeds()
        {
            var environment = new Dictionary<string, string?>
            {
                { SecretsManagerSecretSource.ConnectionStringVariable, "Host=localhost;Database=secrets" },
                { "SECRETS_POSTGRESQL_ENCRYPTION_KEY", "key" },
                { "SECRETS_POSTGRESQL_ENCRYPTION_KEY_FILE", "/run/secrets/key" },
                { "SECRETS_POSTGRESQL_SCHEMA", "secrets" },
                { SecretSourceFactory.JsonSecretsVariable, @"{""api_key"":""abc""}" },
                { "JORTT_CLIENT_ID", "id" },
                { "PATH", "/usr/bin" }
            };

            SecretEnvironment.RemoveStoreVariables(environment);

            environment.Keys.ShouldBe(new[] { "JORTT_CLIENT_ID", "PATH" }, ignoreOrder: true);
        }

        [Fact]
        public void Test_RemoveStoreVariables_ToleratesAnEmptyEnvironment()
        {
            var environment = new Dictionary<string, string?>();

            SecretEnvironment.RemoveStoreVariables(environment);

            environment.ShouldBeEmpty();
        }
    }
}
