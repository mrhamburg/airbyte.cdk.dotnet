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
        private bool _built;

        public SecretsManagerSecretSource(Func<ISecretProvider> providerFactory)
        {
            _provider = new Lazy<ISecretProvider>(() =>
            {
                _built = true;
                return providerFactory();
            });
        }

        public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            var provider = _provider.Value;
            if (!await provider.SecretExistsAsync(key, cancellationToken))
                return null;

            var secret = await provider.GetSecretAsync(key, null, cancellationToken);
            return secret?.Value;
        }

        public async ValueTask DisposeAsync()
        {
            if (_built)
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