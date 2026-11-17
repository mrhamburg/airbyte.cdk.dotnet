#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Streams.Http.Auth;
using Airbyte.Cdk.Sources.Utils;
using Flurl.Http;

namespace Airbyte.Cdk.Sources.Streams.Http
{
    /// <summary>
    /// Base abstract class for an Airbyte Stream using the HTTP protocol. Basic building block for users building an Airbyte source for a HTTP API.
    /// </summary>
    public abstract class HttpStream : Stream
    {
        /// <summary>
        /// Get the latest response from the api stream
        /// </summary>
        protected IFlurlResponse? LastFlurlResponse { get; private set; }

        /// <summary>
        /// Currently used sync mode
        /// </summary>
        protected SyncMode SyncMode { get; private set; }

        /// <summary>
        /// Most HTTP streams use a source defined cursor (i.e: the user can't configure it like on a SQL table)
        /// </summary>
        public override bool SourceDefinedCursor { get; protected set; } = true;

        /// <summary>
        /// Use this variable to define page size for API http requests with pagination support
        /// </summary>
        public virtual int? PageSize { get; set; } = null;

        /// <summary>
        /// Set auth implementation to be used for each request
        /// </summary>
        private AuthBase? AuthImplementation { get; set; }

        /// <summary>
        /// Set the authentication implementation for this stream
        /// </summary>
        public void WithAuth(AuthBase? auth) => AuthImplementation = auth;

        /// <summary>
        /// URL base for the  API endpoint e.g: if you wanted to hit https://myapi.com/v1/some_entity then this should return "https://myapi.com/v1/"
        /// </summary>
        public abstract string UrlBase();

        /// <summary>
        /// Override if needed. See get_request_data/get_request_json if using POST/PUT/PATCH.
        /// </summary>
        public virtual HttpMethod HttpMethod => HttpMethod.Get;

        /// <summary>
        /// Override if needed. If set to False, allows opting-out of raising HTTP code exception.
        /// </summary>
        public virtual bool RaiseOnHttpErrors => true;

        /// <summary>
        /// Override if needed. Specifies maximum amount of retries for backoff policy. Return 0 for no limit.
        /// </summary>
        public virtual int MaxRetries => 5;

        /// <summary>
        /// Override if needed. Specifies timeout period for a request.
        /// </summary>
        public virtual TimeSpan TimeOut => TimeSpan.FromMinutes(15);

        /// <summary>
        /// Override this method to define a pagination strategy.
        /// The value returned from this method is passed to most other methods in this class. Use it to form a request e.g: set headers or query params.
        /// </summary>
        /// <returns>The token for the next page from the input response object. Returning None means there are no more pages to read in this response.</returns>
        public abstract Dictionary<string, object> NextPageToken(IFlurlRequest request, IFlurlResponse response);

        /// <summary>
        /// Returns the URL path for the API endpoint e.g: if you wanted to hit https://myapi.com/v1/some_entity then this should return "some_entity"
        /// Defaults to {UrlBase}/{Name} where Name is the name of this stream
        /// </summary>
        public virtual string Path(JsonElement streamstate, Dictionary<string, object>? streamslice = null,
            Dictionary<string, object>? nextpagetoken = null) =>
            Name.ToLower();

        /// <summary>
        /// Override this method to define the query parameters that should be set on an outgoing HTTP request given the inputs.
        /// E.g: you might want to define query parameters for paging if next_page_token is not None.
        /// </summary>
        public virtual Dictionary<string, object> RequestParams(JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
            new();

        /// <summary>
        /// Override to return any non-auth headers. Authentication headers will overwrite any overlapping headers returned from this method.
        /// </summary>
        public virtual Dictionary<string, object> RequestHeaders(JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null) =>
            new();

        /// <summary>
        /// Override when creating POST/PUT/PATCH requests to populate the body of the request with a non-JSON payload.
        /// 
        /// If returns a ready text that it will be sent as is.
        /// If returns a dict that it will be converted to a urlencoded form.
        /// E.g. {"key1": "value1", "key2": "value2"} =&gt; "key1=value1&amp;key2=value2"
        /// 
        /// At the same time only one of the 'request_body_data' and 'request_body_json' functions can be overridden.
        /// </summary>
        public virtual string RequestBodyData(JsonElement streamstate, Dictionary<string, object>? streamslice = null,
            Dictionary<string, object>? nextpagetoken = null) =>
            string.Empty;

        /// <summary>
        /// Override when creating POST/PUT/PATCH requests to populate the body of the request with a JSON payload.
        /// At the same time only one of the 'request_body_data' and 'request_body_json' functions can be overridden.
        /// </summary>
        public virtual string RequestBodyJson(JsonElement streamstate, Dictionary<string, object>? streamslice = null,
            Dictionary<string, object>? nextpagetoken = null) =>
            string.Empty;

        /// <summary>
        /// Parses the raw response object into a list of records.
        /// By default, this returns an iterable containing the input. Override to parse differently.
        /// </summary>
        public abstract IEnumerable<JsonElement> ParseResponse(IFlurlResponse response, JsonElement streamstate,
            Dictionary<string, object>? streamslice = null, Dictionary<string, object>? nextpagetoken = null);

        /// <summary>
        /// Override to set different conditions for backoff based on the response from the server.
        /// 
        /// By default, back off on the following HTTP response statuses:
        /// - 429 (Too Many Requests) indicating rate limiting
        /// - 500s to handle transient server errors
        /// 
        /// Unexpected but transient exceptions (connection timeout, DNS resolution failed, etc..) are retried by default.
        /// </summary>
        public virtual bool ShouldRetry(FlurlHttpException exc) => IsRetryableStatus(exc.StatusCode);

        /// <summary>
        /// Returns true for transient failures the retry policy may recover from: rate limits,
        /// server errors, and connection failures that have no HTTP status.
        /// </summary>
        public static bool IsRetryableStatus(int? statusCode) =>
            statusCode is null or 429 or (>= 500 and < 600);

        /// <summary>
        /// Override this method to dynamically determine backoff time e.g: by reading the X-Retry-After header.
        /// This method is called only if ShouldBackoff returns True for the input request.
        /// </summary>
        /// <returns>how long to backoff in seconds. The return value may be a floating point number for subsecond precision. Returning None defers backoff to the default backoff behavior (e.g using an exponential algorithm).</returns>
        public virtual TimeSpan BackoffTime(int retryCount, IFlurlResponse? response) =>
            response?.Headers.TryGetFirst("X-Retry-After", out string retryafter) == true
                ? TimeSpan.FromSeconds(double.TryParse(retryafter, out var result) ? result : 5)
                : TimeSpan.FromSeconds(5) * retryCount;

        /// <summary>
        /// Process and send the request to the endpoint as set by the urlbase, includes all configuration as set in this stream
        /// </summary>
        public virtual async Task<(IFlurlRequest, IFlurlResponse)> Send(string path,
            Dictionary<string, object>? headers = null, Dictionary<string, object>? parameters = null,
            string json = "", string data = "")
        {
            var result = await new HttpRequestExecutor(this).SendAsync(path, headers, parameters, json, data);
            LastFlurlResponse = result.Item2;
            return result;
        }

        /// <summary>
        /// Builds the request body with the media type that matches the payload.
        /// </summary>
        public static HttpContent CreateBodyContent(string content, bool isJson) =>
            HttpRequestExecutor.CreateBodyContent(content, isJson);

        internal Task ApplyAuthAsync(IFlurlRequest request) =>
            AuthImplementation?.ApplyAsync(request) ?? Task.CompletedTask;

        /// <summary>
        /// Start reading records
        /// </summary>
        public override Task<long> ReadRecords(AirbyteLogger logger, SyncMode syncMode,
            ChannelWriter<AirbyteMessage> streamchannel, JsonElement streamstate, long? recordlimit = null,
            string[]? cursorfield = null, Dictionary<string, object>? streamslice = null)
        {
            SyncMode = syncMode;
            Logger = logger;
            StateEmitter ??= new StateEmitter(streamchannel);
            return HttpRecordReader.ReadAsync(this, logger, syncMode, streamchannel, streamstate, recordlimit,
                cursorfield, streamslice);
        }
    }
}