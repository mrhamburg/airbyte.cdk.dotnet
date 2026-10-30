#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Consolidates per-stream, global, and legacy state messages into a single lookup by stream descriptor.
    /// </summary>
    public class ConnectorStateManager
    {
        private readonly Dictionary<StreamKey, JsonElement?> _streamStates = new();
        private JsonElement? _legacyData;

        public ConnectorStateManager(IReadOnlyList<AirbyteStateMessage> state)
        {
            if (state == null)
                return;

            foreach (var message in state)
                Add(message);
        }

        private void Add(AirbyteStateMessage message)
        {
            switch (message.Type)
            {
                case AirbyteStateType.STREAM:
                    AddStreamState(message.Stream);
                    break;
                case AirbyteStateType.GLOBAL:
                    if (message.Global?.SharedState.HasValue == true &&
                        message.Global.SharedState.Value.ValueKind != JsonValueKind.Undefined)
                        throw new NotSupportedException(
                            "GLOBAL state with shared_state is not supported by this CDK; use per-stream STREAM state.");

                    if (message.Global?.StreamStates != null)
                        foreach (var streamState in message.Global.StreamStates)
                            AddStreamState(streamState);
                    break;
                case AirbyteStateType.LEGACY:
                default:
                    if (message.Data.ValueKind != JsonValueKind.Undefined)
                        _legacyData = message.Data;
                    break;
            }
        }

        private void AddStreamState(AirbyteStreamState? streamState)
        {
            if (streamState?.StreamDescriptor == null)
                return;

            _streamStates[new StreamKey(streamState.StreamDescriptor.Name,
                streamState.StreamDescriptor.Namespace)] = streamState.StreamState;
        }

        /// <summary>
        /// Returns the black-box state for a stream. Missing or reset state is represented by an empty object.
        /// </summary>
        public JsonElement GetStreamState(string name, string? ns)
        {
            if (_streamStates.TryGetValue(new StreamKey(name, ns), out var state))
                return state ?? "{}".AsJsonElement();

            if (ns != null && _streamStates.TryGetValue(new StreamKey(name, null), out state))
                return state ?? "{}".AsJsonElement();

            if (ns == null)
            {
                var namespaced = _streamStates.FirstOrDefault(x => x.Key.Name == name);
                if (namespaced.Key.Name != null)
                    return namespaced.Value ?? "{}".AsJsonElement();
            }

            if (_legacyData.HasValue &&
                _legacyData.Value.ValueKind == JsonValueKind.Object &&
                _legacyData.Value.TryGetProperty(name, out var legacyState))
                return legacyState;

            return "{}".AsJsonElement();
        }

        public AirbyteStateMessage CreateStateMessage(StreamDescriptor descriptor, JsonElement? state,
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

        public AirbyteStateMessage CreateStateMessage(string name, string? ns, JsonElement? state,
            long recordCount = 0) => CreateStateMessage(
            new StreamDescriptor { Name = name, Namespace = ns }, state, recordCount);

        private readonly struct StreamKey : IEquatable<StreamKey>
        {
            public StreamKey(string name, string? ns)
            {
                Name = name;
                Namespace = ns;
            }

            public string Name { get; }
            private string? Namespace { get; }

            public bool Equals(StreamKey other) =>
                string.Equals(Name, other.Name, StringComparison.Ordinal) &&
                string.Equals(Namespace, other.Namespace, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is StreamKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Name, Namespace);
        }
    }
}
