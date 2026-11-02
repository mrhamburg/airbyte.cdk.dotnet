#nullable enable
using System;

namespace Airbyte.Cdk.Sources.Streams
{
    /// <summary>
    /// Declares that a stream is a substream of another stream: it is read once per record of its parent.
    /// </summary>
    /// <remarks>
    /// The parent is read first and each of its records produces one slice. The extracted key is handed to the
    /// substream through the stream slice, so <see cref="Streams.Http.HttpStream.Path"/>,
    /// <see cref="Streams.Http.HttpStream.RequestParams"/> and
    /// <see cref="Streams.Http.HttpStream.RequestHeaders"/> can build a per-parent request, for example
    /// <c>projects/{project_id}/line_items</c>.
    /// </remarks>
    public sealed class SubstreamDependency
    {
        public SubstreamDependency(string parentStream, string parentKeyField, string sliceParameter)
        {
            if (string.IsNullOrWhiteSpace(parentStream))
                throw new ArgumentException("A substream needs the name of its parent stream", nameof(parentStream));
            if (string.IsNullOrWhiteSpace(parentKeyField))
                throw new ArgumentException("A substream needs the field that identifies a parent record", nameof(parentKeyField));
            if (string.IsNullOrWhiteSpace(sliceParameter))
                throw new ArgumentException("A substream needs the slice parameter its parent key is exposed as", nameof(sliceParameter));

            ParentStream = parentStream;
            ParentKeyField = parentKeyField;
            SliceParameter = sliceParameter;
        }

        /// <summary>Name of the stream whose records drive this one.</summary>
        public string ParentStream { get; }

        /// <summary>
        /// Dotted path to the key inside a parent record, for example <c>id</c> or <c>customer.id</c>.
        /// </summary>
        public string ParentKeyField { get; }

        /// <summary>
        /// Key under which the parent key is handed to the substream's stream slice, for example
        /// <c>project_id</c>.
        /// </summary>
        public string SliceParameter { get; }

        public override string ToString() => $"{ParentStream}.{ParentKeyField} -> {SliceParameter}";
    }
}
