#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Resolves the credentials a connector scenario needs.
    /// </summary>
    /// <remarks>
    /// The intended production implementation reads from a PostgreSQL-backed secret store through
    /// <c>SecretsManager.PostgreSql</c>. That package is not published to nuget.org yet, so this branch ships
    /// the environment-backed implementation and keeps the seam in place: swapping in the store is a new
    /// implementation of this interface plus one line in <see cref="SecretSourceFactory"/>.
    /// </remarks>
    public interface ISecretSource
    {
        /// <summary>
        /// Returns the secret value, or null when the key is not available in this environment.
        /// </summary>
        Task<string?> GetAsync(string key, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Reads secrets from environment variables. The key is used verbatim as the variable name.
    /// </summary>
    public sealed class EnvSecretSource : ISecretSource
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Environment.GetEnvironmentVariable(key));
    }

    public static class SecretSourceFactory
    {
        public static ISecretSource Create() => new EnvSecretSource();
    }
}