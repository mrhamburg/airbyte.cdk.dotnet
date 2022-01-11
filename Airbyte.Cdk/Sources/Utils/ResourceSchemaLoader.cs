using System;
using System.IO;
using System.Text.Json;
using Airbyte.Cdk.Models;
using Json.Schema;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Used for loading schema information
    /// </summary>
    public class ResourceSchemaLoader
    {
        /// <summary>
        /// Checks if the config is in line with the set specifications
        /// </summary>
        public static bool TryCheckConfigAgainstSpecOrExit(JsonElement config, ConnectorSpecification spec,
            out Exception exc)
            => VerifySchema(config, JsonSchema.FromText(spec.ConnectionSpecification.RootElement.GetRawText()), out exc);

        /// <summary>
        /// Create a new instance for interpreting and validating the schema of a stream 
        /// </summary>
        public ResourceSchemaLoader(string filename) => FileName = filename;
        
        /// <summary>
        /// Filename of the schema definition loaded in this instance
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Reads the schema file and returns the jsonschema object
        /// </summary>
        public JsonSchema GetSchema()
        {
            string path = Path.Join(Path.GetDirectoryName(AirbyteEntrypoint.AirbyteImplPath), "schemas", FileName + ".json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Could not find schema file [{FileName}]: {path}");

            return JsonSchema.FromFile(path);
        }

        /// <summary>
        /// Checks if the jsonelement as provided is in accordance to the provided schema, returns false if this is not the case
        /// </summary>
        public static bool VerifySchema(JsonElement jsonElement, JsonSchema schema, out Exception exc)
        {
            var result = schema.Validate(jsonElement, new ValidationOptions { OutputFormat = OutputFormat.Detailed });
            exc = !result.IsValid ? new Exception(result.Message) : null;
            return result.IsValid;
        }
    }
}
