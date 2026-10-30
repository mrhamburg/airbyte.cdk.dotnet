#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Turns the <c>discover</c> output into the configured catalog the <c>read</c> command expects.
    /// </summary>
    public static class CatalogBuilder
    {
        public static string Build(JsonElement discoveredCatalog, IEnumerable<StreamSpec> streams)
        {
            var discovered = discoveredCatalog.TryGetProperty("streams", out var known)
                ? known.EnumerateArray().ToList()
                : new List<JsonElement>();

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("streams");
                writer.WriteStartArray();

                foreach (var stream in streams)
                {
                    var match = discovered.FirstOrDefault(x => NameOf(x) == stream.Name);
                    if (match.ValueKind != JsonValueKind.Object)
                        throw new ConnectorSpecException(
                            $"Stream '{stream.Name}' was not found in the discover output. Available streams: " +
                            string.Join(", ", discovered.Select(NameOf)) + ".");

                    writer.WriteStartObject();
                    writer.WritePropertyName("stream");
                    match.WriteTo(writer);
                    writer.WriteString("sync_mode", stream.SyncMode);

                    if (stream.SyncMode == "incremental")
                    {
                        writer.WritePropertyName("cursor_field");
                        writer.WriteStartArray();
                        foreach (var field in stream.CursorField ?? DefaultCursorField(match, stream.Name))
                            writer.WriteStringValue(field);
                        writer.WriteEndArray();
                        writer.WriteString("destination_sync_mode", "append");
                    }
                    else
                    {
                        writer.WriteString("destination_sync_mode", "overwrite");
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static string? NameOf(JsonElement stream) =>
            stream.TryGetProperty("name", out var name) ? name.GetString() : null;

        private static string[] DefaultCursorField(JsonElement stream, string name)
        {
            if (stream.TryGetProperty("default_cursor_field", out var cursorField)
                && cursorField.ValueKind == JsonValueKind.Array
                && cursorField.GetArrayLength() > 0)
                return cursorField.EnumerateArray().Select(x => x.GetString()!).ToArray();

            throw new ConnectorSpecException(
                $"Stream '{name}' has no default_cursor_field; set cursor_field for it in integration_tests.yaml.");
        }
    }
}