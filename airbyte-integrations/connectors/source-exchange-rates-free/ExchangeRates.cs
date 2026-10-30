using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Streams;
using Airbyte.Cdk.Sources.Utils;
using Flurl;
using Flurl.Http;

namespace source_exchange_rates
{
    public class ExchangeRates : AbstractSource
    {
        public static async Task Main(string[] args) => await AirbyteEntrypoint.Main(args);

        public string UrlBase => "https://api.frankfurter.dev/v1";

        public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
        {
            exc = null;
            try
            {
                var response = UrlBase.AppendPathSegment("latest")
                    .SetQueryParam("base", "USD")
                    .GetAsync()
                    .Result;

                return response.StatusCode == 200;
            }
            catch (Exception e)
            {
                exc = e;
            }

            return false;
        }

        public override Stream[] Streams(JsonElement config)
        {
            var baseimpl = UrlBase.HttpStream()
                .ParseResponseObject("$")
                .MaxRetries(3)
                .ShouldRetry(exc => exc.StatusCode == 429 || (exc.StatusCode >= 500 && exc.StatusCode < 600))
                .BackoffTime((retryCount, _) => TimeSpan.FromSeconds(Math.Max(5, retryCount * 5)));

            var configuredSymbols = config.TryGetProperty("base", out var baseElement)
                ? (baseElement.GetString() ?? string.Empty)
                    .Split(",")
                    .Select(x => x.Replace(" ", "").ToUpper())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray()
                : Array.Empty<string>();
            var baseCurrency = configuredSymbols.FirstOrDefault() ?? "EUR";

            var symbols = baseimpl
                .Path((_, _, _) => "currencies")
                .Create("symbols");

            var latest = baseimpl
                .Path((_, _, _) => "latest")
                .RequestParams((_, _, _) => new Dictionary<string, object> { { "base", baseCurrency } })
                .Create("latest");

            DateTime? lastDateTime = null;
            var currentState = new Dictionary<string, DateTime>();

            var historical = baseimpl
                .CursorField(new[] { "date" })
                .BackoffTime((i, _) => TimeSpan.FromSeconds(Math.Max(5, i * 5)))
                .GetUpdatedState((_, _) => currentState.AsJsonElement())
                .NextPageToken((request, _) =>
                {
                    var lastSegment = request.Url.PathSegments.LastOrDefault();
                    if (lastSegment == null || !DateTime.TryParse(lastSegment, out var date))
                        return null;

                    var symbol = request.Url.QueryParams.FirstOrDefault("base")?.ToString() ?? baseCurrency;
                    if (date >= DateTime.Today.AddDays(-1))
                    {
                        var symbolsToSync = configuredSymbols.Length > 0
                            ? configuredSymbols.ToList()
                            : new List<string> { "EUR" };
                        var symbolIndex = symbolsToSync.FindIndex(x => x == symbol) + 1;
                        if (symbolIndex >= symbolsToSync.Count)
                            return null;

                        symbol = symbolsToSync[symbolIndex];
                        lastDateTime = null;
                    }
                    else
                    {
                        currentState[symbol] = date;
                    }

                    return new Dictionary<string, object> { { "symbol", symbol } };
                })
                .Path((state, _, nextpagetoken) =>
                {
                    var inputState = state.ToType<Dictionary<string, DateTime>>();
                    if (inputState != null && inputState.Count > 0 && currentState.Count == 0)
                        currentState = inputState;

                    if (nextpagetoken != null &&
                        currentState.ContainsKey(nextpagetoken["symbol"].ToString()))
                    {
                        var date = currentState[nextpagetoken["symbol"].ToString()].AddDays(1);
                        if (lastDateTime.HasValue && lastDateTime >= date)
                            date = lastDateTime.Value.AddDays(1);

                        lastDateTime = date;
                        return date.ToString("yyyy-MM-dd");
                    }

                    if (nextpagetoken == null && currentState.Count > 0)
                        return currentState.Min(x => x.Value).ToString("yyyy-MM-dd");

                    return config.TryGetProperty("start_date", out var startDate)
                        ? startDate.GetString()
                        : "2008-01-01";
                })
                .RequestParams((_, _, nextpagetoken) => new Dictionary<string, object>
                {
                    { "base", nextpagetoken != null ? nextpagetoken["symbol"] : baseCurrency }
                })
                .Create("historical");

            return new Stream[] { symbols, latest, historical };
        }
    }
}
