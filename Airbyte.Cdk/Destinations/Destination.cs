using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;

namespace Airbyte.Cdk.Destinations
{
    public abstract class Destination : Connector
    {
        public virtual JsonElement ReadState(string statepath) =>
            string.IsNullOrWhiteSpace(statepath) || !File.Exists(statepath)
                ? "".AsJsonElement()
                : JsonDocument.Parse(File.ReadAllText(statepath)).RootElement.Clone();

        /// <summary>
        /// Writes a single input message. When <paramref name="msg"/> is a state message, the destination
        /// must flush any pending records before returning; the CDK echoes the state message immediately
        /// after this method returns.
        /// </summary>
        public abstract Task Write(AirbyteLogger logger, JsonElement config,
            ConfiguredAirbyteCatalog catalog, AirbyteMessage msg);

        /// <summary>
        /// Called once after the input stream ends. Destinations that buffer data or defer cleanup can
        /// use this to finish the last flush and commit the sync.
        /// </summary>
        public virtual Task CompleteAsync() => Task.CompletedTask;
    }
}
