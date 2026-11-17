#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using System.Threading.Tasks;
using Airbyte.Cdk.Models;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Streams.Http.Auth;
using Flurl.Http;
using Json.Schema;

namespace Airbyte.Cdk.Sources.Utils
{
    /// <summary>
    /// A typed cache rule for one stream that is read from another stream's records.
    /// </summary>
    public sealed record CacheRule(
        string Stream,
        string JsonPath,
        Func<JsonElement[], List<JsonElement>, List<JsonElement>> Merge);

    /// <summary>
    /// Immutable configuration collected by <see cref="FluentStreamBuilder"/>. Every option is
    /// strongly typed, so a missing or wrongly typed option is a compile-time or construction-time
    /// error instead of a runtime cast failure.
    /// </summary>
    public sealed record StreamOptions
    {
        public string? Name { get; init; }
        public AuthBase? Authentication { get; init; }
        public Func<string>? UrlBase { get; init; }
        public Func<FlurlHttpException, bool>? ShouldRetry { get; init; }
        public Func<int, IFlurlResponse?, TimeSpan>? BackoffTime { get; init; }
        public Func<IFlurlRequest, IFlurlResponse, Dictionary<string, object>>? NextPageToken { get; init; }

        public Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?, string>? Path
        {
            get;
            init;
        }

        public Func<IFlurlResponse, JsonElement, Dictionary<string, object>?, Dictionary<string, object>?, IEnumerable<JsonElement>>?
            ParseResponse { get; init; }

        public Func<JsonSchema>? GetJsonSchema { get; init; }
        public Func<JsonElement, JsonElement, JsonElement>? GetUpdatedState { get; init; }

        public Func<AirbyteLogger, SyncMode, ChannelWriter<AirbyteMessage>, JsonElement, long?, string[]?,
            Dictionary<string, object>?, Task<long>>? ReadRecords { get; init; }

        public Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?, string>? RequestBodyData
        {
            get;
            init;
        }

        public Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?, string>? RequestBodyJson
        {
            get;
            init;
        }

        public Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?,
            Dictionary<string, object>>? RequestHeaders { get; init; }

        public Func<JsonElement, Dictionary<string, object>?, Dictionary<string, object>?,
            Dictionary<string, object>>? RequestParams { get; init; }

        public Func<SyncMode, string[], JsonElement, Dictionary<string, object>>? StreamSlices { get; init; }

        public IReadOnlyList<CacheRule> CacheRules { get; init; } = Array.Empty<CacheRule>();

        public bool? SourceDefinedCursor { get; init; }
        public string? Namespace { get; init; }
        public int? PageSize { get; init; }
        public HttpMethod? HttpMethod { get; init; }
        public int? StateCheckpointInterval { get; init; }
        public int? MaxRetries { get; init; }
        public string[]? CursorField { get; init; }
        public SubstreamDependency? Dependency { get; init; }
        public bool? RaiseOnHttpErrors { get; init; }
        public TimeSpan? TimeOut { get; init; }
    }
}
