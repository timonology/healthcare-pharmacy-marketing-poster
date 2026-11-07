using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// What the Google Ads client actually sends. These pin the request shapes —
/// paused, EU declaration, unshared budget, phrase keywords, resolved areas,
/// brand assets at campaign level — so a refactor cannot quietly produce a
/// campaign that spends differently from the one the pharmacy set up.
/// They do not prove Google accepts them; the test-account checklist in
/// SOCIAL_SETUP.md does that.
/// </summary>
public class GoogleAdsRestClientTests
{
    private static readonly AdsAccess Access = new("refresh-1", "1234567890", null);

    [Fact]
    public async Task A_search_campaign_is_one_atomic_paused_request()
    {
        var google = new FakeGoogle();

        var result = await Client(google).CreateCampaignAsync(Search(), Access, null, default);

        Assert.True(result.Success, result.Error);
        Assert.Equal("555", result.ExternalCampaignId);

        var mutate = google.Mutates.Single();
        var ops = (JsonArray)mutate["mutateOperations"]!;

        var campaign = Created(ops, "campaignOperation").Single();
        Assert.Equal("PAUSED", (string?)campaign["status"]);
        Assert.Equal("SEARCH", (string?)campaign["advertisingChannelType"]);
        Assert.Equal("DOES_NOT_CONTAIN_EU_POLITICAL_ADVERTISING", (string?)campaign["containsEuPoliticalAdvertising"]);
        Assert.Equal("PRESENCE", (string?)campaign["geoTargetTypeSetting"]!["positiveGeoTargetType"]);
        Assert.NotNull(campaign["targetSpend"]);

        var budget = Created(ops, "campaignBudgetOperation").Single();
        Assert.False((bool)budget["explicitlyShared"]!);
        Assert.Equal("5000000", (string?)budget["amountMicros"]);

        var keyword = Created(ops, "adGroupCriterionOperation").Single();
        Assert.Equal("PHRASE", (string?)keyword["keyword"]!["matchType"]);

        var ad = Created(ops, "adGroupAdOperation").Single();
        Assert.Equal(3, ((JsonArray)ad["ad"]!["responsiveSearchAd"]!["headlines"]!).Count);

        // The area was looked up, in the UK, and used — not dropped.
        Assert.Equal("GB", (string?)google.GeoLookups.Single()["countryCode"]);
        Assert.Contains(Created(ops, "campaignCriterionOperation"),
            c => (string?)c["location"]?["geoTargetConstant"] == "geoTargetConstants/1006886");
        Assert.False((bool)mutate["validateOnly"]!);
    }

    [Fact]
    public async Task Every_call_carries_a_fresh_access_token_and_a_legacy_token_only_if_set()
    {
        var google = new FakeGoogle();

        await Client(google).CreateCampaignAsync(Search(), Access with { LoginCustomerId = "999-000-1111" }, null, default);

        var call = google.Requests.First(r => r.Uri.Contains("googleAds:mutate"));
        // Set in this test's options, so still sent; Google ignores it since
        // developer tokens were sunset (10 Sep 2026).
        Assert.Equal("dev-token", call.DeveloperToken);
        Assert.Equal("Bearer access-1", call.Authorization);
        Assert.Equal("9990001111", call.LoginCustomerId);
        Assert.Contains("/v25/", call.Uri);
    }

    [Fact]
    public async Task With_no_developer_token_set_none_is_sent_and_ads_still_work()
    {
        // Developer tokens were sunset: access comes from the Cloud project,
        // and a later API version may reject a call that carries one.
        var google = new FakeGoogle();

        var result = await Client(google, devToken: "").CreateCampaignAsync(Search(), Access, null, default);

        Assert.True(result.Success, result.Error);
        Assert.All(google.Requests.Where(r => r.Uri.Contains("googleads.googleapis.com")),
            r => Assert.Null(r.DeveloperToken));
    }

    [Fact]
    public void Google_ads_is_available_with_just_the_oauth_client()
    {
        Assert.True(new GoogleOptions { ClientId = "c", ClientSecret = "s" }.IsAdsConfigured);
        Assert.False(new GoogleOptions { ClientId = "c", ClientSecret = "s", AdsEnabled = false }.IsAdsConfigured);
    }

    [Fact]
    public async Task The_access_token_is_reused_until_it_expires()
    {
        var google = new FakeGoogle();
        var client = Client(google);

        await client.CreateCampaignAsync(Search(), Access, null, default);
        await client.CreateCampaignAsync(Search(), Access, null, default);

        Assert.Equal(1, google.TokenExchanges);
    }

    [Fact]
    public async Task An_area_google_cannot_find_stops_the_campaign_rather_than_running_it_everywhere()
    {
        var google = new FakeGoogle { KnownAreas = new() };

        var result = await Client(google).CreateCampaignAsync(Search(), Access, null, default);

        Assert.False(result.Success);
        Assert.Contains("Leeds", result.Error);
        Assert.Empty(google.Mutates);
    }

    [Fact]
    public async Task A_lifetime_budget_is_a_total_over_the_campaign_dates()
    {
        var google = new FakeGoogle();
        var campaign = Search(lifetime: true);

        await Client(google).CreateCampaignAsync(campaign, Access, null, default);

        var ops = (JsonArray)google.Mutates.Single()["mutateOperations"]!;
        var budget = Created(ops, "campaignBudgetOperation").Single();
        Assert.Equal("CUSTOM_PERIOD", (string?)budget["period"]);
        Assert.Equal("5000000", (string?)budget["totalAmountMicros"]);
        Assert.Null(budget["amountMicros"]);
        Assert.NotNull(Created(ops, "campaignOperation").Single()["endDateTime"]);
    }

    [Fact]
    public async Task Performance_max_links_brand_assets_to_the_campaign_in_the_same_request()
    {
        var google = new FakeGoogle();
        var creative = new AdsCreative(new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 });

        var result = await Client(google).CreateCampaignAsync(PerformanceMax(), Access, creative, default);

        Assert.True(result.Success, result.Error);

        // Text assets first (Google's rule), then everything else in one go.
        Assert.Equal(2, google.Mutates.Count);
        var main = (JsonArray)google.Mutates[1]["mutateOperations"]!;

        var campaign = Created(main, "campaignOperation").Single();
        Assert.Equal("PERFORMANCE_MAX", (string?)campaign["advertisingChannelType"]);
        Assert.Equal("PAUSED", (string?)campaign["status"]);
        Assert.NotNull(campaign["maximizeConversions"]);

        var brand = Created(main, "campaignAssetOperation").Select(c => (string?)c["fieldType"]).ToList();
        Assert.Contains("LOGO", brand);
        Assert.Contains("BUSINESS_NAME", brand);

        var linked = Created(main, "assetGroupAssetOperation").Select(c => (string?)c["fieldType"]).ToList();
        Assert.Equal(3, linked.Count(f => f == "HEADLINE"));
        Assert.Equal(2, linked.Count(f => f == "DESCRIPTION"));
        Assert.Contains("LONG_HEADLINE", linked);
        Assert.Contains("MARKETING_IMAGE", linked);
        Assert.Contains("SQUARE_MARKETING_IMAGE", linked);

        Assert.Equal("ENABLED", (string?)Created(main, "assetGroupOperation").Single()["status"]);
    }

    [Fact]
    public async Task Validate_only_mode_creates_nothing_and_says_so()
    {
        var google = new FakeGoogle();

        var result = await Client(google, validateOnly: true).CreateCampaignAsync(Search(), Access, null, default);

        Assert.False(result.Success);
        Assert.True(result.ValidatedOnly);
        Assert.Contains("nothing was created", result.Error);
        Assert.True((bool)google.Mutates.Single()["validateOnly"]!);
    }

    [Fact]
    public async Task Googles_own_error_message_reaches_the_pharmacy()
    {
        var google = new FakeGoogle
        {
            MutateFailure = """
            {"error":{"code":400,"message":"Request contains an invalid argument.","status":"INVALID_ARGUMENT",
             "details":[{"@type":"type.googleapis.com/google.ads.googleads.v25.errors.GoogleAdsFailure",
               "errors":[{"errorCode":{"campaignError":"DUPLICATE_CAMPAIGN_NAME"},"message":"Campaign name is already assigned to another campaign."}],
               "requestId":"req-42"}]}}
            """,
        };

        var result = await Client(google).CreateCampaignAsync(Search(), Access, null, default);

        Assert.False(result.Success);
        Assert.Contains("already have a campaign with that name", result.Error);
        Assert.Contains("req-42", result.Error);
    }

    [Fact]
    public async Task A_cloud_project_without_production_access_is_explained()
    {
        // v25's error for a Test Account Access project calling a real account.
        var google = new FakeGoogle
        {
            MutateFailure = """
            {"error":{"code":403,"message":"The caller does not have permission","status":"PERMISSION_DENIED",
             "details":[{"@type":"type.googleapis.com/google.ads.googleads.v25.errors.GoogleAdsFailure",
               "errors":[{"errorCode":{"authorizationError":"CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION"},"message":"Not approved."}],
               "requestId":"req-43"}]}}
            """,
        };

        var result = await Client(google, devToken: "").CreateCampaignAsync(Search(), Access, null, default);

        Assert.False(result.Success);
        Assert.Contains("test accounts", result.Error);
        Assert.Contains("Basic access", result.Error);
    }

    [Fact]
    public async Task Connecting_while_the_project_is_test_only_says_so_instead_of_no_accounts()
    {
        var google = new FakeGoogle { ProjectNotApproved = true };

        var ex = await Assert.ThrowsAsync<GoogleAdsApiException>(() =>
            Client(google, devToken: "").ListAccessibleCustomersAsync("refresh-1", default));

        Assert.True(ex.IsProjectAccess);
        Assert.False(ex.IsAuthFailure);   // not the pharmacy's sign-in: no Reconnect
        Assert.Contains("test accounts", ex.Message);
    }

    [Fact]
    public async Task No_answer_is_reported_as_maybe_created_and_paused()
    {
        var google = new FakeGoogle { MutateThrows = true };

        var result = await Client(google).CreateCampaignAsync(Search(), Access, null, default);

        Assert.False(result.Success);
        Assert.Contains("may have been created", result.Error);
    }

    [Fact]
    public async Task A_revoked_google_grant_is_reported_as_reconnect()
    {
        var google = new FakeGoogle { TokenRevoked = true };

        var result = await Client(google).CreateCampaignAsync(Search(), Access, null, default);

        Assert.False(result.Success);
        Assert.Contains("Reconnect", result.Error);
    }

    [Fact]
    public async Task Accounts_under_a_manager_are_listed_with_the_manager_as_login()
    {
        var google = new FakeGoogle();

        var accounts = await Client(google).ListAccessibleCustomersAsync("refresh-1", default);

        var direct = accounts.Single(a => a.CustomerId == "1234567890");
        Assert.Null(direct.LoginCustomerId);

        var client = accounts.Single(a => a.CustomerId == "3333333333");
        Assert.Equal("2222222222", client.LoginCustomerId);

        // The manager itself cannot hold campaigns and is not offered.
        Assert.DoesNotContain(accounts, a => a.CustomerId == "2222222222");
    }

    [Fact]
    public async Task Enabling_sends_only_the_status_change()
    {
        var google = new FakeGoogle();

        var ok = await Client(google).SetCampaignStatusAsync("555", enabled: true, Access, default);

        Assert.True(ok);
        var body = google.Bodies.Single(b => b.Uri.EndsWith("campaigns:mutate")).Body!;
        var op = body["operations"]![0]!;
        Assert.Equal("status", (string?)op["updateMask"]);
        Assert.Equal("ENABLED", (string?)op["update"]!["status"]);
        Assert.Equal("customers/1234567890/campaigns/555", (string?)op["update"]!["resourceName"]);
    }

    [Fact]
    public async Task Metrics_are_read_from_string_encoded_numbers()
    {
        var google = new FakeGoogle();

        var metrics = await Client(google).FetchMetricsAsync("555", Access, default);

        Assert.NotNull(metrics);
        Assert.Equal(1200, metrics!.Impressions);
        Assert.Equal(34, metrics.Clicks);
        Assert.Equal(12_500_000, metrics.CostMicros);
        Assert.Equal(3, metrics.Conversions);
    }

    /* ================================================================ */

    private static GoogleAdsRestClient Client(FakeGoogle google, bool validateOnly = false, string devToken = "dev-token") => new(
        new Factory(google),
        Options.Create(new GoogleOptions
        {
            ClientId = "client", ClientSecret = "secret", AdsDeveloperToken = devToken,
            AdsApiVersion = "v25", AdsValidateOnly = validateOnly,
        }),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<GoogleAdsRestClient>.Instance);

    private static readonly string[] Headlines = { "Flu jabs now", "Walk in today", "Free for over-65s" };
    private static readonly string[] Descriptions = { "Book your flu jab locally.", "Quick and friendly." };

    private static GoogleAdsCampaign Search(bool lifetime = false) => GoogleAdsCampaign.Draft(
        "owner-1", "conn-1", "1234567890", "Flu jabs", AdsCampaignType.Search,
        lifetime ? AdsBudgetKind.Lifetime : AdsBudgetKind.Daily, 5_000_000, "GBP",
        DateOnly.FromDateTime(DateTime.UtcNow), lifetime ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)) : null,
        "https://pharmacy.example/flu", PublishSourceKind.Poster, "poster-1",
        new[] { new AdsLocationTarget(null, "Leeds", null, null, null) },
        Headlines, Descriptions, new[] { "flu jab" });

    private static GoogleAdsCampaign PerformanceMax() => GoogleAdsCampaign.Draft(
        "owner-1", "conn-1", "1234567890", "Flu jabs PMax", AdsCampaignType.PerformanceMax,
        AdsBudgetKind.Daily, 5_000_000, "GBP", DateOnly.FromDateTime(DateTime.UtcNow), null,
        "https://pharmacy.example/flu", PublishSourceKind.Poster, "poster-1",
        new[] { new AdsLocationTarget(null, "Leeds", null, null, null) },
        Headlines, Descriptions, null, "Flu jabs at your local pharmacy", "Main St Pharmacy");

    private static IEnumerable<JsonNode> Created(JsonArray ops, string kind) =>
        ops.Where(o => o?[kind] is not null).Select(o => o![kind]!["create"]!);

    private sealed record Seen(string Uri, string? Authorization, string? DeveloperToken, string? LoginCustomerId);

    /// <summary>A small stand-in for Google: token endpoint, geo lookup, search and mutate.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = new();
        public List<(string Uri, JsonNode? Body)> Bodies { get; } = new();
        public List<JsonNode> Mutates { get; } = new();
        public List<JsonNode> GeoLookups { get; } = new();
        public int TokenExchanges { get; private set; }

        public Dictionary<string, string> KnownAreas { get; set; } = new() { ["Leeds"] = "geoTargetConstants/1006886" };
        public string? MutateFailure { get; set; }
        public bool MutateThrows { get; set; }
        public bool TokenRevoked { get; set; }

        /// <summary>Every account search refused: the server's Cloud project has test-only access.</summary>
        public bool ProjectNotApproved { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!.ToString();
            var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            JsonNode? body = null;
            if (text is not null && text.StartsWith('{')) body = JsonNode.Parse(text);

            Requests.Add(new Seen(uri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("developer-token", out var dt) ? dt.Single() : null,
                request.Headers.TryGetValues("login-customer-id", out var lc) ? lc.Single() : null));
            Bodies.Add((uri, body));

            if (uri.StartsWith("https://oauth2.googleapis.com/token"))
            {
                TokenExchanges++;
                return TokenRevoked
                    ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
                    : Json(HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3599}""");
            }

            if (uri.EndsWith("geoTargetConstants:suggest"))
            {
                GeoLookups.Add(body!);
                var suggestions = new JsonArray();
                foreach (var name in body!["locationNames"]!["names"]!.AsArray().Select(n => (string)n!))
                    if (KnownAreas.TryGetValue(name, out var resource))
                        suggestions.Add(new JsonObject
                        {
                            ["searchTerm"] = name,
                            ["geoTargetConstant"] = new JsonObject
                            {
                                ["resourceName"] = resource, ["countryCode"] = "GB", ["status"] = "ENABLED",
                            },
                        });
                return Json(HttpStatusCode.OK, new JsonObject { ["geoTargetConstantSuggestions"] = suggestions }.ToJsonString());
            }

            if (uri.EndsWith("googleAds:mutate"))
            {
                if (MutateThrows) throw new TaskCanceledException("timeout");
                if (MutateFailure is not null) return Json(HttpStatusCode.BadRequest, MutateFailure);

                Mutates.Add(body!);
                var responses = new JsonArray();
                foreach (var op in body!["mutateOperations"]!.AsArray())
                {
                    var kind = op!.AsObject().First().Key;
                    var result = kind switch
                    {
                        "campaignOperation" => new JsonObject { ["campaignResult"] = new JsonObject { ["resourceName"] = "customers/1234567890/campaigns/555" } },
                        "campaignBudgetOperation" => new JsonObject { ["campaignBudgetResult"] = new JsonObject { ["resourceName"] = "customers/1234567890/campaignBudgets/777" } },
                        "assetOperation" => new JsonObject { ["assetResult"] = new JsonObject { ["resourceName"] = $"customers/1234567890/assets/{Random.Shared.Next(1000, 9999)}" } },
                        _ => new JsonObject(),
                    };
                    responses.Add(result);
                }
                return Json(HttpStatusCode.OK, new JsonObject { ["mutateOperationResponses"] = responses }.ToJsonString());
            }

            if (uri.EndsWith("campaigns:mutate"))
                return Json(HttpStatusCode.OK, """{"results":[{"resourceName":"customers/1234567890/campaigns/555"}]}""");

            if (uri.EndsWith("customers:listAccessibleCustomers"))
                return Json(HttpStatusCode.OK, """{"resourceNames":["customers/1234567890","customers/2222222222"]}""");

            if (uri.EndsWith("googleAds:search") && ProjectNotApproved)
                return Json(HttpStatusCode.Forbidden, """
                    {"error":{"code":403,"message":"The caller does not have permission","status":"PERMISSION_DENIED",
                     "details":[{"@type":"type.googleapis.com/google.ads.googleads.v25.errors.GoogleAdsFailure",
                       "errors":[{"errorCode":{"authorizationError":"CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION"},"message":"Not approved."}]}]}}
                    """);

            if (uri.EndsWith("googleAds:search"))
            {
                var query = (string)body!["query"]!;
                if (query.Contains("FROM customer_client"))
                    return Json(HttpStatusCode.OK, """{"results":[{"customerClient":{"id":"3333333333","descriptiveName":"Branch","currencyCode":"GBP"}}]}""");
                if (query.Contains("FROM customer "))
                    return uri.Contains("2222222222")
                        ? Json(HttpStatusCode.OK, """{"results":[{"customer":{"id":"2222222222","descriptiveName":"Manager","currencyCode":"GBP","manager":true}}]}""")
                        : Json(HttpStatusCode.OK, """{"results":[{"customer":{"id":"1234567890","descriptiveName":"Main St","currencyCode":"GBP","manager":false}}]}""");
                if (query.Contains("FROM campaign"))
                    return Json(HttpStatusCode.OK, """{"results":[{"metrics":{"impressions":"1200","clicks":"34","costMicros":"12500000","conversions":3.0}}]}""");
            }

            return Json(HttpStatusCode.NotFound, """{"error":{"message":"unexpected call"}}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
