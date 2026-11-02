#nullable enable
using System;
using System.Collections.Generic;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Keeps the secret-store credentials out of the processes the harness starts.
    /// </summary>
    /// <remarks>
    /// A connector is given the values it needs through its config file. Without this, the connector would
    /// inherit <c>SECRETS_POSTGRESQL_*</c> and could read every other connector's credentials from its own
    /// environment.
    /// </remarks>
    public static class SecretEnvironment
    {
        private const string StorePrefix = "SECRETS_POSTGRESQL_";

        /// <summary>
        /// Removes the variables that give access to the whole store, leaving the rest of the environment intact.
        /// </summary>
        public static void RemoveStoreVariables(IDictionary<string, string?> environment)
        {
            foreach (var name in new List<string>(environment.Keys))
                if (name.StartsWith(StorePrefix, StringComparison.Ordinal)
                    || string.Equals(name, SecretSourceFactory.JsonSecretsVariable, StringComparison.Ordinal))
                    environment.Remove(name);
        }
    }
}
