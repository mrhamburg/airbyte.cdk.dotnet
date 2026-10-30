#nullable enable
using System;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;
using MessageType = Airbyte.Cdk.Models.Type;

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
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return false;

                if (!document.RootElement.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String)
                    return false;

                if (!Enum.TryParse<MessageType>(typeElement.GetString(), true, out var messageType) ||
                    !Enum.IsDefined(typeof(MessageType), messageType))
                    return false;

                message = document.RootElement.Deserialize<AirbyteMessage>(AirbyteProtocolJson.Options);
                if (message == null || !HasPayload(message, messageType))
                {
                    message = null;
                    return false;
                }

                return true;
            }
            catch (JsonException)
            {
                message = null;
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

        private static bool HasPayload(AirbyteMessage message, MessageType type) => type switch
        {
            MessageType.RECORD => message.Record != null,
            MessageType.STATE => message.State != null,
            MessageType.LOG => message.Log != null,
            MessageType.SPEC => message.Spec != null,
            MessageType.CONNECTION_STATUS => message.ConnectionStatus != null,
            MessageType.CATALOG => message.Catalog != null,
            MessageType.TRACE => message.Trace != null,
            MessageType.CONTROL => message.Control != null,
            MessageType.DESTINATION_CATALOG => message.DestinationCatalog != null,
            _ => false
        };
    }
}
