#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using SecretsManager;
using SecretsManager.Builder;
using SecretsManager.PostgreSql;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Resolves secrets from a PostgreSQL secret store through <c>mrhamburg.SecretsManager</c>.
    /// </summary>
    /// <remarks>
    /// Connection details come from environment variables, so no credential appears in the workflow or the
    /// repository:
    /// <list type="bullet">
    /// <item><c>SECRETS_POSTGRESQL_CONNECTION_STRING</c> (required)</item>
    /// <item><c>SECRETS_POSTGRESQL_SCHEMA</c>, <c>SECRETS_POSTGRESQL_TABLE_PREFIX</c></item>
    /// <item><c>SECRETS_POSTGRESQL_AUTO_CREATE_SCHEMA</c></item>
    /// <item><c>SECRETS_POSTGRESQL_ENCRYPTION_KEY</c> or <c>SECRETS_POSTGRESQL_ENCRYPTION_KEY_FILE</c></item>
    /// </list>
    /// The provider is built on first use, so configuring the connection string is enough to select this source
    /// even when no secret is read.
    /// </remarks>
    public sealed class SecretsManagerSecretSource : ISecretSource, IAsyncDisposable
    {
        public const string ConnectionStringVariable = "SECRETS_POSTGRESQL_CONNECTION_STRING";

        private readonly Lazy<ISecretProvider> _provider;

        public SecretsManagerSecretSource(Func<ISecretProvider> providerFactory)
        {
            _provider = new Lazy<ISecretProvider>(providerFactory);
        }

        /// <summary>
        /// Returns the secret value, or <c>null</c> when the key does not exist.
        /// </summary>
        /// <remarks>
        /// A missing key is a normal outcome (the scenario is then skipped), so the provider's not-found exception
        /// becomes <c>null</c>. That keeps it to one round trip instead of asking
        /// <see cref="ISecretProvider.SecretExistsAsync"/> first.
        /// </remarks>
        public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                var secret = await _provider.Value.GetSecretAsync(key, null, cancellationToken);
                return secret?.Value;
            }
            catch (SecretNotFoundException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // IsValueCreated stays false when the factory threw, so disposing after a failed build does not
            // re-throw the cached exception and mask the real harness error.
            if (_provider.IsValueCreated)
                await _provider.Value.DisposeAsync();
        }

        /// <summary>
        /// Builds the PostgreSQL provider from the <c>SECRETS_POSTGRESQL_*</c> environment variables.
        /// </summary>
        public static ISecretProvider BuildFromEnvironment()
        {
            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException($"{ConnectionStringVariable} is not set.");

            return new SecretProviderBuilder()
                .WithPostgreSql(options =>
                {
                    options.ConnectionString = connectionString!;

                    var schema = Environment.GetEnvironmentVariable("SECRETS_POSTGRESQL_SCHEMA");
                    if (!string.IsNullOrWhiteSpace(schema))
                        options.Schema = schema!;

                    var tablePrefix = Environment.GetEnvironmentVariable("SECRETS_POSTGRESQL_TABLE_PREFIX");
                    if (!string.IsNullOrWhiteSpace(tablePrefix))
                        options.TablePrefix = tablePrefix!;

                    var encryptionKey = Environment.GetEnvironmentVariable("SECRETS_POSTGRESQL_ENCRYPTION_KEY");
                    if (!string.IsNullOrWhiteSpace(encryptionKey))
                        options.EncryptionKey = encryptionKey;

                    var encryptionKeyFile = Environment.GetEnvironmentVariable("SECRETS_POSTGRESQL_ENCRYPTION_KEY_FILE");
                    if (!string.IsNullOrWhiteSpace(encryptionKeyFile))
                        options.EncryptionKeyFile = encryptionKeyFile;

                    var autoCreate =
                        Environment.GetEnvironmentVariable("SECRETS_POSTGRESQL_AUTO_CREATE_SCHEMA");
                    if (bool.TryParse(autoCreate, out var autoCreateSchema))
                        options.AutoCreateSchema = autoCreateSchema;
                })
                .Build();
        }
    }
}