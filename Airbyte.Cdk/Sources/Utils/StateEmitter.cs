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
        private readonly Dictionary<string, string> _lastEmitted = new();

        public StateEmitter(ChannelWriter<AirbyteMessage> channel) => _channel = channel;

        public async Task<bool> EmitAsync(StreamDescriptor descriptor, JsonElement? state, long recordCount = 0)
        {
            var key = Key(descriptor);
            var stateJson = state?.GetRawText() ?? "null";
            if (_lastEmitted.TryGetValue(key, out var previous) && previous == stateJson)
                return false;

            var message = StateMessageFactory.Create(descriptor, state, recordCount);
            await _channel.WriteAsync(new AirbyteMessage { Type = MessageType.STATE, State = message });
            _lastEmitted[key] = stateJson;
            return true;
        }

        /// <summary>
        /// Records a state message written directly by a custom stream.
        /// </summary>
        public void MarkEmitted(StreamDescriptor descriptor) =>
            _lastEmitted[Key(descriptor)] = "\u0000observed";

        public bool HasEmitted(StreamDescriptor descriptor) => _lastEmitted.ContainsKey(Key(descriptor));

        private static string Key(StreamDescriptor descriptor) =>
            descriptor.Name + "\u0000" + (descriptor.Namespace ?? string.Empty);
    }
}
