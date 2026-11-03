#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using MessageType = Airbyte.Cdk.Models.Type;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Passes messages through to the underlying channel while marking state messages written by custom streams.
    /// This prevents the source from appending a second, conflicting fallback state.
    /// </summary>
    public class StateTrackingChannelWriter : ChannelWriter<AirbyteMessage>
    {
        private readonly ChannelWriter<AirbyteMessage> _inner;
        private readonly StateEmitter _stateEmitter;
        private readonly PartitionTracker? _partitionTracker;
        private readonly HashSet<string> _running = new();
        private readonly object _sync = new();

        public StateTrackingChannelWriter(ChannelWriter<AirbyteMessage> inner, StateEmitter stateEmitter,
            PartitionTracker? partitionTracker = null)
        {
            _inner = inner;
            _stateEmitter = stateEmitter;
            _partitionTracker = partitionTracker;
        }

        /// <summary>
        /// Descriptor of the stream currently being read, used for legacy state messages that do not embed a descriptor.
        /// </summary>
        public StreamDescriptor? CurrentDescriptor { get; set; }

        /// <summary>
        /// Logger used to emit the RUNNING status on the first record of any stream.
        /// </summary>
        public AirbyteLogger? Logger { get; set; }

        public override bool TryWrite(AirbyteMessage item)
        {
            Observe(item);
            return _inner.TryWrite(item);
        }

        public override ValueTask WriteAsync(AirbyteMessage item, CancellationToken cancellationToken = default)
        {
            Observe(item);
            return _inner.WriteAsync(item, cancellationToken);
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            _inner.WaitToWriteAsync(cancellationToken);

        public override bool TryComplete(Exception? error = null) => _inner.TryComplete(error);

        private void Observe(AirbyteMessage item)
        {
            if (item.Type == MessageType.RECORD && CurrentDescriptor != null)
            {
                if (item.Record != null)
                    _partitionTracker?.ObserveRecord(item.Record, CurrentDescriptor);

                bool firstRecord;
                lock (_sync)
                    firstRecord = _running.Add(Key(CurrentDescriptor));

                if (firstRecord)
                    Logger?.TraceStreamStatus(CurrentDescriptor, AirbyteStreamStatus.RUNNING);

                return;
            }

            if (item.Type != MessageType.STATE)
                return;

            var descriptor = item.State?.Stream?.StreamDescriptor ?? CurrentDescriptor;
            if (descriptor != null)
            {
                _stateEmitter.MarkEmitted(descriptor);
                if (item.State != null)
                    _partitionTracker?.PrepareState(item.State, descriptor);
            }
        }

        private static string Key(StreamDescriptor descriptor) =>
            descriptor.Name + "\u0000" + (descriptor.Namespace ?? string.Empty);
    }
}
