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
        private readonly PartitionTracker? _partitionTracker;
        private readonly Dictionary<string, string> _lastEmitted = new();
        private readonly Dictionary<string, JsonElement> _states = new();
        private readonly object _sync = new();

        public StateEmitter(ChannelWriter<AirbyteMessage> channel, PartitionTracker? partitionTracker = null)
        {
            _channel = channel;
            _partitionTracker = partitionTracker;
        }

        public async Task<bool> EmitAsync(StreamDescriptor descriptor, JsonElement? state, long recordCount = 0)
        {
            var key = Key(descriptor);
            var stateJson = state?.GetRawText() ?? "null";

            lock (_sync)
            {
                // A pending record count is meaningful even when the state blob is unchanged; it is
                // how the platform learns how many records the last checkpoint covered.
                if (recordCount == 0 &&
                    _lastEmitted.TryGetValue(key, out var previous) &&
                    previous == stateJson)
                    return false;

                _lastEmitted[key] = stateJson;
                if (state.HasValue)
                    _states[key] = state.Value;
            }

            var message = StateMessageFactory.Create(descriptor, state, recordCount);
            _partitionTracker?.PrepareState(message, descriptor);
            await _channel.WriteAsync(new AirbyteMessage { Type = MessageType.STATE, State = message });
            return true;
        }

        /// <summary>
        /// Returns the last state emitted for a stream. Substreams use it to carry the cursor from one parent
        /// record to the next, so the reported state cannot move backwards when parents arrive out of order.
        /// </summary>
        public bool TryGetLastState(StreamDescriptor descriptor, out JsonElement state)
        {
            lock (_sync)
            {
                if (_states.TryGetValue(Key(descriptor), out state))
                    return true;
            }

            state = default;
            return false;
        }

        /// <summary>
        /// Records a state message written directly by a custom stream.
        /// </summary>
        public void MarkEmitted(StreamDescriptor descriptor)
        {
            lock (_sync)
                _lastEmitted[Key(descriptor)] = "\u0000observed";
        }

        public bool HasEmitted(StreamDescriptor descriptor)
        {
            lock (_sync)
                return _lastEmitted.ContainsKey(Key(descriptor));
        }

        private static string Key(StreamDescriptor descriptor) =>
            descriptor.Name + "\u0000" + (descriptor.Namespace ?? string.Empty);
    }
}
