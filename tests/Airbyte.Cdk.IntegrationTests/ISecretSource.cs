#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
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
    /// two environment-backed implementations and keeps the seam in place: swapping in the store is a new
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

    /// <summary>
    /// Reads secrets from a single JSON object supplied through an environment variable, so CI can pass a set of
    /// connector credentials through one repository secret instead of naming each one in the workflow.
    /// </summary>
    public sealed class JsonSecretSource : ISecretSource
    {
        private readonly IReadOnlyDictionary<string, string> _secrets;

        public JsonSecretSource(string json)
        {
            var secrets = new Dictionary<string, string>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(json))
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new FormatException("The secrets JSON must be a JSON object of key/value pairs.");

                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        secrets[property.Name] = property.Value.GetString()!;
            }

            _secrets = secrets;
        }

        public IReadOnlyDictionary<string, string> Secrets => _secrets;

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_secrets.TryGetValue(key, out var value) ? value : null);
    }

    public static class SecretSourceFactory
    {
        /// <summary>
        /// Environment variable holding a JSON object of connector credentials.
        /// </summary>
        public const string JsonSecretsVariable = "AIRBYTE_INTEGRATION_SECRETS_JSON";

        public static ISecretSource Create()
        {
            var json = Environment.GetEnvironmentVariable(JsonSecretsVariable);
            return string.IsNullOrWhiteSpace(json) ? new EnvSecretSource() : new JsonSecretSource(json!);
        }
    }
}