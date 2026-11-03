#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Transport
{
    /// <summary>
    /// Adapts an <see cref="IDataChannelWriter"/> to the <see cref="ChannelWriter{T}"/> contract that
    /// streams and the source logger already use.
    /// </summary>
    public sealed class SinkChannelWriter : ChannelWriter<AirbyteMessage>
    {
        private readonly IDataChannelWriter _sink;

        public SinkChannelWriter(IDataChannelWriter sink) => _sink = sink;

        public override ValueTask WriteAsync(AirbyteMessage item, CancellationToken cancellationToken = default) =>
            _sink.WriteAsync(item, cancellationToken);

        public override bool TryWrite(AirbyteMessage item)
        {
            try
            {
                _sink.WriteAsync(item).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public override bool TryComplete(Exception? error = null) => false;
    }
}
