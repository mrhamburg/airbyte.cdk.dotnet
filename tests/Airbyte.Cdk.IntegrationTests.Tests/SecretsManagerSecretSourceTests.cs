#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SecretsManager;
using Shouldly;
using Xunit;

namespace Airbyte.Cdk.IntegrationTests.Tests
{
    [Collection("secret-environment")]
    public class SecretsManagerSecretSourceTests : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(SecretsManagerSecretSource.ConnectionStringVariable, null);
            Environment.SetEnvironmentVariable(SecretSourceFactory.JsonSecretsVariable, null);
        }

        [Fact]
        public void Test_Create_UsesSecretsManagerWhenConnectionStringIsSet()
        {
            Environment.SetEnvironmentVariable(SecretsManagerSecretSource.ConnectionStringVariable,
                "Host=localhost;Database=secrets;Username=app;Password=pass");

            SecretSourceFactory.Create().ShouldBeOfType<SecretsManagerSecretSource>();
        }

        [Fact]
        public void Test_Create_PrefersSecretsManagerOverJsonAndEnvironment()
        {
            Environment.SetEnvironmentVariable(SecretsManagerSecretSource.ConnectionStringVariable,
                "Host=localhost;Database=secrets;Username=app;Password=pass");
            Environment.SetEnvironmentVariable(SecretSourceFactory.JsonSecretsVariable, @"{""a"":""b""}");

            SecretSourceFactory.Create().ShouldBeOfType<SecretsManagerSecretSource>();
        }

        [Fact]
        public void Test_Create_FallsBackToJsonWithoutConnectionString()
        {
            Environment.SetEnvironmentVariable(SecretsManagerSecretSource.ConnectionStringVariable, null);
            Environment.SetEnvironmentVariable(SecretSourceFactory.JsonSecretsVariable, @"{""a"":""b""}");

            SecretSourceFactory.Create().ShouldBeOfType<JsonSecretSource>();
        }

        [Fact]
        public void Test_BuildFromEnvironment_RejectsMissingConnectionString()
        {
            Environment.SetEnvironmentVariable(SecretsManagerSecretSource.ConnectionStringVariable, null);

            Should.Throw<InvalidOperationException>(() => SecretsManagerSecretSource.BuildFromEnvironment())
                .Message.ShouldContain(SecretsManagerSecretSource.ConnectionStringVariable);
        }

        [Fact]
        public async Task Test_BuildFromEnvironment_BuildsPostgreSqlProvider()
        {
            Environment.SetEnvironmentVariable(SecretsManagerSecretSource.ConnectionStringVariable,
                "Host=localhost;Database=secrets;Username=app;Password=pass");

            var provider = SecretsManagerSecretSource.BuildFromEnvironment();
            provider.ShouldNotBeNull();

            await provider.DisposeAsync();
        }

        [Fact]
        public async Task Test_GetAsync_PropagatesProviderFailures()
        {
            // A configured but unreachable store must fail the run, not look like a skipped scenario.
            var source = new SecretsManagerSecretSource(() => new FailingSecretProvider());

            await Should.ThrowAsync<InvalidOperationException>(() =>
                source.GetAsync("any", CancellationToken.None));
        }

        [Fact]
        public async Task Test_ConstructingAndDisposingDoesNotBuildTheProvider()
        {
            var built = false;
            var source = new SecretsManagerSecretSource(() =>
            {
                built = true;
                throw new InvalidOperationException("should not be built");
            });

            built.ShouldBeFalse();

            // Selecting the source must stay cheap: `dotnet run --help` and list-changed never touch the store.
            await source.DisposeAsync();

            built.ShouldBeFalse();
        }

        [Fact]
        public async Task Test_GetAsync_ReturnsNullWhenTheKeyDoesNotExist()
        {
            var source = new SecretsManagerSecretSource(() => new MissingSecretProvider());

            (await source.GetAsync("absent", CancellationToken.None)).ShouldBeNull();
        }

    }

    internal sealed class FailingSecretProvider : ISecretProvider
    {
        public Task<bool> SecretExistsAsync(string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("connection refused");

        public Task<SecretValue> GetSecretAsync(string key, SecretQuery? query = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SecretValue> PutSecretAsync(string key, string value, SecretMetadata? metadata = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SecretVersionInfo>> GetSecretVersionsAsync(string key,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Minimal provider that reports every key as absent; enough to exercise the null path without a database.
    /// </summary>
    internal sealed class MissingSecretProvider : ISecretProvider
    {
        public Task<bool> SecretExistsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<SecretValue> GetSecretAsync(string key, SecretQuery? query = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SecretValue> PutSecretAsync(string key, string value, SecretMetadata? metadata = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SecretVersionInfo>> GetSecretVersionsAsync(string key,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}