using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Utils;
using Flurl;
using Flurl.Http;
using Stream = Airbyte.Cdk.Sources.Streams.Stream;

namespace source_jortt
{
    /// <summary>
    /// Jortt is a Dutch accounting package. This source reads the bookkeeping objects of one administration
    /// through the v3 REST API.
    /// </summary>
    public class Jortt : AbstractSource
    {
        public static async Task Main(string[] args) => await AirbyteEntrypoint.Main(args);

        public const string ApiBase = "https://api.jortt.nl";

        public const string TokenEndpoint = "https://app.jortt.nl/oauth-provider/oauth/token";

        /// <summary>
        /// Read-only scopes the source needs. The registered OAuth application must grant them.
        /// </summary>
        public const string DefaultScopes =
            "customers:read invoices:read estimates:read expenses:read organizations:read reconciling:read";

        public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
        {
            exc = null;
            try
            {
                var token = Auth(config).GetAccessToken();
                var organizations = ApiBase
                    .AppendPathSegment("v3/organizations")
                    .WithHeader("Accept", "application/json")
                    .WithOAuthBearerToken(token)
                    .GetAsync()
                    .Result.GetJsonAsync<JsonElement>()
                    .Result;

                var organizationId = Optional(config, "organization_id");
                if (string.IsNullOrWhiteSpace(organizationId))
                    return true;

                var available = organizations.TryGetProperty("data", out var data)
                                && data.ValueKind == JsonValueKind.Array
                                && data.EnumerateArray().Any(x =>
                                    x.TryGetProperty("id", out var id) && id.GetString() == organizationId);

                if (!available)
                    exc = new Exception($"organization_id '{organizationId}' is not available for these credentials");
                return available;
            }
            catch (FlurlHttpException e)
            {
                exc = new Exception($"Jortt API returned {(int?)e.StatusCode}: {e.Message}");
            }
            catch (Exception e)
            {
                exc = e;
            }

            return false;
        }

        public override Stream[] Streams(JsonElement config)
        {
            var startDate = Optional(config, "start_date") ?? "2020-01-01";
            var organizationId = Optional(config, "organization_id");

            // The token is resolved on the first request, so `discover` works without credentials: it only
            // reads the schema files shipped next to the connector.
            var auth = Auth(config);
            var token = new Lazy<string>(auth.GetAccessToken);

            var baseimpl = ApiBase.HttpStream()
                .MaxRetries(3)
                .ShouldRetry(exc => exc.StatusCode is 429 or 500 or 502 or 503)
                .BackoffTime((retryCount, _) => TimeSpan.FromSeconds(Math.Max(5, retryCount * 5)))
                .RequestHeaders((_, _, _) => new Dictionary<string, object>
                {
                    { "Accept", "application/json" },
                    { "Authorization", $"Bearer {token.Value}" },
                    { "Organization-Id", organizationId }
                }.Where(x => !string.IsNullOrWhiteSpace(x.Value?.ToString()))
                    .ToDictionary(x => x.Key, x => x.Value!))
                .ParseResponseArray("$.data[*]")
                .NextPageToken((_, response) => NextPageToken(response));

            return new Stream[]
            {
                List(baseimpl, "v3/organizations", "organizations"),
                List(baseimpl, "v3/customers", "customers"),
                List(baseimpl, "v3/invoices", "invoices"),
                List(baseimpl, "v3/estimates", "estimates"),
                Expenses(baseimpl, startDate),
                List(baseimpl, "v3/receipts", "receipts", ("scope", "all")),
                List(baseimpl, "v3/bank-accounts", "bank_accounts"),
                List(baseimpl, "v3/ledger_accounts/expenses/cost", "ledger_accounts_cost"),
                List(baseimpl, "v3/ledger_accounts/expenses/income", "ledger_accounts_income"),
                List(baseimpl, "v3/ledger_accounts/expenses/balance", "ledger_accounts_balance")
            };
        }

        private static Stream List(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, string path, string name,
            params (string Key, object Value)[] query)
        {
            // Every list endpoint is paged, so the page token always has to reach the request parameters;
            // without that the next-page token repeats forever.
            return builder
                .Path((_, _, _) => path)
                .RequestParams((_, _, nextpagetoken) =>
                {
                    var parameters = PageParameters(nextpagetoken);
                    foreach (var (key, value) in query)
                        parameters[key] = value;
                    return parameters;
                })
                .Create(name);
        }

        /// <summary>
        /// Expenses are the only Jortt object with a date filter, so they are the only incremental stream.
        /// The cursor is the expense VAT date, which is what <c>vat_date_from</c> filters on.
        /// </summary>
        private static Stream Expenses(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, string startDate) =>
            builder
                .Path((_, _, _) => "v3/expenses")
                .CursorField(new[] { "vat_date" })
                .GetUpdatedState((state, record) =>
                {
                    var current = StateDate(state);
                    var latest = Date(record, "vat_date");
                    var cursor = new[] { current, latest }
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .OrderByDescending(x => x, StringComparer.Ordinal)
                        .FirstOrDefault() ?? startDate;

                    return new Dictionary<string, string> { { "vat_date", cursor } }.AsJsonElement();
                })
                .RequestParams((state, _, nextpagetoken) =>
                {
                    var parameters = PageParameters(nextpagetoken);
                    parameters["vat_date_from"] = StateDate(state) ?? startDate;
                    return parameters;
                })
                .Create("expenses");

        private static Dictionary<string, object> PageParameters(Dictionary<string, object> nextpagetoken) =>
            new() { { "page", nextpagetoken != null && nextpagetoken.TryGetValue("page", out var page) ? page : 1 } };

        /// <summary>
        /// Follows <c>_links.next.href</c>. Every list endpoint answers with the same envelope, and endpoints that
        /// do not paginate simply omit the link.
        /// </summary>
        private static Dictionary<string, object> NextPageToken(IFlurlResponse response)
        {
            var body = response.GetStringAsync().Result.AsJsonElement();
            if (!body.TryGetProperty("_links", out var links)
                || !links.TryGetProperty("next", out var next)
                || next.ValueKind != JsonValueKind.Object
                || !next.TryGetProperty("href", out var href)
                || href.ValueKind != JsonValueKind.String)
                return null;

            var page = new Url(href.GetString()!).QueryParams.FirstOrDefault("page")?.ToString();
            return int.TryParse(page, out var number)
                ? new Dictionary<string, object> { { "page", number } }
                : null;
        }

        private static JorttAuth Auth(JsonElement config) =>
            new(TokenEndpoint,
                Required(config, "client_id"),
                Required(config, "client_secret"),
                Optional(config, "scopes") ?? DefaultScopes);

        private static string StateDate(JsonElement state) =>
            state.ValueKind == JsonValueKind.Object ? Date(state, "vat_date") : null;

        private static string Date(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string Required(JsonElement config, string property) =>
            Optional(config, property)
            ?? throw new Exception($"Config property '{property}' is required");

        private static string Optional(JsonElement config, string property) =>
            config.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// OAuth 2.0 client-credentials flow. The access token is cached for the lifetime of the process, which is a
    /// single Airbyte command.
    /// </summary>
    internal sealed class JorttAuth
    {
        private readonly string _tokenEndpoint;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _scopes;
        private string _token;
        private DateTime _expiresAt;

        public JorttAuth(string tokenEndpoint, string clientId, string clientSecret, string scopes)
        {
            _tokenEndpoint = tokenEndpoint;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _scopes = scopes;
        }

        public string GetAccessToken()
        {
            if (_token != null && DateTime.UtcNow < _expiresAt.AddMinutes(-1))
                return _token;

            var response = _tokenEndpoint
                .WithBasicAuth(_clientId, _clientSecret)
                .PostUrlEncodedAsync(new { grant_type = "client_credentials", scope = _scopes })
                .ReceiveJson<JsonElement>()
                .GetAwaiter()
                .GetResult();

            _token = response.GetProperty("access_token").GetString();
            var expiresIn = response.TryGetProperty("expires_in", out var value) && value.TryGetInt32(out var seconds)
                ? seconds
                : 3600;
            _expiresAt = DateTime.UtcNow.AddSeconds(expiresIn);

            return _token ?? throw new Exception("Jortt did not return an access token");
        }
    }
}