#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Loads the connector implementation assembly and resolves the configured connector type.
    /// </summary>
    internal static class ConnectorLoader
    {
        public static Connector Load(string? implPath, string? implModule)
        {
            if (string.IsNullOrWhiteSpace(implPath) || !File.Exists(implPath))
                throw new FileNotFoundException($"Cannot find implementation binary {implPath}", implPath);

            var implementation = Assembly.LoadFile(implPath).GetTypes()
                .Where(x => typeof(Connector).IsAssignableFrom(x) && !x.IsAbstract && !x.IsInterface)
                .FirstOrDefault(x => x.Name.Equals(implModule, StringComparison.OrdinalIgnoreCase));

            if (implementation == null)
                throw new InvalidOperationException("Source implementation not found!");

            if (Activator.CreateInstance(implementation) is not Connector instance)
                throw new InvalidOperationException("Implementation provided does not implement Connector class!");

            return instance;
        }
    }
}
