#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Destinations
{
    /// <summary>
    /// Optional destination interface for connectors that can write records concurrently. The CDK
    /// falls back to the sequential <see cref="Destination.Write"/> path when this is not
    /// implemented, which is the safe default for destinations with shared connection state.
    /// </summary>
    public interface IParallelDestination
    {
        Task WritePartitionAsync(AirbyteLogger logger, JsonElement config, ConfiguredAirbyteCatalog catalog,
            string partitionId, AirbyteMessage msg);
    }
}
