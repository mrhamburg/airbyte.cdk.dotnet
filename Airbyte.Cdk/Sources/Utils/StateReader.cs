#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Parses the state file supplied by the Airbyte platform. The platform may send a legacy
    /// stream-name map, a single state message, or a list of state messages.
    /// </summary>
    public static class StateReader
    {
        public static IReadOnlyList<AirbyteStateMessage> Parse(JsonElement state)
        {
            var result = new List<AirbyteStateMessage>();
            ParseInto(state, result);
            return result;
        }

        private static void ParseInto(JsonElement state, List<AirbyteStateMessage> result)
        {
            switch (state.ValueKind)
            {
                case JsonValueKind.Undefined:
                case JsonValueKind.Null:
                    return;
                case JsonValueKind.Array:
                    foreach (var item in state.EnumerateArray())
                        ParseInto(item, result);
                    return;
                case JsonValueKind.Object:
                    break;
                default:
                    return;
            }

            // A state file may contain the full AirbyteMessage envelope.
            if (TryGetString(state, "type", out var type) && type == "STATE" &&
                state.TryGetProperty("state", out var innerState))
            {
                ParseInto(innerState, result);
                return;
            }

            if (IsStateMessage(state))
            {
                var message = JsonSerializer.Deserialize<AirbyteStateMessage>(state.GetRawText(),
                    AirbyteProtocolJson.Options);
                if (message != null)
                {
                    InferType(message);
                    result.Add(message);
                }

                return;
            }

            // Legacy state is a map of stream name to a black-box stream state.
            foreach (var property in state.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    continue;

                result.Add(new AirbyteStateMessage
                {
                    Type = AirbyteStateType.STREAM,
                    Stream = new AirbyteStreamState
                    {
                        StreamDescriptor = new StreamDescriptor { Name = property.Name },
                        StreamState = property.Value
                    }
                });
            }
        }

        private static bool IsStateMessage(JsonElement state)
        {
            if (state.TryGetProperty("stream", out _) ||
                state.TryGetProperty("global", out _) ||
                state.TryGetProperty("data", out _) ||
                state.TryGetProperty("state_type", out _))
                return true;

            if (!TryGetString(state, "type", out var type))
                return false;

            return type is "STATE" or "GLOBAL" or "STREAM" or "LEGACY";
        }

        private static void InferType(AirbyteStateMessage message)
        {
            if (message.Type.HasValue)
                return;

            if (message.Stream != null)
                message.Type = AirbyteStateType.STREAM;
            else if (message.Global != null)
                message.Type = AirbyteStateType.GLOBAL;
            else
                message.Type = AirbyteStateType.LEGACY;
        }

        private static bool TryGetString(JsonElement element, string name, out string value)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString()!;
                return true;
            }

            value = string.Empty;
            return false;
        }
    }
}
