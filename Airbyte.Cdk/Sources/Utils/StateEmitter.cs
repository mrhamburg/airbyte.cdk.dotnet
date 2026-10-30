#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Emits per-stream state messages and suppresses duplicate sequential states.
    /// </summary>
    public class StateEmitter
    {
        private readonly ChannelWriter<AirbyteMessage> _channel;
        private readonly ConnectorStateManager _stateManager = new(Array.Empty<AirbyteStateMessage>());
        private readonly Dictionary<string, string> _lastEmitted = new();

        public StateEmitter(ChannelWriter<AirbyteMessage> channel) => _channel = channel;

        public async Task<bool> EmitAsync(StreamDescriptor descriptor, JsonElement? state, long recordCount = 0)
        {
            var key = Key(descriptor);
            var stateJson = state?.GetRawText() ?? "null";
            if (_lastEmitted.TryGetValue(key, out var previous) && previous == stateJson)
                return false;

            var message = _stateManager.CreateStateMessage(descriptor, state, recordCount);
            await _channel.WriteAsync(new AirbyteMessage { Type = MessageType.STATE, State = message });
            _lastEmitted[key] = stateJson;
            return true;
        }

        public bool HasEmitted(StreamDescriptor descriptor) => _lastEmitted.ContainsKey(Key(descriptor));

        private static string Key(StreamDescriptor descriptor) =>
            descriptor.Name + "\u0000" + (descriptor.Namespace ?? string.Empty);
    }
}
