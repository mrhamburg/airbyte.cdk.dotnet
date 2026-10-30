#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Airbyte.Cdk.IntegrationTests
{
    /// <summary>
    /// Turns a discovered stream schema into the strict schema records are checked against.
    /// </summary>
    /// <remarks>
    /// Airbyte schemas almost never set <c>additionalProperties: false</c>, so a schema validator happily
    /// accepts a record whose API grew a new field. That is exactly the drift this harness exists to catch, so
    /// every object that declares <c>properties</c> gets <c>additionalProperties: false</c> injected.
    /// Objects that only declare <c>additionalProperties</c> (typed maps such as a currency table) are left
    /// alone: a new key in those is routine data, not schema drift.
    /// </remarks>
    public static class StrictSchema
    {
        private static readonly string[] SupportedDialects =
        {
            "http://json-schema.org/draft-06/schema#",
            "http://json-schema.org/draft-07/schema#",
            "https://json-schema.org/draft/2019-09/schema",
            "https://json-schema.org/draft/2020-12/schema"
        };

        private static readonly string[] NestedArrays = { "allOf", "anyOf", "oneOf", "prefixItems" };

        private static readonly string[] DefinitionContainers = { "$defs", "definitions" };

        public static JsonSchema Build(JsonElement discoveredSchema)
        {
            var raw = discoveredSchema.ValueKind == JsonValueKind.Undefined
                ? null
                : discoveredSchema.GetRawText();

            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "{}")
                throw new ConnectorSpecException(
                    "The connector advertises an empty json_schema, so there is nothing to validate records against.");

            if (JsonNode.Parse(raw!) is not JsonObject root)
                throw new ConnectorSpecException("The discovered json_schema is not a JSON object.");

            VerifyDialect(root);
            if (root["$schema"] == null)
                root["$schema"] = "http://json-schema.org/draft-07/schema#";

            ApplyStrict(root);
            return JsonSchema.FromText(root.ToJsonString());
        }

        private static void VerifyDialect(JsonObject root)
        {
            if (root["$schema"] is not JsonValue dialect || !dialect.TryGetValue<string>(out var value))
                return;

            if (!SupportedDialects.Contains(value))
                throw new ConnectorSpecException(
                    $"Unsupported JSON Schema dialect '{value}'. Declare one of draft-06, draft-07, 2019-09 or 2020-12.");
        }

        private static void ApplyStrict(JsonObject schema)
        {
            if (schema["properties"] is JsonObject properties)
            {
                if (!schema.ContainsKey("additionalProperties") && !schema.ContainsKey("patternProperties"))
                    schema["additionalProperties"] = false;

                foreach (var property in properties)
                    if (property.Value is JsonObject nested)
                        ApplyStrict(nested);
            }

            if (schema["items"] is JsonObject items)
                ApplyStrict(items);
            else if (schema["items"] is JsonArray itemsArray)
                foreach (var item in itemsArray)
                    if (item is JsonObject nestedItem)
                        ApplyStrict(nestedItem);

            foreach (var keyword in NestedArrays)
                if (schema[keyword] is JsonArray alternatives)
                    foreach (var alternative in alternatives)
                        if (alternative is JsonObject nestedAlternative)
                            ApplyStrict(nestedAlternative);

            if (schema["additionalProperties"] is JsonObject additional)
                ApplyStrict(additional);

            foreach (var container in DefinitionContainers)
                if (schema[container] is JsonObject definitions)
                    foreach (var definition in definitions)
                        if (definition.Value is JsonObject nestedDefinition)
                            ApplyStrict(nestedDefinition);
        }
    }
}