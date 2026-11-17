#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Utils;
using Flurl.Http;

namespace Airbyte.Cdk.Sources.Streams.Http
{
    /// <summary>
    /// Runs the pagination and checkpoint loop for an <see cref="HttpStream"/>. The stream owns the
    /// request shape; this class owns the read loop.
    /// </summary>
    internal static class HttpRecordReader
    {
        public static async Task<long> ReadAsync(HttpStream stream, AirbyteLogger logger, SyncMode syncMode,
            ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
            string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
        {
            bool paginationcompleted = false;
            Dictionary<string, object>? nextpagetoken = null;
            long recordcount = 0;
            long recordsSinceState = 0;
            JsonElement lastitem = "{}".AsJsonElement();
            bool Limitreached() => recordlimit.HasValue && recordcount >= recordlimit.Value;

            async Task UpdateState(JsonElement lastrecord)
            {
                var updatedstate = syncMode == SyncMode.incremental
                    ? stream.GetUpdatedState(streamstate, lastrecord)
                    : streamstate;
                logger.Info($"Setting state of {stream.Name} stream to {updatedstate.GetRawText()}");
                var emitted = await stream.StateEmitter!.EmitAsync(
                    new StreamDescriptor { Name = stream.Name, Namespace = stream.Namespace },
                    updatedstate, recordsSinceState);
                if (emitted)
                    recordsSinceState = 0;
                streamstate = updatedstate;
            }

            while (!paginationcompleted)
            {
                var requestheaders = stream.RequestHeaders(streamstate, streamslice, nextpagetoken);
                var path = stream.Path(streamstate, streamslice, nextpagetoken);
                var parameters = stream.RequestParams(streamstate, streamslice, nextpagetoken);
                var requestjson = stream.RequestBodyJson(streamstate, streamslice, nextpagetoken);
                var requestBodyData = stream.RequestBodyData(streamstate, streamslice, nextpagetoken);
                (IFlurlRequest request, IFlurlResponse response) result = await stream.Send(path, requestheaders,
                    parameters, requestjson, requestBodyData);

                foreach (var item in stream.ParseResponse(result.response, streamstate, streamslice, nextpagetoken))
                {
                    await streamchannel.WriteAsync(AbstractSource.AsAirbyteMessage(stream.Name, stream.Namespace, item));
                    recordcount++;
                    recordsSinceState++;
                    lastitem = item;

                    if (Limitreached())
                        break;

                    if (syncMode == SyncMode.incremental && stream.StateCheckpointInterval.HasValue)
                    {
                        if (stream.StateCheckpointInterval.Value <= 0)
                            throw new InvalidOperationException(
                                "StateCheckpointInterval must be greater than zero.");

                        if (recordcount % stream.StateCheckpointInterval.Value == 0)
                            await UpdateState(item);
                    }
                }

                nextpagetoken = stream.NextPageToken(result.request, result.response);
                paginationcompleted = (nextpagetoken == null || nextpagetoken.Count == 0) || Limitreached();
                if (paginationcompleted && (syncMode != SyncMode.incremental || recordcount > 0))
                    await UpdateState(lastitem);
            }

            return recordcount;
        }
    }
}
