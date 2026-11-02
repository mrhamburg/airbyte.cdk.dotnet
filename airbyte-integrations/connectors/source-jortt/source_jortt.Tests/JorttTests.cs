using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Airbyte.Cdk.Sources.Streams.Http;
using Flurl.Http;
using Flurl.Http.Testing;
using Json.Schema;
using Shouldly;
using Xunit;

namespace source_jortt.Tests
{
    public class PaginationTests
    {
        [Fact]
        public void Test_PageParameters_DefaultsToFirstPage()
        {
            Jortt.PageParameters(null)["page"].ShouldBe(1);
        }

        [Fact]
        public void Test_PageParameters_UsesNextPageToken()
        {
            var parameters = Jortt.PageParameters(new Dictionary<string, object> { { "page", 3 } });

            parameters["page"].ShouldBe(3);
        }

        [Fact]
        public async Task Test_NextPageToken_ReadsPageFromLinks()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(
                "{\"data\":[],\"_links\":{\"next\":{\"href\":\"https://api.jortt.nl/v3/customers?page=3\"}}}", 200);

            var response = await "https://api.jortt.nl/v3/customers".GetAsync();

            Jortt.NextPageToken(response)["page"].ShouldBe(3);
        }

        [Fact]
        public async Task Test_NextPageToken_ReturnsNull_WhenNextIsNull()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":[],""_links"":{""next"":null}}", 200);

            var response = await "https://api.jortt.nl/v3/customers".GetAsync();

            Jortt.NextPageToken(response).ShouldBeNull();
        }

        [Fact]
        public async Task Test_NextPageToken_ReturnsNull_WhenLinksAreMissing()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":[]}", 200);

            var response = await "https://api.jortt.nl/v3/organizations".GetAsync();

            Jortt.NextPageToken(response).ShouldBeNull();
        }

        [Fact]
        public async Task Test_NextPageToken_ReturnsNull_WhenHrefHasNoPage()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":[],""_links"":{""next"":{""href"":""https://api.jortt.nl/v3/customers?query=x""}}}", 200);

            var response = await "https://api.jortt.nl/v3/customers".GetAsync();

            Jortt.NextPageToken(response).ShouldBeNull();
        }

        [Fact]
        public async Task Test_ParseData_ReturnsEveryRecord()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":[{""id"":""a""},{""id"":""b""}]}", 200);

            var response = await "https://api.jortt.nl/v3/customers".GetAsync();

            Jortt.ParseData(response).Count().ShouldBe(2);
        }

        [Fact]
        public async Task Test_ParseData_TreatsNullDataAsEmpty()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":null}", 200);

            var response = await "https://api.jortt.nl/v3/customers".GetAsync();

            Jortt.ParseData(response).ShouldBeEmpty();
        }

        [Fact]
        public async Task Test_ParseData_FailsOnAnUnexpectedShapeInsteadOfSyncingNothing()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""error"":{""code"":401}}", 200);
            httpTest.RespondWith(@"{""data"":{""id"":""a""}}", 200);

            var withoutData = await "https://api.jortt.nl/v3/customers".GetAsync();
            var objectData = await "https://api.jortt.nl/v3/customers".GetAsync();

            Should.Throw<Exception>(() => Jortt.ParseData(withoutData).ToList())
                .Message.ShouldContain("without 'data'");
            Should.Throw<Exception>(() => Jortt.ParseData(objectData).ToList())
                .Message.ShouldContain("instead of Array");
        }

        [Fact]
        public async Task Test_ParseObjectData_FailsOnAnArrayShape()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":[]}", 200);

            var response = await "https://api.jortt.nl/v3/organizations/me".GetAsync();

            Should.Throw<Exception>(() => Jortt.ParseObjectData(response).ToList())
                .Message.ShouldContain("instead of Object");
        }
    }

    public class CursorTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void Test_CursorTracker_KeepsHighestDateRegardlessOfOrder()
        {
            var tracker = new CursorTracker();

            tracker.Update("2026-03-01");
            tracker.Update("2026-01-01");
            tracker.Update(null);

            Jortt.FormatDate(tracker.Max!.Value).ShouldBe("2026-03-01");
        }

        [Fact]
        public void Test_ExpensesCursor_PrefersTheHighestOfStateAndRecords()
        {
            var state = Json(@"{""vat_date"":""2026-02-01""}");

            Jortt.FormatDate(Jortt.ExpensesCursor(state, new DateTime(2026, 1, 1), null)!.Value)
                .ShouldBe("2026-02-01");
            Jortt.FormatDate(Jortt.ExpensesCursor(state, new DateTime(2026, 5, 1), null)!.Value)
                .ShouldBe("2026-05-01");
        }

        [Fact]
        public void Test_ExpensesCursor_FallsBackToStartDate()
        {
            var state = Json("{}");

            Jortt.FormatDate(Jortt.ExpensesCursor(state, null, new DateTime(2020, 1, 1))!.Value)
                .ShouldBe("2020-01-01");
        }

        [Fact]
        public void Test_ExpensesCursor_IsNullWithoutStateRecordsOrStartDate()
        {
            Jortt.ExpensesCursor(Json("{}"), null, null).ShouldBeNull();
        }

        [Fact]
        public void Test_ExpensesLowerBound_AppliesLookback()
        {
            var state = Json(@"{""vat_date"":""2026-10-10""}");

            Jortt.FormatDate(Jortt.ExpensesLowerBound(state, null, null, 7)!.Value).ShouldBe("2026-10-03");
        }

        [Fact]
        public void Test_ExpensesLowerBound_IsNullSoNoFilterIsSent()
        {
            // A full refresh must not silently drop history, so start_date has no default and no lower bound is
            // sent when nothing is known.
            Jortt.ExpensesLowerBound(Json("{}"), null, null, 7).ShouldBeNull();
        }

        [Fact]
        public void Test_ExpensesLowerBound_NeverReachesBehindStartDate()
        {
            // start_date is a floor, so the lookback may not pull the window behind it.
            Jortt.FormatDate(Jortt.ExpensesLowerBound(Json("{}"), null, new DateTime(2026, 9, 1), 7)!.Value)
                .ShouldBe("2026-09-01");

            var stale = Json(@"{""vat_date"":""2020-01-01""}");
            Jortt.FormatDate(Jortt.ExpensesLowerBound(stale, null, new DateTime(2026, 9, 1), 7)!.Value)
                .ShouldBe("2026-09-01");
        }

        [Fact]
        public void Test_ExpensesLowerBound_ClampsOnlyWhenTheLookbackCrossesStartDate()
        {
            var near = Json(@"{""vat_date"":""2026-09-05""}");
            Jortt.FormatDate(Jortt.ExpensesLowerBound(near, null, new DateTime(2026, 9, 1), 7)!.Value)
                .ShouldBe("2026-09-01");

            var far = Json(@"{""vat_date"":""2026-10-10""}");
            Jortt.FormatDate(Jortt.ExpensesLowerBound(far, null, new DateTime(2026, 9, 1), 7)!.Value)
                .ShouldBe("2026-10-03");
        }
    }

    public class AuthTests
    {
        [Fact]
        public void Test_GetAccessToken_PostsClientCredentialsAndCaches()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""access_token"":""token-1"",""expires_in"":7200}", 200);

            var auth = new JorttAuth("https://app.jortt.nl/oauth-provider/oauth/token", "id", "secret",
                "customers:read");

            auth.GetAccessToken().ShouldBe("token-1");
            auth.GetAccessToken().ShouldBe("token-1");

            httpTest.CallLog.Count.ShouldBe(1);
            var call = httpTest.CallLog[0];
            call.Request.Url.ToString().ShouldBe("https://app.jortt.nl/oauth-provider/oauth/token");
            call.RequestBody.ShouldContain("grant_type=client_credentials");
            call.RequestBody.ShouldContain("scope=customers%3Aread");
            call.HttpRequestMessage.Headers.GetValues("Authorization").First().ShouldStartWith("Basic ");
        }

        [Fact]
        public void Test_GetAccessToken_RefreshesAfterExpiry()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""access_token"":""token-1"",""expires_in"":7200}", 200);
            httpTest.RespondWith(@"{""access_token"":""token-2"",""expires_in"":7200}", 200);

            var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var auth = new JorttAuth("https://app.jortt.nl/oauth-provider/oauth/token", "id", "secret",
                "customers:read", () => now);

            auth.GetAccessToken().ShouldBe("token-1");
            now = now.AddHours(2);
            auth.GetAccessToken().ShouldBe("token-2");

            httpTest.CallLog.Count.ShouldBe(2);
        }

        [Fact]
        public void Test_GetAccessToken_ThrowsWhenTokenIsMissing()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""token_type"":""Bearer""}", 200);

            var auth = new JorttAuth("https://app.jortt.nl/oauth-provider/oauth/token", "id", "secret", "s");

            Should.Throw<Exception>(() => auth.GetAccessToken());
        }

        [Fact]
        public void Test_TryRefreshAfterUnauthorized_DropsTheCachedTokenOnlyOnce()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""access_token"":""token-1"",""expires_in"":7200}", 200);
            httpTest.RespondWith(@"{""access_token"":""token-2"",""expires_in"":7200}", 200);

            var auth = new JorttAuth("https://app.jortt.nl/oauth-provider/oauth/token", "id", "secret", "s");

            auth.GetAccessToken().ShouldBe("token-1");
            auth.TryRefreshAfterUnauthorized().ShouldBeTrue();
            auth.GetAccessToken().ShouldBe("token-2");
            auth.TryRefreshAfterUnauthorized().ShouldBeFalse();
        }

        [Fact]
        public async Task Test_ShouldRetry_RefreshesTheTokenOnceAfterUnauthorized()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith("unauthorized", 401);

            var auth = new JorttAuth("https://app.jortt.nl/oauth-provider/oauth/token", "id", "secret", "s");
            var exception = await Should.ThrowAsync<FlurlHttpException>(() => "https://api.jortt.nl/x".GetAsync());

            Jortt.ShouldRetry(exception, auth).ShouldBeTrue();
            Jortt.ShouldRetry(exception, auth).ShouldBeFalse();
        }

        [Fact]
        public async Task Test_ShouldRetry_LeavesClientErrorsAlone()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith("forbidden", 403);

            var auth = new JorttAuth("https://app.jortt.nl/oauth-provider/oauth/token", "id", "secret", "s");
            var exception = await Should.ThrowAsync<FlurlHttpException>(() => "https://api.jortt.nl/x".GetAsync());

            Jortt.ShouldRetry(exception, auth).ShouldBeFalse();
        }
    }

    public class ConfigTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        [Fact]
        public void Test_Optional_ReturnsNullForBlankValues()
        {
            var config = Json(@"{""client_id"":""  "",""organization_id"":""""}");

            Jortt.Optional(config, "client_id").ShouldBeNull();
            Jortt.Optional(config, "organization_id").ShouldBeNull();
            Jortt.Optional(config, "missing").ShouldBeNull();
        }

        [Fact]
        public void Test_Optional_ReturnsTrimmedValue()
        {
            Jortt.Optional(Json(@"{""client_id"":""abc""}"), "client_id").ShouldBe("abc");
        }

        [Fact]
        public void Test_OptionalInt_IgnoresStringsAndClampsNegativeValues()
        {
            Jortt.OptionalInt(Json(@"{""lookback_days"":7}"), "lookback_days").ShouldBe(7);
            Jortt.OptionalInt(Json(@"{""lookback_days"":""7""}"), "lookback_days").ShouldBeNull();
            Jortt.OptionalInt(Json(@"{""lookback_days"":-3}"), "lookback_days").ShouldBe(0);
            Jortt.OptionalInt(Json("{}"), "lookback_days").ShouldBeNull();
        }

        [Fact]
        public async Task Test_IsTransient_CoversThrottlingAndServerFailures()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith("throttled", 429);
            httpTest.RespondWith("bad gateway", 502);
            httpTest.RespondWith("gateway timeout", 504);
            httpTest.RespondWith("not found", 404);

            Jortt.IsTransient(await Should.ThrowAsync<FlurlHttpException>(() => "https://api.jortt.nl/x".GetAsync()))
                .ShouldBeTrue();
            Jortt.IsTransient(await Should.ThrowAsync<FlurlHttpException>(() => "https://api.jortt.nl/x".GetAsync()))
                .ShouldBeTrue();
            Jortt.IsTransient(await Should.ThrowAsync<FlurlHttpException>(() => "https://api.jortt.nl/x".GetAsync()))
                .ShouldBeTrue();
            Jortt.IsTransient(await Should.ThrowAsync<FlurlHttpException>(() => "https://api.jortt.nl/x".GetAsync()))
                .ShouldBeFalse();
        }
    }

    public class ErrorTests
    {
        [Fact]
        public void Test_ParseError_ReadsKeyAndMessage()
        {
            var (key, message) = Jortt.ParseError(
                @"{""error"":{""code"":401,""key"":""organization.header_required"",""message"":""Name the organization""}}");

            key.ShouldBe("organization.header_required");
            message.ShouldBe("Name the organization");
        }

        [Fact]
        public void Test_ParseError_ReadsOAuthStyleErrors()
        {
            var (key, message) = Jortt.ParseError(
                @"{""error"":""invalid_client"",""error_description"":""Client verification failed""}");

            key.ShouldBe("invalid_client");
            message.ShouldBe("Client verification failed");
        }

        [Fact]
        public void Test_ParseError_ReturnsNullsForUnusableBodies()
        {
            Jortt.ParseError(null).Key.ShouldBeNull();
            Jortt.ParseError("not json").Key.ShouldBeNull();
            Jortt.ParseError(@"{""data"":[]}").Key.ShouldBeNull();
        }
    }

    public class SubstreamTests
    {
        private static Jortt Source() => new();

        private static System.Collections.Generic.Dictionary<string, Airbyte.Cdk.Sources.Streams.Stream> Streams()
        {
            var config = JsonDocument.Parse(@"{""client_id"":""id"",""client_secret"":""secret""}").RootElement.Clone();
            return Source().Streams(config).ToDictionary(x => x.Name);
        }

        [Theory]
        [InlineData("project_line_items", "projects", "id", "project_id")]
        [InlineData("bank_transactions", "bank_accounts", "id", "bank_account_id")]
        [InlineData("invoice_payments", "invoices", "id", "invoice_id")]
        [InlineData("expense_payments", "expenses", "id", "expense_id")]
        public void Test_Substreams_DeclareTheirParent(string stream, string parent, string keyField, string slice)
        {
            var dependency = Streams()[stream].Dependency;

            dependency.ShouldNotBeNull();
            dependency!.ParentStream.ShouldBe(parent);
            dependency.ParentKeyField.ShouldBe(keyField);
            dependency.SliceParameter.ShouldBe(slice);
        }

        [Fact]
        public void Test_Substreams_ParentsAreAvailableStreams()
        {
            var streams = Streams();

            streams.Keys.ShouldContain("projects");
            streams.Keys.ShouldContain("bank_accounts");
            streams.Keys.ShouldContain("invoices");
            streams.Keys.ShouldContain("expenses");

            streams.Values.Count(x => x.Dependency != null).ShouldBe(4);
        }
    }

    public class StreamRequestTests
    {
        private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

        private static Dictionary<string, HttpStream> Streams() =>
            new Jortt()
                .Streams(Json(@"{""client_id"":""id"",""client_secret"":""secret""}"))
                .ToDictionary(x => x.Name, x => (HttpStream)x);

        [Fact]
        public async Task Test_Expenses_KeepsTheDateFilterStableAcrossPages()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(
                @"{""data"":[{""vat_date"":""2026-10-10""}],""_links"":{""next"":{""href"":""https://api.jortt.nl/v3/expenses?page=2""}}}",
                200);

            var expenses = Streams()["expenses"];
            var state = Json(@"{""vat_date"":""2026-01-01""}");

            var first = expenses.RequestParams(state, null, null);
            var response = await "https://api.jortt.nl/v3/expenses".GetAsync();
            expenses.ParseResponse(response, state, null, null).Count().ShouldBe(1);
            var second = expenses.RequestParams(state, null, new Dictionary<string, object> { { "page", 2 } });

            // Parsing page 1 moved the cursor to 2026-10-10. Page 2 must still ask for the same window: Jortt
            // applies page=2 to the result set of the filters in that request, so a moving filter skips history.
            first["vat_date_from"].ShouldBe("2025-12-25");
            second["vat_date_from"].ShouldBe("2025-12-25");
            second["page"].ShouldBe(2);
        }

        [Fact]
        public async Task Test_Expenses_UpdatesStateFromTheHighestDateItSaw()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(
                @"{""data"":[{""vat_date"":""2026-03-01""},{""vat_date"":""2026-10-10""}]}", 200);

            var expenses = Streams()["expenses"];
            var state = Json(@"{""vat_date"":""2026-01-01""}");
            var response = await "https://api.jortt.nl/v3/expenses".GetAsync();
            expenses.ParseResponse(response, state, null, null).Count().ShouldBe(2);

            // The tracker now only feeds the state, never the paging filter.
            expenses.GetUpdatedState(state, Json("{}")).GetProperty("vat_date").GetString()
                .ShouldBe("2026-10-10");
        }

        [Fact]
        public void Test_Expenses_ClampsTheFilterToStartDate()
        {
            var expenses = (HttpStream)new Jortt()
                .Streams(Json(
                    @"{""client_id"":""id"",""client_secret"":""secret"",""start_date"":""2026-09-01""}"))
                .Single(x => x.Name == "expenses");

            expenses.RequestParams(Json("{}"), null, null)["vat_date_from"].ShouldBe("2026-09-01");
        }

        [Theory]
        [InlineData("bank_transactions", "bank_account_id")]
        [InlineData("invoice_payments", "invoice_id")]
        [InlineData("expense_payments", "expense_id")]
        [InlineData("project_line_items", "project_id")]
        public async Task Test_Substreams_WriteTheParentKeyIntoEveryRecord(string streamName, string field)
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":[{""booking_id"":""b-1""},{""booking_id"":""b-2""}]}", 200);

            var records = Streams()[streamName]
                .ParseResponse(await "https://api.jortt.nl/v3/parent/1/children".GetAsync(), Json("{}"),
                    new Dictionary<string, object> { { field, "parent-1" } }, null)
                .ToList();

            records.Count.ShouldBe(2);
            records.ShouldAllBe(record => record.GetProperty(field).GetString() == "parent-1");
        }

        [Fact]
        public void Test_WithProperty_KeepsAValueTheApiSentAndIgnoresNonObjects()
        {
            Jortt.WithProperty(Json(@"{""project_id"":""from-api""}"), "project_id", "from-slice")
                .GetProperty("project_id").GetString().ShouldBe("from-api");
            Jortt.WithProperty(Json(@"{""id"":""x""}"), "project_id", "from-slice")
                .GetProperty("project_id").GetString().ShouldBe("from-slice");
            Jortt.WithProperty(Json("[1]"), "project_id", "from-slice").ValueKind.ShouldBe(JsonValueKind.Array);
        }
    }

    public class SchemaTests
    {
        public static IEnumerable<object[]> Streams() => new[]
        {
            "organizations", "customers", "invoices", "estimates", "expenses", "receipts", "bank_accounts",
            "ledger_accounts_cost", "ledger_accounts_income", "ledger_accounts_balance",
            "projects", "labels", "loonjournaalposten", "organization",
            "report_balance", "report_profit_and_loss", "report_summary_balance", "report_summary_btw",
            "report_summary_cash_and_bank", "report_summary_invoices", "report_summary_profit_and_loss",
            "project_line_items", "bank_transactions", "invoice_payments", "expense_payments"
        }.Select(x => new object[] { x });

        [Theory]
        [MemberData(nameof(Streams))]
        public void Test_FixtureMatchesSchema(string stream)
        {
            var schema = JsonSchema.FromFile(Path.Combine(AppContext.BaseDirectory, "schemas", stream + ".json"));
            var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                stream + ".json"))).RootElement;

            var result = schema.Evaluate(fixture, new EvaluationOptions { OutputFormat = OutputFormat.List });

            result.IsValid.ShouldBeTrue(Violations(result));
        }

        [Fact]
        public void Test_SpecAcceptsTheSampleConfig()
        {
            var spec = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "spec.json")))
                .RootElement.GetProperty("connectionSpecification");
            var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures",
                "config.json"))).RootElement;

            var result = JsonSchema.FromText(spec.GetRawText())
                .Evaluate(config, new EvaluationOptions { OutputFormat = OutputFormat.List });

            result.IsValid.ShouldBeTrue(Violations(result));
        }

        private static string Violations(EvaluationResults result) =>
            string.Join(Environment.NewLine, (result.Details ?? new List<EvaluationResults>())
                .Where(x => x.Errors != null && x.Errors.Count > 0)
                .Select(x => $"{x.InstanceLocation}: {string.Join(", ", x.Errors.Select(e => e.Key + " " + e.Value))}"));
    }
}