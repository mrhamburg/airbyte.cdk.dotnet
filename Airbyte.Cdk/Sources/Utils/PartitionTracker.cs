#nullable enable
using System;
using System.Collections.Generic;
using Airbyte.Cdk.Models;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// Assigns socket-mode partition identifiers and state identifiers. A new partition is created
    /// for every checkpoint segment: records written after a state message belong to the next
    /// partition, so a destination can match each state with exactly the records it covers.
    ///
    /// In STDIO mode this class is disabled and changes nothing.
    /// </summary>
    public class PartitionTracker
    {
        private readonly Dictionary<StreamKey, Segment> _segments = new();
        private readonly Dictionary<StreamKey, int> _nextStateIds = new();
        private readonly object _sync = new();

        public PartitionTracker(bool enabled) => Enabled = enabled;

        public bool Enabled { get; }

        public void BeginStream(StreamDescriptor descriptor)
        {
            if (!Enabled)
                return;

            lock (_sync)
            {
                var key = StreamKey.From(descriptor);
                if (!_segments.ContainsKey(key))
                    _segments[key] = NewSegment();
            }
        }

        /// <summary>
        /// Stamps a record with the current partition and counts it.
        /// </summary>
        public void ObserveRecord(AirbyteRecordMessage record, StreamDescriptor descriptor)
        {
            if (!Enabled)
                return;

            lock (_sync)
            {
                var segment = GetSegment(descriptor);
                segment.RecordCount++;
                record.PartitionId = segment.PartitionId;
            }
        }

        /// <summary>
        /// Stamps a state message with the current partition, the next state id, and the number of
        /// records in the segment, then starts a new segment for the records that follow.
        /// </summary>
        public void PrepareState(AirbyteStateMessage state, StreamDescriptor descriptor)
        {
            if (!Enabled)
                return;

            lock (_sync)
            {
                var key = StreamKey.From(descriptor);
                var segment = GetSegment(descriptor);
                if (!_nextStateIds.TryGetValue(key, out var nextStateId))
                    nextStateId = 1;

                state.PartitionId ??= segment.PartitionId;
                state.Id ??= nextStateId;
                state.SourceStats ??= new AirbyteStateStats();
                state.SourceStats.RecordCount = segment.RecordCount;

                _nextStateIds[key] = nextStateId + 1;
                _segments[key] = NewSegment();
            }
        }

        private Segment GetSegment(StreamDescriptor descriptor)
        {
            var key = StreamKey.From(descriptor);
            if (!_segments.TryGetValue(key, out var segment))
            {
                segment = NewSegment();
                _segments[key] = segment;
            }

            return segment;
        }

        private static Segment NewSegment() =>
            new(Guid.NewGuid().ToString("N"));

        private sealed class Segment
        {
            public Segment(string partitionId) => PartitionId = partitionId;

            public string PartitionId { get; }
            public long RecordCount { get; set; }
        }

    }
}
