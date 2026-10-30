#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk
{
    /// <summary>
    /// Reads protocol messages and configured catalogs with the shared protocol JSON settings.
    /// </summary>
    public static class AirbyteProtocolReader
    {
        public static bool TryParseMessage(string? line, out AirbyteMessage? message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(line))
                return false;

            try
            {
                message = JsonSerializer.Deserialize<AirbyteMessage>(line, AirbyteProtocolJson.Options);
                return message != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public static ConfiguredAirbyteCatalog ReadCatalog(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Catalog path is required", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Cannot find catalog file: " + path, path);

            var contents = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ConfiguredAirbyteCatalog>(contents, AirbyteProtocolJson.Options)
                ?? throw new InvalidOperationException("Catalog file is empty: " + path);
        }
    }
}
