using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
        public async Task Test_ParseData_ReturnsEveryRecordAndToleratesMissingData()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWith(@"{""data"":[{""id"":""a""},{""id"":""b""}]}", 200);
            httpTest.RespondWith(@"{""error"":{""code"":401}}", 200);

            var withData = await "https://api.jortt.nl/v3/customers".GetAsync();
            var withoutData = await "https://api.jortt.nl/v3/customers".GetAsync();

            Jortt.ParseData(withData).Count().ShouldBe(2);
            Jortt.ParseData(withoutData).ShouldBeEmpty();
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

    public class SchemaTests
    {
        public static IEnumerable<object[]> Streams() => new[]
        {
            "organizations", "customers", "invoices", "estimates", "expenses", "receipts", "bank_accounts",
            "ledger_accounts_cost", "ledger_accounts_income", "ledger_accounts_balance"
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