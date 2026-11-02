#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Type = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Forwards every message to the real channel while keeping the record data of the stream it wraps, so a
    /// parent stream that is configured in the catalog can feed its substreams without being read twice.
    /// </summary>
    internal sealed class RecordCollectingChannelWriter : ChannelWriter<AirbyteMessage>
    {
        private readonly ChannelWriter<AirbyteMessage> _inner;

        public RecordCollectingChannelWriter(ChannelWriter<AirbyteMessage> inner) => _inner = inner;

        public List<JsonElement> Records { get; } = new();

        public override bool TryWrite(AirbyteMessage item)
        {
            Collect(item);
            return _inner.TryWrite(item);
        }

        public override ValueTask WriteAsync(AirbyteMessage item, CancellationToken cancellationToken = default)
        {
            Collect(item);
            return _inner.WriteAsync(item, cancellationToken);
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            _inner.WaitToWriteAsync(cancellationToken);

        public override bool TryComplete(Exception? error = null) => _inner.TryComplete(error);

        private void Collect(AirbyteMessage message)
        {
            if (message.Type == Type.RECORD && message.Record?.Data.ValueKind == JsonValueKind.Object)
                Records.Add(message.Record.Data);
        }
    }
}
