#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk.Destinations;
using Airbyte.Cdk.Models;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Serializes destination writes and gates socket-mode state messages. A state is written to the
    /// destination only after every record in its partition has been written, and states are written
    /// in ascending id order. This preserves the protocol rule that state means "flush first".
    /// </summary>
    public sealed class DestinationMessageDispatcher
    {
        private readonly Destination _destination;
        private readonly AirbyteLogger _logger;
        private readonly JsonElement _config;
        private readonly ConfiguredAirbyteCatalog _catalog;
        private readonly TextWriter _stateOutput;
        private readonly Dictionary<string, long> _written = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedDictionary<int, AirbyteStateMessage>> _pending = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _nextStateIds = new(StringComparer.Ordinal);

        public DestinationMessageDispatcher(Destination destination, AirbyteLogger logger, JsonElement config,
            ConfiguredAirbyteCatalog catalog, TextWriter stateOutput)
        {
            _destination = destination;
            _logger = logger;
            _config = config;
            _catalog = catalog;
            _stateOutput = stateOutput;
        }

        public async Task HandleAsync(AirbyteMessage message)
        {
            switch (message.Type)
            {
                case Type.RECORD:
                    if (_destination is IParallelDestination parallel &&
                        !string.IsNullOrEmpty(message.Record?.PartitionId))
                    {
                        await parallel.WritePartitionAsync(_logger, _config, _catalog,
                            message.Record!.PartitionId!, message);
                    }
                    else
                    {
                        await _destination.Write(_logger, _config, _catalog, message);
                    }

                    if (!string.IsNullOrEmpty(message.Record?.PartitionId))
                    {
                        var partition = message.Record!.PartitionId!;
                        _written[partition] = _written.TryGetValue(partition, out var count) ? count + 1 : 1;
                    }

                    await FlushReadyStatesAsync();
                    break;
                case Type.STATE:
                    if (message.State?.Id == null)
                    {
                        await _destination.Write(_logger, _config, _catalog, message);
                        EchoState(message);
                    }
                    else
                    {
                        var key = StateOrderKey(message.State);
                        if (!_pending.TryGetValue(key, out var pending))
                        {
                            pending = new SortedDictionary<int, AirbyteStateMessage>();
                            _pending[key] = pending;
                        }

                        pending[message.State.Id.Value] = message.State;
                        await FlushReadyStatesAsync();
                    }

                    break;
                default:
                    await _destination.Write(_logger, _config, _catalog, message);
                    break;
            }
        }

        /// <summary>
        /// Fails when the stream ended with states that never received all of their records. Quietly
        /// dropping them would make the platform resume from an uncommitted checkpoint.
        /// </summary>
        public Task CompleteAsync()
        {
            if (_pending.Count > 0)
            {
                var ids = string.Join(", ", _pending.SelectMany(x => x.Value.Keys));
                throw new InvalidOperationException(
                    $"Socket stream ended before states were complete (pending state ids: {ids})");
            }

            return Task.CompletedTask;
        }

        private async Task FlushReadyStatesAsync()
        {
            foreach (var (key, pending) in _pending.ToArray())
            {
                if (!_nextStateIds.TryGetValue(key, out var nextStateId))
                    nextStateId = 1;

                while (pending.TryGetValue(nextStateId, out var state))
                {
                    if (!IsComplete(state))
                        break;

                    await _destination.Write(_logger, _config, _catalog,
                        new AirbyteMessage { Type = Type.STATE, State = state });
                    EchoState(new AirbyteMessage { Type = Type.STATE, State = state });
                    RemoveCounts(state);
                    pending.Remove(nextStateId);
                    _nextStateIds[key] = nextStateId + 1;
                    nextStateId++;
                }

                if (pending.Count == 0)
                    _pending.Remove(key);
            }
        }

        private bool IsComplete(AirbyteStateMessage state)
        {
            var expected = (long)(state.SourceStats?.RecordCount ?? 0);
            var partitions = Partitions(state).ToArray();
            if (partitions.Length == 0)
                return true;

            var written = partitions.Sum(x => _written.TryGetValue(x, out var count) ? count : 0);
            return written >= expected;
        }

        private void RemoveCounts(AirbyteStateMessage state)
        {
            foreach (var partition in Partitions(state))
                _written.Remove(partition);
        }

        private static string StateOrderKey(AirbyteStateMessage state)
        {
            if (state.Stream?.StreamDescriptor is { } descriptor)
                return $"{descriptor.Namespace}\u0000{descriptor.Name}";

            return string.Empty;
        }

        private static IEnumerable<string> Partitions(AirbyteStateMessage state)
        {
            if (state.Stream != null)
            {
                var partition = state.PartitionId ?? state.Stream.PartitionId;
                if (!string.IsNullOrEmpty(partition))
                    yield return partition!;
                yield break;
            }

            if (state.Global == null)
                yield break;

            if (!string.IsNullOrEmpty(state.PartitionId))
                yield return state.PartitionId!;

            foreach (var streamState in state.Global.StreamStates ?? Array.Empty<AirbyteStreamState>())
                if (!string.IsNullOrEmpty(streamState.PartitionId))
                    yield return streamState.PartitionId!;
        }

        private void EchoState(AirbyteMessage message) =>
            _stateOutput.WriteLine(JsonSerializer.Serialize(message, AirbyteProtocolJson.Options));
    }
}
