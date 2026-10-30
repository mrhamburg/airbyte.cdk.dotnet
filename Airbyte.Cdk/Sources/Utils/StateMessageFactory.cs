#nullable enable
using System.Text.Json;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Creates the per-stream state message shape used by the protocol.
    /// </summary>
    public static class StateMessageFactory
    {
        public static AirbyteStateMessage Create(StreamDescriptor descriptor, JsonElement? state,
            long recordCount = 0) => new()
        {
            Type = AirbyteStateType.STREAM,
            Stream = new AirbyteStreamState
            {
                StreamDescriptor = descriptor,
                StreamState = state
            },
            SourceStats = new AirbyteStateStats { RecordCount = recordCount }
        };
    }
}
