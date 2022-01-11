using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Json.Schema;

namespace Airbyte.Cdk.Sources.Streams
{
    /// <summary>
    /// Base abstract class for an Airbyte Stream. Makes no assumption of the Stream's underlying transport protocol.
    /// </summary>
    public abstract class Stream
    {
        /// <summary>
        /// Decides how often to checkpoint state (i.e: emit a STATE message). E.g: if this returns a value of 100, then state is persisted after reading
        /// 100 records, then 200, 300, etc.. A good default value is 1000 although your mileage may vary depending on the underlying data source.
        /// 
        /// Checkpointing a stream avoids re-reading records in the case a sync is failed or cancelled.
        /// 
        /// return None if state should not be checkpointed e.g: because records returned from the underlying data source are not returned in
        /// ascending order with respect to the cursor field. This can happen if the source does not support reading records in ascending order of
        /// created_at date (or whatever the cursor is). In those cases, state must only be saved once the full stream has been read.
        /// </summary>
        public virtual int? StateCheckpointInterval { get; protected set; } = null;

        /// <summary>
        /// Logger instance, for sending log messages as per Airbyte protocol
        /// </summary>
        public AirbyteLogger Logger { get; protected set; }

        /// <summary>
        /// If true, this stream supports incremental updates
        /// </summary>
        public bool SupportsIncremental
        {
            get => CursorField.Length > 0;
        }

        /// <summary>
        /// If incremental support is to be implemented, this field needs to be set for determining the range of updates
        /// </summary>
        public bool SourceDefinedCursor { get; protected set; } = true;

        /// <summary>
        /// Name of this stream 
        /// </summary>
        public string Name
        {
            //TODO: should be converted to PascalCase or is snake case an Airbyte protocol requirement?
            get => (string.IsNullOrWhiteSpace(_givenName) ? GetType().Name : _givenName).ToSnakeCase();
        }

        /// <summary>
        /// An internal name, which can deviate from the default classname
        /// </summary>
        protected string _givenName = string.Empty;

        /// <summary>
        /// This method should be overridden by subclasses to read records based on the inputs
        /// </summary>
        public abstract Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode, ChannelWriter<AirbyteMessage> streamchannel,
            JsonElement streamstate,
            long? recordlimit = null,
            string[] cursorfield = null,
            Dictionary<string, object> streamslice = null);

        /// <summary>
        /// The default implementation of this method looks for a JSONSchema file with the same name as this stream's "name" property.
        /// Override as needed.
        /// </summary>
        /// <returns>A JsonSchema object representing this stream</returns>
        public virtual JsonSchema GetJsonSchema() => new ResourceSchemaLoader(Name).GetSchema();

        /// <summary>
        /// Override to extract state from the latest record.Needed to implement incremental sync.
        ///
        ///     Inspects the latest record extracted from the data source and the current state object and return an updated state object.
        ///
        /// For example: if the state object is based on created_at timestamp, and the current state is { 'created_at': 10}, and the latest_record is
        /// {'name': 'octavia', 'created_at': 20 } then this method would return {'created_at': 20}
        /// to indicate state should be updated to this object.
        /// </summary>
        public virtual JsonElement GetUpdatedState(JsonElement currentstreamstate,
            JsonElement latestrecord) => "{}".AsJsonElement();

        /// <summary>
        /// Converts the current stream to an Airbyte stream, to determine stream specifications
        /// </summary>
        public virtual AirbyteStream AsAirbyteStream()
        {
            var stream = new AirbyteStream { Name = Name, JsonSchema = GetJsonSchema().AsJsonElement(), SupportedSyncModes = new[] { SyncMode.full_refresh } };

            if (SupportsIncremental)
            {
                stream.SourceDefinedCursor = SourceDefinedCursor;
                stream.SupportedSyncModes = stream.SupportedSyncModes.Concat(new[] {SyncMode.incremental}).ToArray();
                stream.DefaultCursorField = CursorField;
            }

            return stream;
        }

        /// <summary>
        /// Override to return the default cursor field used by this stream e.g: an API entity might always use created_at as the cursor field.
        /// </summary>
        /// <returns>The name of the field used as a cursor. If the cursor is nested, return an array consisting of the path to the cursor.</returns>
        public virtual string[] CursorField => Array.Empty<string>();

        /// <summary>
        /// Override to define the slices for this stream. See the stream slicing section of the docs for more information.
        /// </summary>
        public virtual Dictionary<string, object> StreamSlices(SyncMode syncmode, string[] cursorfield,
            JsonElement streamstate) => new();
    }
}
