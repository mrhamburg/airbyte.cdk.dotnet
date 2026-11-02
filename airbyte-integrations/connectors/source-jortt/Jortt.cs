using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk;
using Airbyte.Cdk.Sources;
using Airbyte.Cdk.Sources.Streams;
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
            "customers:read invoices:read estimates:read expenses:read organizations:read reconciling:read payroll:read";

        /// <summary>
        /// Expenses entered late carry an old VAT date, so the incremental filter deliberately reaches back
        /// behind the cursor. Override with the <c>lookback_days</c> config property.
        /// </summary>
        public const int DefaultLookbackDays = 7;

        private const string DateFormat = "yyyy-MM-dd";

        public override bool CheckConnection(AirbyteLogger logger, JsonElement config, out Exception exc)
        {
            exc = null;

            string token;
            try
            {
                token = Auth(config).GetAccessToken();
            }
            catch (Exception e)
            {
                exc = new Exception("Could not obtain a Jortt access token: " + Describe(e));
                return false;
            }

            List<string> organizationIds;
            try
            {
                organizationIds = Organizations(token);
            }
            catch (Exception e)
            {
                exc = new Exception("Could not read the Jortt organizations: " + Describe(e));
                return false;
            }

            var organizationId = Optional(config, "organization_id");
            if (string.IsNullOrWhiteSpace(organizationId))
            {
                // Every other stream would fail with organization.header_required, so a green check here would
                // be a lie.
                if (organizationIds.Count > 1)
                {
                    exc = new Exception(
                        "These credentials can act on more than one Jortt administration. Set organization_id to one " +
                        "of: " + string.Join(", ", organizationIds));
                    return false;
                }

                return true;
            }

            if (organizationIds.Contains(organizationId))
                return true;

            exc = new Exception(
                $"organization_id '{organizationId}' is not available for these credentials. Available: " +
                string.Join(", ", organizationIds));
            return false;
        }

        public override Stream[] Streams(JsonElement config)
        {
            var organizationId = Optional(config, "organization_id");
            var startDate = ParseDate(Optional(config, "start_date"));
            var lookbackDays = OptionalInt(config, "lookback_days") ?? DefaultLookbackDays;

            // No request is made here, so `discover` works with placeholder credentials: it only reads the
            // schema files shipped next to the connector.
            var auth = Auth(config);

            var listBase = Common(ApiBase.HttpStream(), auth, organizationId)
                .ParseResponse((response, _, _, _) => ParseData(response))
                .NextPageToken((_, response) => NextPageToken(response));

            // Period driven streams walk periods themselves, so they must not inherit the link based pagination.
            var periodBase = Common(ApiBase.HttpStream(), auth, organizationId)
                .ParseResponse((response, _, _, _) => ParseData(response));

            var objectBase = Common(ApiBase.HttpStream(), auth, organizationId)
                .ParseResponse((response, _, _, _) => ParseObjectData(response));

            return new Stream[]
            {
                Paged(listBase, "v3/organizations", "organizations", false),
                Paged(listBase, "v3/customers", "customers"),
                Paged(listBase, "v3/invoices", "invoices"),
                Paged(listBase, "v3/estimates", "estimates"),
                Expenses(listBase, startDate, lookbackDays),
                Paged(listBase, "v3/receipts", "receipts", false, ("scope", "all")),
                Paged(listBase, "v3/bank-accounts", "bank_accounts", false),
                Paged(listBase, "v3/ledger_accounts/expenses/cost", "ledger_accounts_cost", false),
                Paged(listBase, "v3/ledger_accounts/expenses/income", "ledger_accounts_income", false),
                Paged(listBase, "v3/ledger_accounts/expenses/balance", "ledger_accounts_balance", false),

                Paged(listBase, "v3/projects", "projects"),
                Paged(listBase, "v3/labels", "labels", false),

                Substream(listBase, "projects", "id", "project_id", "v3/projects/{project_id}/line_items",
                    "project_line_items"),
                Substream(listBase, "bank_accounts", "id", "bank_account_id",
                    "v3/bank-accounts/{bank_account_id}/bank-transactions", "bank_transactions"),
                Substream(listBase, "invoices", "id", "invoice_id", "v3/payments/{invoice_id}", "invoice_payments",
                    false),
                Substream(listBase, "expenses", "id", "expense_id", "v3/payments/{expense_id}", "expense_payments",
                    false),
                Yearly(periodBase, "v3/loonjournaalposten", "loonjournaalposten", startDate),

                Single(objectBase, "v3/organizations/me", "organization"),

                Monthly(objectBase, "v3/reports/balance", "report_balance", startDate),
                Monthly(objectBase, "v3/reports/profit-and-loss", "report_profit_and_loss", startDate),
                Single(objectBase, "v3/reports/summaries/balance", "report_summary_balance"),
                Single(objectBase, "v3/reports/summaries/btw", "report_summary_btw"),
                Single(objectBase, "v3/reports/summaries/cash_and_bank", "report_summary_cash_and_bank"),
                Single(objectBase, "v3/reports/summaries/invoices", "report_summary_invoices"),
                Single(objectBase, "v3/reports/summaries/profit_and_loss", "report_summary_profit_and_loss")
            };
        }

        private static Airbyte.Cdk.Sources.Utils.FluentStreamBuilder Common(
            Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, JorttAuth auth, string organizationId) =>
            builder
                .MaxRetries(5)
                .ShouldRetry(IsTransient)
                .BackoffTime((retryCount, _) => TimeSpan.FromSeconds(Math.Max(5, retryCount * 5)))
                .RequestHeaders((_, _, _) => Headers(auth, organizationId));

        /// <summary>
        /// A list endpoint. <paramref name="paged"/> is false for the endpoints Jortt does not paginate: they
        /// reject nothing, but there is no page parameter to send.
        /// </summary>
        private static Stream Paged(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, string path, string name,
            bool paged = true, params (string Key, object Value)[] query)
        {
            return builder
                .Path((_, _, _) => path)
                .RequestParams((_, _, nextpagetoken) =>
                {
                    var parameters = paged ? PageParameters(nextpagetoken) : new Dictionary<string, object>();
                    foreach (var (key, value) in query)
                        parameters[key] = value;
                    return parameters;
                })
                .Create(name);
        }

        /// <summary>
        /// A collection that only exists below a parent object, read once per parent record. The parent key is
        /// handed to the path through the stream slice.
        /// </summary>
        private static Stream Substream(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, string parent,
            string parentKeyField, string sliceParameter, string pathTemplate, string name, bool paged = true)
        {
            return builder
                .Path((_, streamSlice, _) => pathTemplate.Replace("{" + sliceParameter + "}",
                    streamSlice != null && streamSlice.TryGetValue(sliceParameter, out var key)
                        ? key?.ToString() ?? string.Empty
                        : string.Empty))
                .Dependency(new SubstreamDependency(parent, parentKeyField, sliceParameter))
                .RequestParams((_, _, nextpagetoken) =>
                    paged ? PageParameters(nextpagetoken) : new Dictionary<string, object>())
                .Create(name);
        }

        /// <summary>
        /// An endpoint that answers with a single object in <c>data</c>.
        /// </summary>
        private static Stream Single(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, string path, string name) =>
            builder.Path((_, _, _) => path).Create(name);

        /// <summary>
        /// A report for one calendar month per record, walking from the start date (or January of the current
        /// year) through the current month.
        /// </summary>
        private static Stream Monthly(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, string path,
            string name, DateTime? startDate)
        {
            var first = startDate.HasValue
                ? new DateTime(startDate.Value.Year, startDate.Value.Month, 1)
                : new DateTime(DateTime.UtcNow.Year, 1, 1);
            var last = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);

            return builder
                .Path((_, _, _) => path)
                .RequestParams((_, _, nextpagetoken) =>
                {
                    var period = nextpagetoken == null ? first : Period(nextpagetoken, first);
                    return new Dictionary<string, object> { { "year", period.Year }, { "month", period.Month } };
                })
                .NextPageToken((request, _) => NextPeriod(request, last, months: 1))
                .Create(name);
        }

        /// <summary>
        /// Payroll journal entries are filtered by year, so one request covers a whole year.
        /// </summary>
        private static Stream Yearly(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, string path,
            string name, DateTime? startDate)
        {
            var first = (startDate ?? new DateTime(DateTime.UtcNow.Year, 1, 1)).Year;
            var last = DateTime.UtcNow.Year;

            return builder
                .Path((_, _, _) => path)
                .RequestParams((_, _, nextpagetoken) => new Dictionary<string, object>
                {
                    { "year", nextpagetoken != null && nextpagetoken.TryGetValue("year", out var year) ? year : first }
                })
                .NextPageToken((request, _) => NextYear(request, last))
                .Create(name);
        }

        private static DateTime Period(Dictionary<string, object> token, DateTime fallback) =>
            token.TryGetValue("year", out var year) && int.TryParse(year?.ToString(), out var y)
            && token.TryGetValue("month", out var month) && int.TryParse(month?.ToString(), out var m)
                ? new DateTime(y, m, 1)
                : fallback;

        private static Dictionary<string, object> NextPeriod(IFlurlRequest request, DateTime last, int months)
        {
            var year = QueryInt(request, "year");
            var month = QueryInt(request, "month");
            if (year == null || month == null)
                return null;

            var next = new DateTime(year.Value, month.Value, 1).AddMonths(months);
            return next > last
                ? null
                : new Dictionary<string, object> { { "year", next.Year }, { "month", next.Month } };
        }

        private static Dictionary<string, object> NextYear(IFlurlRequest request, int last)
        {
            var year = QueryInt(request, "year");
            if (year == null)
                return null;

            var next = year.Value + 1;
            return next > last ? null : new Dictionary<string, object> { { "year", next } };
        }

        private static int? QueryInt(IFlurlRequest request, string name)
        {
            var value = request.Url.QueryParams.FirstOrDefault(name)?.ToString();
            return int.TryParse(value, out var parsed) ? parsed : null;
        }

        private static Stream Expenses(Airbyte.Cdk.Sources.Utils.FluentStreamBuilder builder, DateTime? startDate,
            int lookbackDays)
        {
            // The API does not document a sort order, and the CDK only hands the last record of a page to
            // GetUpdatedState, so the stream tracks the highest VAT date it has seen instead of trusting order.
            var seen = new CursorTracker();

            return builder
                .Path((_, _, _) => "v3/expenses")
                .CursorField(new[] { "vat_date" })
                .ParseResponse((response, _, _, _) =>
                {
                    var records = ParseData(response);
                    foreach (var record in records)
                        seen.Update(Date(record, "vat_date"));
                    return records;
                })
                .GetUpdatedState((state, _) =>
                {
                    var cursor = ExpensesCursor(state, seen.Max, startDate);
                    return cursor == null
                        ? EmptyState()
                        : new Dictionary<string, string> { { "vat_date", FormatDate(cursor.Value) } }.AsJsonElement();
                })
                .RequestParams((state, _, nextpagetoken) =>
                {
                    var parameters = PageParameters(nextpagetoken);
                    var from = ExpensesLowerBound(state, seen.Max, startDate, lookbackDays);

                    // Only send the filter when there is a lower bound: a full refresh must not silently drop
                    // history, so start_date has no default.
                    if (from != null)
                        parameters["vat_date_from"] = FormatDate(from.Value);

                    return parameters;
                })
                .Create("expenses");
        }

        internal static bool IsTransient(FlurlHttpException exception) =>
            exception.StatusCode == null || exception.StatusCode is 429 or 500 or 502 or 503 or 504;

        internal static Dictionary<string, object> PageParameters(Dictionary<string, object> nextpagetoken) =>
            new() { { "page", nextpagetoken != null && nextpagetoken.TryGetValue("page", out var page) ? page : 1 } };

        /// <summary>
        /// Follows <c>_links.next.href</c>. Every list endpoint answers with the same envelope, and endpoints that
        /// do not paginate simply omit the link.
        /// </summary>
        internal static Dictionary<string, object> NextPageToken(IFlurlResponse response)
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

        internal static IEnumerable<JsonElement> ParseData(IFlurlResponse response)
        {
            var body = response.GetStringAsync().Result.AsJsonElement();
            return body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().ToList()
                : new List<JsonElement>();
        }

        internal static IEnumerable<JsonElement> ParseObjectData(IFlurlResponse response)
        {
            var body = response.GetStringAsync().Result.AsJsonElement();
            return body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? new[] { data }
                : Array.Empty<JsonElement>();
        }

        internal static string Describe(Exception exception) => exception switch
        {
            FlurlHttpException http => DescribeHttp(http),
            _ => exception.Message
        };

        private static string DescribeHttp(FlurlHttpException exception)
        {
            var status = exception.StatusCode.HasValue ? ((int)exception.StatusCode).ToString() : "no response";

            string body;
            try
            {
                body = exception.GetResponseStringAsync()?.GetAwaiter().GetResult() ?? string.Empty;
            }
            catch (Exception)
            {
                body = string.Empty;
            }

            var (key, message) = ParseError(body);
            return string.IsNullOrWhiteSpace(key) ? $"HTTP {status}" : $"HTTP {status} {key}: {message}";
        }

        /// <summary>
        /// Jortt errors carry a machine readable key, for example <c>organization.header_required</c> or
        /// <c>organization.requires_mkb_plan</c>.
        /// </summary>
        internal static (string Key, string Message) ParseError(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return (null, null);

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("error", out var error))
                    return (null, null);

                // The API answers {"error": {"key": ..., "message": ...}}, while the OAuth token endpoint uses the
                // standard {"error": "invalid_client", "error_description": ...} shape.
                if (error.ValueKind == JsonValueKind.String)
                {
                    var oauthMessage = document.RootElement.TryGetProperty("error_description", out var description)
                        ? description.GetString()
                        : null;
                    return (error.GetString(), oauthMessage);
                }

                if (error.ValueKind != JsonValueKind.Object)
                    return (null, null);

                var key = error.TryGetProperty("key", out var errorKey) ? errorKey.GetString() : null;
                var message = error.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : null;
                return (key, message);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }

        private static List<string> Organizations(string token)
        {
            var body = ApiBase
                .AppendPathSegment("v3/organizations")
                .WithHeader("Accept", "application/json")
                .WithOAuthBearerToken(token)
                .GetJsonAsync<JsonElement>()
                .GetAwaiter()
                .GetResult();

            return body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray()
                    .Where(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetProperty("id").GetString()!)
                    .ToList()
                : new List<string>();
        }

        private static Dictionary<string, object> Headers(JorttAuth auth, string organizationId)
        {
            // GetAccessToken caches until shortly before expiry, so this both caches and refreshes within a
            // long sync.
            var headers = new Dictionary<string, object>
            {
                { "Accept", "application/json" }, { "Authorization", $"Bearer {auth.GetAccessToken()}" }
            };

            if (!string.IsNullOrWhiteSpace(organizationId))
                headers["Organization-Id"] = organizationId;

            return headers;
        }

        internal static JorttAuth Auth(JsonElement config) =>
            new(TokenEndpoint,
                Required(config, "client_id"),
                Required(config, "client_secret"),
                Optional(config, "scopes") ?? DefaultScopes);

        private static JsonElement EmptyState() => "{}".AsJsonElement();

        /// <summary>
        /// Cursor for the next incremental run: the highest VAT date seen so far, or the configured start date.
        /// </summary>
        internal static DateTime? ExpensesCursor(JsonElement state, DateTime? seenMax, DateTime? startDate) =>
            MaxDate(StateDate(state), seenMax) ?? startDate;

        /// <summary>
        /// Lower bound actually sent as <c>vat_date_from</c>. The lookback window catches expenses that were
        /// booked late with an older VAT date, which a cursor alone would skip forever.
        /// </summary>
        internal static DateTime? ExpensesLowerBound(JsonElement state, DateTime? seenMax, DateTime? startDate,
            int lookbackDays) =>
            ExpensesCursor(state, seenMax, startDate)?.AddDays(-lookbackDays);

        internal static DateTime? StateDate(JsonElement state) =>
            state.ValueKind == JsonValueKind.Object ? ParseDate(Date(state, "vat_date")) : null;

        internal static string Date(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        internal static DateTime? ParseDate(string value) =>
            DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var parsed)
                ? parsed
                : null;

        internal static string FormatDate(DateTime date) => date.ToString(DateFormat, CultureInfo.InvariantCulture);

        internal static DateTime? MaxDate(params DateTime?[] values)
        {
            DateTime? max = null;
            foreach (var value in values)
                if (value.HasValue && (max == null || value.Value > max.Value))
                    max = value;
            return max;
        }

        private static string Required(JsonElement config, string property) =>
            Optional(config, property)
            ?? throw new Exception($"Config property '{property}' is required");

        internal static string Optional(JsonElement config, string property)
        {
            if (!config.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
                return null;

            // A blank property must fall through to the default; sending `scope=` or `vat_date_from=` is worse
            // than omitting it.
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        internal static int? OptionalInt(JsonElement config, string property) =>
            config.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
    }

    /// <summary>
    /// Tracks the highest value seen so far, because the API's sort order is undocumented.
    /// </summary>
    internal sealed class CursorTracker
    {
        public DateTime? Max { get; private set; }

        public void Update(string value)
        {
            var parsed = Jortt.ParseDate(value);
            if (parsed.HasValue && (Max == null || parsed.Value > Max.Value))
                Max = parsed;
        }
    }

    /// <summary>
    /// OAuth 2.0 client-credentials flow. The access token is cached until shortly before it expires, and
    /// re-requested after that so a sync longer than the token lifetime keeps working.
    /// </summary>
    internal sealed class JorttAuth
    {
        private readonly string _tokenEndpoint;
        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _scopes;
        private readonly Func<DateTime> _clock;
        private string _token;
        private DateTime _expiresAt;

        public JorttAuth(string tokenEndpoint, string clientId, string clientSecret, string scopes,
            Func<DateTime> clock = null)
        {
            _tokenEndpoint = tokenEndpoint;
            _clientId = clientId;
            _clientSecret = clientSecret;
            _scopes = scopes;
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public string GetAccessToken()
        {
            if (_token != null && _clock() < _expiresAt)
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

            // Renew a minute early so a token cannot expire in flight.
            _expiresAt = _clock().AddSeconds(Math.Max(1, expiresIn - 60));

            return _token ?? throw new Exception("Jortt did not return an access token");
        }
    }
}