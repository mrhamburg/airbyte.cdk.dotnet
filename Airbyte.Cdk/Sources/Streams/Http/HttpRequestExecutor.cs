#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Flurl;
using Flurl.Http;
using Polly;
using Polly.Retry;

namespace Airbyte.Cdk.Sources.Streams.Http
{
    /// <summary>
    /// Builds and executes one HTTP request, including authentication, retries, and backoff. The
    /// stream supplies the policy decisions; this class owns the transport mechanics.
    /// </summary>
    internal sealed class HttpRequestExecutor
    {
        private readonly HttpStream _stream;

        public HttpRequestExecutor(HttpStream stream) => _stream = stream;

        public async Task<(IFlurlRequest, IFlurlResponse)> SendAsync(string path,
            Dictionary<string, object>? headers = null, Dictionary<string, object>? parameters = null,
            string json = "", string data = "")
        {
            Url requestUrl = _stream.UrlBase().AppendPathSegment(path);
            IFlurlRequest request = requestUrl.WithTimeout(_stream.TimeOut);

            if (headers?.Count > 0)
                request = headers.Aggregate(request,
                    (current, header) => current.WithHeader(header.Key, header.Value));
            if (parameters?.Count > 0)
                request = parameters.Aggregate(request,
                    (current, @params) => current.SetQueryParam(@params.Key, @params.Value));

            if (!string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(data))
                throw new InvalidOperationException(
                    "At the same time only one of the 'RequestBodyData' and 'RequestBodyJson' functions can return data");

            string stringContent = !string.IsNullOrWhiteSpace(json) ? json :
                !string.IsNullOrWhiteSpace(data) ? data : string.Empty;

            await _stream.ApplyAuthAsync(request);

            Func<Task<IFlurlResponse>> requestFunc =
                new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Post }.Contains(_stream.HttpMethod)
                    ? !string.IsNullOrWhiteSpace(stringContent)
                        ? async () => await request.SendAsync(_stream.HttpMethod,
                            CreateBodyContent(stringContent, !string.IsNullOrWhiteSpace(json)))
                        : async () => await request.SendAsync(_stream.HttpMethod)
                    : async () => await request.GetAsync();

            try
            {
                return await GetRetryPolicy()
                    .ExecuteAsync(async () =>
                    {
                        var response = await requestFunc();
                        return (request, response);
                    });
            }
            catch (Exception e)
            {
                if (!_stream.RaiseOnHttpErrors && e is FlurlHttpException { Call.Response: { } errorResponse })
                    return (request, errorResponse);

                if (e is FlurlHttpException flurlHttpException)
                    _stream.Logger.Info($"Giving up for returned HTTP status: {flurlHttpException.StatusCode}");
                throw;
            }
        }

        /// <summary>
        /// Builds the request body with the media type that matches the payload.
        /// </summary>
        public static HttpContent CreateBodyContent(string content, bool isJson) =>
            new StringContent(content, System.Text.Encoding.UTF8,
                isJson ? "application/json" : "application/x-www-form-urlencoded");

        private AsyncRetryPolicy GetRetryPolicy() =>
            Policy.Handle<FlurlHttpException>(_stream.ShouldRetry)
                .WaitAndRetryAsync(_stream.MaxRetries,
                    (int retryCount, Exception exception, Context _) => _stream.BackoffTime(retryCount,
                        exception is FlurlHttpException flurlException
                            ? flurlException.Call.Response
                            : null),
                    (Exception exc, TimeSpan span, int count, Context _) =>
                    {
                        _stream.Logger.Info(
                            $"Caught retryable error '{exc.Message}' after {count} tries. Waiting {Math.Round(span.TotalSeconds)} seconds then retrying...");
                        return Task.CompletedTask;
                    });
    }
}
