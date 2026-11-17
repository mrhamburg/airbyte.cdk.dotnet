#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Streams.Http;
using Airbyte.Cdk.Sources.Streams.Http.Auth;
using Flurl.Http;
using Json.Schema;
using JsonCons.JsonPath;

namespace Airbyte.Cdk.Sources.Utils
{
    public static class FluenStreamBuilderExtension
    {
        public static FluentStreamBuilder HttpStream(this string baseUrl) =>
            new FluentStreamBuilder().UrlBase(() => baseUrl);
    }

    /// <summary>
    /// Immutable fluent builder for an HTTP stream. Each method returns a new builder with one
    /// typed option replaced.
    /// </summary>
    public class FluentStreamBuilder
    {
        private readonly StreamOptions _options;

        public FluentStreamBuilder() : this(new StreamOptions())
        {
        }

        private FluentStreamBuilder(StreamOptions options) => _options = options;

        public string? Name => _options.Name;

        /// <summary>
        /// Override to set different conditions for backoff based on the response from the server.
        /// </summary>
        public FluentStreamBuilder ShouldRetry(Func<FlurlHttpException, bool> func) =>
            new(_options with { ShouldRetry = func });

        /// <summary>
        /// Override this method to dynamically determine backoff time e.g: by reading the X-Retry-After header.
        /// </summary>
        public FluentStreamBuilder BackoffTime(Func<int, IFlurlResponse?, TimeSpan> func) =>
            new(_options with { BackoffTime = func });

        /// <summary>
        /// Override this method to define a pagination strategy.
        /// </summary>
        public FluentStreamBuilder NextPageToken(
            Func<IFlurlRequest, IFlurlResponse, Dictionary<string, object>> func) =>
            new(_options with { NextPageToken = func });

        /// <summary>
        /// Returns the URL path for the API endpoint.
        /// </summary>
        public FluentStreamBuilder Path(Func<JsonElement, Dictionary<string, object>?,
            Dictionary<string, object>?, string> func) =>
            new(_options with { Path = func });

        /// <summary>
        /// Parses the raw response object into a list of records.
        /// </summary>
        public FluentStreamBuilder ParseResponse(
            Func<IFlurlResponse, JsonElement, Dictionary<string, object>?,
                Dictionary<string, object>?, IEnumerable<JsonElement>> func) =>
            new(_options with { ParseResponse = func });

        /// <summary>
        /// The default implementation looks for a JSONSchema file with the same name as this stream.
        /// </summary>
        public FluentStreamBuilder GetJsonSchema(Func<JsonSchema> func) =>
            new(_options with { GetJsonSchema = func });

        /// <summary>
        /// Override to extract state from the latest record. Needed to implement incremental sync.
        /// </summary>
        public FluentStreamBuilder GetUpdatedState(
            Func<JsonElement, JsonElement, JsonElement> func) =>
            new(_options with { GetUpdatedState = func });

        /// <summary>
        /// Override to read records with a custom implementation.
        /// </summary>
        public FluentStreamBuilder ReadRecords(
            Func<AirbyteLogger, SyncMode, ChannelWriter<AirbyteMessage>, JsonElement, long?, string[]?,
                Dictionary<string, object>?, Task<long>> func) =>
            new(_options with { ReadRecords = func });

        /// <summary>
        /// Override when creating POST/PUT/PATCH requests to populate the body with a non-JSON payload.
        /// </summary>
        public FluentStreamBuilder RequestBodyData(
            Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?, string> func) =>
            new(_options with { RequestBodyData = func });

        /// <summary>
        /// Override when creating POST/PUT/PATCH requests to populate the body with a JSON payload.
        /// </summary>
        public FluentStreamBuilder RequestBodyJson(
            Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?, string> func) =>
            new(_options with { RequestBodyJson = func });

        /// <summary>
        /// Override to return any non-auth headers.
        /// </summary>
        public FluentStreamBuilder RequestHeaders(
            Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?,
                Dictionary<string, object>> func) =>
            new(_options with { RequestHeaders = func });

        /// <summary>
        /// Override to define the query parameters for an outgoing HTTP request.
        /// </summary>
        public FluentStreamBuilder RequestParams(
            Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?,
                Dictionary<string, object>> func) =>
            new(_options with { RequestParams = func });

        /// <summary>
        /// Override to define the slices for this stream.
        /// </summary>
        public FluentStreamBuilder StreamSlices(
            Func<SyncMode, string[], JsonElement, Dictionary<string, object>> func) =>
            new(_options with { StreamSlices = func });

        /// <summary>
        /// Override the parse response by parsing an array that can be found using JSONPath.
        /// </summary>
        public FluentStreamBuilder ParseResponseArray(string path) => ParseResponse((response, _, _, _) =>
        {
            var responseobject = response.GetStringAsync().GetAwaiter().GetResult().AsJsonElement();
            var toreturn = new List<JsonElement>();
            foreach (var item in JsonSelector.Select(responseobject, path))
                if (item.ValueKind == JsonValueKind.Array)
                    foreach (var subobject in item.EnumerateArray())
                        toreturn.Add(subobject);

            return toreturn;
        });

        /// <summary>
        /// Override the parse response by parsing an object that can be found using JSONPath.
        /// </summary>
        public FluentStreamBuilder ParseResponseObject(string path) => ParseResponse((response, _, _, _) =>
        {
            var responseobject = response.GetStringAsync().GetAwaiter().GetResult().AsJsonElement();
            var toreturn = new List<JsonElement>();
            foreach (var item in JsonSelector.Select(responseobject, path))
                toreturn.Add(item);

            return toreturn.Count > 0 ? new[] { toreturn.First() } : Array.Empty<JsonElement>();
        });

        /// <summary>
        /// URL base for the API endpoint.
        /// </summary>
        public FluentStreamBuilder UrlBase(Func<string> func) =>
            new(_options with { UrlBase = func });

        /// <summary>
        /// Override if needed. Specifies timeout period for a request.
        /// </summary>
        public FluentStreamBuilder TimeOut(TimeSpan timeout) =>
            new(_options with { TimeOut = timeout });

        /// <summary>
        /// Decides how often to checkpoint state.
        /// </summary>
        public FluentStreamBuilder StateCheckpointInterval(int? interval) =>
            new(_options with { StateCheckpointInterval = interval });

        /// <summary>
        /// Most HTTP streams use a source defined cursor.
        /// </summary>
        public FluentStreamBuilder SourceDefinedCursor(bool value) =>
            new(_options with { SourceDefinedCursor = value });

        /// <summary>
        /// Optional source-defined namespace for this stream.
        /// </summary>
        public FluentStreamBuilder Namespace(string value) =>
            new(_options with { Namespace = value });

        /// <summary>
        /// Use this variable to define page size for API http requests with pagination support.
        /// </summary>
        public FluentStreamBuilder PageSize(int? value) =>
            new(_options with { PageSize = value });

        /// <summary>
        /// Override if needed. See RequestBodyData/RequestBodyJson if using POST/PUT/PATCH.
        /// </summary>
        public FluentStreamBuilder HttpMethod(HttpMethod value) =>
            new(_options with { HttpMethod = value });

        /// <summary>
        /// Override if needed. If set to false, allows opting-out of raising HTTP code exceptions.
        /// </summary>
        public FluentStreamBuilder RaiseOnHttpErrors(bool value) =>
            new(_options with { RaiseOnHttpErrors = value });

        /// <summary>
        /// Override if needed. Specifies maximum amount of retries for the backoff policy.
        /// </summary>
        public FluentStreamBuilder MaxRetries(int value) =>
            new(_options with { MaxRetries = value });

        /// <summary>
        /// Override to return the default cursor field used by this stream.
        /// </summary>
        public FluentStreamBuilder CursorField(string[] value) =>
            new(_options with { CursorField = value });

        /// <summary>
        /// Declares this stream as a substream of another stream.
        /// </summary>
        public FluentStreamBuilder Dependency(SubstreamDependency dependency) =>
            new(_options with { Dependency = dependency });

        /// <summary>
        /// Set the authentication implementation for this stream.
        /// </summary>
        public FluentStreamBuilder WithAuth(AuthBase auth) =>
            new(_options with { Authentication = auth });

        /// <summary>
        /// Have one stream depend on another stream and cache this output as input for the other stream.
        /// </summary>
        public FluentStreamBuilder CacheResultsForStream(string stream, string jsonpath,
            Func<JsonElement[], List<JsonElement>, List<JsonElement>> currentitems) =>
            new(_options with
            {
                CacheRules = _options.CacheRules.Append(new CacheRule(stream, jsonpath, currentitems)).ToArray()
            });

        public GenericStream Create(string name) => new(name, _options with { Name = name });
    }

    /// <summary>
    /// HTTP stream backed by a <see cref="StreamOptions"/> instance. Options that are not set fall
    /// back to the <see cref="HttpStream"/> defaults.
    /// </summary>
    public class GenericStream : HttpStream
    {
        private readonly StreamOptions _options;

        /// <summary>
        /// Receives any dependent elements from this stream.
        /// </summary>
        public Dictionary<string, IReadOnlyList<JsonElement>> CachedElements { get; } = new();

        /// <summary>
        /// Streams the current stream is dependent on.
        /// </summary>
        public string[] CachedFor { get; }

        public GenericStream(string name, StreamOptions options)
        {
            _givenName = name;
            _options = options;
            WithAuth(options.Authentication);
            CachedFor = options.CacheRules.Select(x => x.Stream).ToArray();
        }

        public override bool SourceDefinedCursor =>
            _options.SourceDefinedCursor ?? base.SourceDefinedCursor;

        public override string? Namespace => _options.Namespace ?? base.Namespace;

        public override int? PageSize => _options.PageSize ?? base.PageSize;

        public override HttpMethod HttpMethod => _options.HttpMethod ?? base.HttpMethod;

        public override int? StateCheckpointInterval =>
            _options.StateCheckpointInterval ?? base.StateCheckpointInterval;

        public override int MaxRetries => _options.MaxRetries ?? base.MaxRetries;

        public override string[] CursorField => _options.CursorField ?? base.CursorField;

        public override SubstreamDependency? Dependency => _options.Dependency ?? base.Dependency;

        public override bool RaiseOnHttpErrors => _options.RaiseOnHttpErrors ?? base.RaiseOnHttpErrors;

        public override TimeSpan TimeOut => _options.TimeOut ?? base.TimeOut;

        public override TimeSpan BackoffTime(int retryCount, IFlurlResponse? response) =>
            _options.BackoffTime?.Invoke(retryCount, response) ?? base.BackoffTime(retryCount, response);

        public override string UrlBase() =>
            _options.UrlBase?.Invoke() ?? throw new InvalidOperationException("UrlBase is required.");

        public override Dictionary<string, object> NextPageToken(IFlurlRequest request, IFlurlResponse response) =>
            _options.NextPageToken?.Invoke(request, response) ?? new Dictionary<string, object>();

        public override string Path(JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
            _options.Path?.Invoke(streamstate, streamslice, nextpagetoken) ?? base.Path(streamstate, streamslice, nextpagetoken);

        public override IEnumerable<JsonElement> ParseResponse(IFlurlResponse response,
            JsonElement streamstate, Dictionary<string, object>? streamslice = null,
            Dictionary<string, object>? nextpagetoken = null)
        {
            var results = _options.ParseResponse?.Invoke(response, streamstate, streamslice, nextpagetoken)
                          ?? throw new InvalidOperationException("ParseResponse is required.");

            var jsonElements = results as JsonElement[] ?? results.ToArray();
            foreach (var rule in _options.CacheRules)
                foreach (var record in jsonElements)
                {
                    if (!CachedElements.ContainsKey(rule.Stream))
                        CachedElements.Add(rule.Stream, new List<JsonElement>());

                    CachedElements[rule.Stream] = rule.Merge(
                        JsonSelector.Select(record, rule.JsonPath).ToArray(),
                        CachedElements[rule.Stream].ToList());
                }

            return jsonElements;
        }

        public override bool ShouldRetry(FlurlHttpException exc) =>
            _options.ShouldRetry?.Invoke(exc) ?? base.ShouldRetry(exc);

        public override JsonSchema GetJsonSchema() =>
            _options.GetJsonSchema?.Invoke() ?? base.GetJsonSchema();

        public override JsonElement GetUpdatedState(JsonElement currentstreamstate,
            JsonElement latestrecord) =>
            _options.GetUpdatedState?.Invoke(currentstreamstate, latestrecord)
            ?? base.GetUpdatedState(currentstreamstate, latestrecord);

        public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
            ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
            string[]? cursorfield = null, Dictionary<string, object>? streamslice = null) =>
            _options.ReadRecords?.Invoke(logger, syncMode, streamchannel, streamstate, recordlimit, cursorfield,
                streamslice)
            ?? base.ReadRecords(logger, syncMode, streamchannel, streamstate, recordlimit, cursorfield, streamslice);

        public override string RequestBodyData(JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
            _options.RequestBodyData?.Invoke(streamstate, streamslice, nextpagetoken)
            ?? base.RequestBodyData(streamstate, streamslice, nextpagetoken);

        public override string RequestBodyJson(JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
            _options.RequestBodyJson?.Invoke(streamstate, streamslice, nextpagetoken)
            ?? base.RequestBodyJson(streamstate, streamslice, nextpagetoken);

        public override Dictionary<string, object> RequestHeaders(JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
            _options.RequestHeaders?.Invoke(streamstate, streamslice, nextpagetoken)
            ?? base.RequestHeaders(streamstate, streamslice, nextpagetoken);

        public override Dictionary<string, object> RequestParams(JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
            _options.RequestParams?.Invoke(streamstate, streamslice, nextpagetoken)
            ?? base.RequestParams(streamstate, streamslice, nextpagetoken);

        public override Dictionary<string, object> StreamSlices(SyncMode syncmode,
            string[] cursorfield, JsonElement streamstate) =>
            _options.StreamSlices?.Invoke(syncmode, cursorfield, streamstate)
            ?? base.StreamSlices(syncmode, cursorfield, streamstate);
    }
}
