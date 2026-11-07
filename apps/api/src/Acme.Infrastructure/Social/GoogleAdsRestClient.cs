using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// The Google Ads API over REST — no client library, the same way the Meta
/// integration talks to Graph.
/// </summary>
/// <remarks>
/// <para>
/// REST rather than the gRPC client library because the library pins a
/// version per package release and pulls in gRPC and protobuf for a handful
/// of calls; REST keeps the version a setting (<see cref="GoogleOptions.AdsApiVersion"/>)
/// and the dependency list unchanged.
/// </para>
/// <para>
/// <b>Campaign creation is one atomic <c>googleAds:mutate</c></b>: budget,
/// campaign, targeting, ad group or asset group, assets and links either all
/// exist afterwards or none do, so a half-made campaign can never be left
/// spending. The campaign is always created <c>PAUSED</c>; enabling it is a
/// separate, confirmed step. Performance Max needs its headlines and
/// descriptions to exist first (Google's rule), so those text assets are made
/// in one preceding call — harmless if the main call then fails, because an
/// unlinked text asset shows nowhere and costs nothing.
/// </para>
/// <para>
/// Every new campaign declares that it contains no EU political advertising —
/// Google refuses campaign creation without the declaration, and a pharmacy
/// advertising services has none.
/// </para>
/// </remarks>
public sealed class GoogleAdsRestClient(
    IHttpClientFactory httpFactory,
    IOptions<GoogleOptions> options,
    IMemoryCache cache,
    ILogger<GoogleAdsRestClient> log) : IGoogleAdsClient
{
    private readonly GoogleOptions _o = options.Value;

    /// <summary>English. The audience a UK pharmacy writes its ads for.</summary>
    private const string LanguageEnglish = "languageConstants/1000";

    /// <summary>Area names are looked up in the UK.</summary>
    private const string CountryCode = "GB";

    public bool IsConfigured => _o.IsAdsConfigured;

    private string Api(string path) => $"https://googleads.googleapis.com/{_o.AdsApiVersion}/{path}";

    /* ================================================================ *
     * Accounts
     * ================================================================ */

    /// <summary>
    /// Every Ads account the signed-in Google user can run campaigns in.
    /// </summary>
    /// <remarks>
    /// Directly accessible accounts come back from <c>listAccessibleCustomers</c>.
    /// A manager (MCC) cannot hold campaigns itself, so each manager is opened
    /// up one level to the client accounts under it, which are then reached
    /// with the manager as <c>login-customer-id</c>. That is the usual shape of
    /// a test setup (a test manager with test clients) and of an agency.
    /// </remarks>
    public async Task<IReadOnlyList<AdsAccountSummary>> ListAccessibleCustomersAsync(
        string refreshToken, CancellationToken ct)
    {
        using var listed = await SendAsync(HttpMethod.Get, Api("customers:listAccessibleCustomers"),
            null, refreshToken, loginCustomerId: null, ct);

        var ids = listed.RootElement.TryGetProperty("resourceNames", out var names)
            ? names.EnumerateArray().Select(n => IdOf(n.GetString())).Where(id => id.Length > 0).ToList()
            : new List<string>();

        var accounts = new List<AdsAccountSummary>();
        GoogleAdsApiException? projectRefusal = null;

        // Bounded: one person with access to hundreds of accounts is an agency,
        // and the picker would be useless anyway.
        foreach (var id in ids.Take(50))
        {
            try
            {
                var rows = await SearchAsync(id, id,
                    "SELECT customer.id, customer.descriptive_name, customer.currency_code, customer.manager " +
                    "FROM customer LIMIT 1",
                    refreshToken, ct);

                var customer = rows.FirstOrDefault()?["customer"];
                if (customer is null) continue;

                if (customer["manager"]?.GetValue<bool>() == true)
                {
                    var clients = await SearchAsync(id, id,
                        "SELECT customer_client.id, customer_client.descriptive_name, customer_client.currency_code, " +
                        "customer_client.manager, customer_client.status FROM customer_client " +
                        "WHERE customer_client.level = 1 AND customer_client.manager = FALSE " +
                        "AND customer_client.status = 'ENABLED'",
                        refreshToken, ct);

                    foreach (var row in clients.Take(100))
                    {
                        var c = row["customerClient"];
                        if (c is null) continue;
                        accounts.Add(new AdsAccountSummary(
                            Text(c["id"]), Text(c["descriptiveName"]), Text(c["currencyCode"]), LoginCustomerId: id));
                    }
                }
                else
                {
                    accounts.Add(new AdsAccountSummary(
                        Text(customer["id"]), Text(customer["descriptiveName"]), Text(customer["currencyCode"])));
                }
            }
            catch (GoogleAdsApiException ex) when (!ex.IsAuthFailure)
            {
                // A cancelled, suspended or otherwise unusable account should
                // not hide the usable ones beside it.
                if (ex.IsProjectAccess) projectRefusal ??= ex;
                log.LogInformation("Skipping Google Ads account {Customer}: {Reason}", id, ex.Message);
            }
        }

        // Every real account refused because this server's Cloud project has
        // test-only access: say that, rather than "you have no Ads account".
        if (accounts.Count == 0 && projectRefusal is not null) throw projectRefusal;

        return accounts
            .Where(a => a.CustomerId.Length > 0)
            .DistinctBy(a => a.CustomerId)
            .ToList();
    }

    /* ================================================================ *
     * Create
     * ================================================================ */

    public async Task<AdsCreateResult> CreateCampaignAsync(
        GoogleAdsCampaign campaign, AdsAccess access, AdsCreative? creative, CancellationToken ct)
    {
        try
        {
            var geo = await ResolveLocationsAsync(campaign.Locations, access, ct);

            var operations = campaign.CampaignType switch
            {
                AdsCampaignType.Search => SearchOperations(campaign, access.CustomerId, geo),
                AdsCampaignType.PerformanceMax => await PerformanceMaxOperationsAsync(
                    campaign, access, creative ?? throw new InvalidOperationException(
                        "Performance Max needs its images."), geo, ct),
                _ => throw new GoogleAdsApiException("That campaign type is not supported yet."),
            };

            var body = new JsonObject
            {
                ["mutateOperations"] = new JsonArray(operations.ToArray<JsonNode?>()),
                ["validateOnly"] = _o.AdsValidateOnly,
            };

            using var response = await SendAsync(HttpMethod.Post,
                Api($"customers/{access.CustomerId}/googleAds:mutate"), body,
                access.RefreshToken, access.LoginCustomerId, ct);

            if (_o.AdsValidateOnly)
                return new AdsCreateResult(false, null, null, null,
                    "Checked by Google and valid — nothing was created, because this server is in validate-only mode.",
                    ValidatedOnly: true);

            string? campaignId = null, budgetId = null, assetId = null;
            if (response.RootElement.TryGetProperty("mutateOperationResponses", out var results))
            {
                foreach (var r in results.EnumerateArray())
                {
                    if (r.TryGetProperty("campaignResult", out var c)) campaignId ??= IdOf(ResourceName(c));
                    else if (r.TryGetProperty("campaignBudgetResult", out var b)) budgetId ??= IdOf(ResourceName(b));
                    else if (r.TryGetProperty("assetResult", out var a)) assetId ??= IdOf(ResourceName(a));
                }
            }

            return string.IsNullOrEmpty(campaignId)
                ? new AdsCreateResult(false, null, null, null,
                    "Google accepted the request but returned no campaign. Check your Google Ads account before trying again.")
                : new AdsCreateResult(true, campaignId, budgetId, assetId, null);
        }
        catch (GoogleAdsApiException ex)
        {
            return new AdsCreateResult(false, null, null, null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       && !ct.IsCancellationRequested)
        {
            // The mutate is atomic, but an answer lost in transit leaves the
            // outcome unknown. Paused either way, so nothing is spending.
            log.LogWarning(ex, "Google Ads campaign creation for {Customer} had no answer", access.CustomerId);
            return new AdsCreateResult(false, null, null, null,
                "Google did not answer in time. The campaign may have been created, paused — check your Google Ads account before trying again.");
        }
    }

    /// <summary>Search: budget, campaign, targeting, one ad group, one responsive search ad, keywords.</summary>
    private static List<JsonObject> SearchOperations(GoogleAdsCampaign c, string customerId, IReadOnlyList<JsonObject> geo)
    {
        var budget = $"customers/{customerId}/campaignBudgets/-1";
        var campaign = $"customers/{customerId}/campaigns/-2";
        var adGroup = $"customers/{customerId}/adGroups/-3";

        var ops = new List<JsonObject>
        {
            Op("campaignBudgetOperation", Budget(c, budget)),
            Op("campaignOperation", new JsonObject
            {
                ["resourceName"] = campaign,
                ["name"] = c.Name,
                ["status"] = "PAUSED",
                ["advertisingChannelType"] = "SEARCH",
                ["campaignBudget"] = budget,
                // Maximise clicks: the strategy that works without conversion
                // tracking, which most pharmacies do not have.
                ["targetSpend"] = new JsonObject(),
                ["networkSettings"] = new JsonObject
                {
                    ["targetGoogleSearch"] = true,
                    ["targetSearchNetwork"] = true,
                    ["targetContentNetwork"] = false,
                    ["targetPartnerSearchNetwork"] = false,
                },
                // People actually in the area — not people elsewhere who are
                // "interested in" it, who cannot walk into the shop.
                ["geoTargetTypeSetting"] = new JsonObject { ["positiveGeoTargetType"] = "PRESENCE" },
                ["containsEuPoliticalAdvertising"] = "DOES_NOT_CONTAIN_EU_POLITICAL_ADVERTISING",
                ["startDateTime"] = StartDateTime(c.StartDate),
                ["endDateTime"] = c.EndDate is { } end ? EndDateTime(end) : null,
            }.WithoutNulls()),
        };

        ops.AddRange(Targeting(campaign, geo));

        ops.Add(Op("adGroupOperation", new JsonObject
        {
            ["resourceName"] = adGroup,
            ["name"] = c.Name,
            ["campaign"] = campaign,
            ["status"] = "ENABLED",
            ["type"] = "SEARCH_STANDARD",
        }));

        ops.Add(Op("adGroupAdOperation", new JsonObject
        {
            ["adGroup"] = adGroup,
            ["status"] = "ENABLED",
            ["ad"] = new JsonObject
            {
                ["finalUrls"] = new JsonArray(c.FinalUrl),
                ["responsiveSearchAd"] = new JsonObject
                {
                    ["headlines"] = TextAssets(c.Headlines),
                    ["descriptions"] = TextAssets(c.Descriptions),
                },
            },
        }));

        foreach (var keyword in c.Keywords)
        {
            ops.Add(Op("adGroupCriterionOperation", new JsonObject
            {
                ["adGroup"] = adGroup,
                ["status"] = "ENABLED",
                // Phrase: the searcher's words must include the phrase, in
                // order. Broad would spend on loosely related searches far
                // from a local pharmacy's services.
                ["keyword"] = new JsonObject { ["text"] = keyword, ["matchType"] = "PHRASE" },
            }));
        }

        return ops;
    }

    /// <summary>
    /// Performance Max: text assets first (Google requires it), then one atomic
    /// request for budget, campaign, targeting, asset group, images and the
    /// brand assets that brand guidelines need at campaign level.
    /// </summary>
    private async Task<List<JsonObject>> PerformanceMaxOperationsAsync(
        GoogleAdsCampaign c, AdsAccess access, AdsCreative creative, IReadOnlyList<JsonObject> geo,
        CancellationToken ct)
    {
        var customerId = access.CustomerId;
        var (headlines, descriptions) = await CreateTextAssetsAsync(c, access, ct);

        var budget = $"customers/{customerId}/campaignBudgets/-1";
        var campaign = $"customers/{customerId}/campaigns/-2";
        var assetGroup = $"customers/{customerId}/assetGroups/-3";
        var nextAsset = -10L;
        string NewAsset() => $"customers/{customerId}/assets/{nextAsset--}";

        var tag = c.Id.Length >= 8 ? c.Id[..8] : c.Id;

        var ops = new List<JsonObject>
        {
            Op("campaignBudgetOperation", Budget(c, budget)),
            Op("campaignOperation", new JsonObject
            {
                ["resourceName"] = campaign,
                ["name"] = c.Name,
                ["status"] = "PAUSED",
                ["advertisingChannelType"] = "PERFORMANCE_MAX",
                ["campaignBudget"] = budget,
                // The only strategies Performance Max takes.
                ["maximizeConversions"] = new JsonObject(),
                ["brandGuidelinesEnabled"] = true,
                ["containsEuPoliticalAdvertising"] = "DOES_NOT_CONTAIN_EU_POLITICAL_ADVERTISING",
                ["startDateTime"] = StartDateTime(c.StartDate),
                ["endDateTime"] = c.EndDate is { } end ? EndDateTime(end) : null,
            }.WithoutNulls()),
        };

        ops.AddRange(Targeting(campaign, geo));

        ops.Add(Op("assetGroupOperation", new JsonObject
        {
            ["resourceName"] = assetGroup,
            ["name"] = c.Name,
            ["campaign"] = campaign,
            ["finalUrls"] = new JsonArray(c.FinalUrl),
            // Enabled: the campaign's PAUSED is what stops it serving. A
            // paused asset group would keep it dark after the pharmacy
            // deliberately started it.
            ["status"] = "ENABLED",
        }));

        foreach (var h in headlines) ops.Add(Link(assetGroup, h, "HEADLINE"));
        foreach (var d in descriptions) ops.Add(Link(assetGroup, d, "DESCRIPTION"));

        var longHeadline = NewAsset();
        ops.Add(Op("assetOperation", new JsonObject
        {
            ["resourceName"] = longHeadline,
            ["textAsset"] = new JsonObject { ["text"] = c.LongHeadline },
        }));
        ops.Add(Link(assetGroup, longHeadline, "LONG_HEADLINE"));

        var landscape = NewAsset();
        ops.Add(ImageAsset(landscape, $"{c.Name} landscape {tag}", creative.LandscapeImage));
        ops.Add(Link(assetGroup, landscape, "MARKETING_IMAGE"));

        var square = NewAsset();
        ops.Add(ImageAsset(square, $"{c.Name} square {tag}", creative.SquareImage));
        ops.Add(Link(assetGroup, square, "SQUARE_MARKETING_IMAGE"));

        // Brand guidelines (on by default for new Performance Max): business
        // name and logo belong to the campaign, and must arrive in this same
        // request or Google refuses the campaign.
        var logo = NewAsset();
        ops.Add(ImageAsset(logo, $"{c.Name} logo {tag}", creative.Logo));
        var businessName = NewAsset();
        ops.Add(Op("assetOperation", new JsonObject
        {
            ["resourceName"] = businessName,
            ["textAsset"] = new JsonObject { ["text"] = c.BusinessName },
        }));
        ops.Add(Op("campaignAssetOperation", new JsonObject
        {
            ["campaign"] = campaign, ["asset"] = logo, ["fieldType"] = "LOGO",
        }));
        ops.Add(Op("campaignAssetOperation", new JsonObject
        {
            ["campaign"] = campaign, ["asset"] = businessName, ["fieldType"] = "BUSINESS_NAME",
        }));

        return ops;
    }

    /// <summary>Headline and description assets, created ahead of the main request.</summary>
    private async Task<(List<string> Headlines, List<string> Descriptions)> CreateTextAssetsAsync(
        GoogleAdsCampaign c, AdsAccess access, CancellationToken ct)
    {
        var texts = c.Headlines.Concat(c.Descriptions).ToList();
        var body = new JsonObject
        {
            ["mutateOperations"] = new JsonArray(texts.Select(t => (JsonNode?)Op("assetOperation",
                new JsonObject { ["textAsset"] = new JsonObject { ["text"] = t } })).ToArray()),
            // Always real, even in validate-only mode. Google insists these
            // exist before the asset group, so the main request can only be
            // checked if they are real; an unlinked text asset shows nowhere
            // and costs nothing.
            ["validateOnly"] = false,
        };

        using var response = await SendAsync(HttpMethod.Post,
            Api($"customers/{access.CustomerId}/googleAds:mutate"), body,
            access.RefreshToken, access.LoginCustomerId, ct);

        var names = response.RootElement.TryGetProperty("mutateOperationResponses", out var results)
            ? results.EnumerateArray()
                .Select(r => r.TryGetProperty("assetResult", out var a) ? ResourceName(a) ?? string.Empty : string.Empty)
                .ToList()
            : new List<string>();

        if (names.Count != texts.Count || names.Any(string.IsNullOrEmpty))
            throw new GoogleAdsApiException("Google did not create all of the headlines and descriptions.");

        return (names.Take(c.Headlines.Count).ToList(), names.Skip(c.Headlines.Count).ToList());
    }

    /// <summary>Location criteria (resolved) plus English, for one campaign.</summary>
    private static IEnumerable<JsonObject> Targeting(string campaign, IReadOnlyList<JsonObject> geo)
    {
        foreach (var g in geo)
        {
            var criterion = (JsonObject)g.DeepClone();
            criterion["campaign"] = campaign;
            yield return Op("campaignCriterionOperation", criterion);
        }

        yield return Op("campaignCriterionOperation", new JsonObject
        {
            ["campaign"] = campaign,
            ["language"] = new JsonObject { ["languageConstant"] = LanguageEnglish },
        });
    }

    /// <summary>
    /// Turns the pharmacy's areas into criteria. Names ("Leeds", "LS6") are
    /// looked up with Google's own geo-target suggestion service, in the UK;
    /// one that matches nothing is an error naming it, never silently dropped
    /// — dropping the only area would leave an ad running nationwide.
    /// </summary>
    private async Task<IReadOnlyList<JsonObject>> ResolveLocationsAsync(
        IReadOnlyList<AdsLocationTarget> locations, AdsAccess access, CancellationToken ct)
    {
        var criteria = new List<JsonObject>();
        var toLookUp = new List<string>();

        foreach (var l in locations)
        {
            if (l.Latitude is { } lat && l.Longitude is { } lng && l.RadiusKm is { } km)
            {
                criteria.Add(new JsonObject
                {
                    ["proximity"] = new JsonObject
                    {
                        ["geoPoint"] = new JsonObject
                        {
                            ["latitudeInMicroDegrees"] = (long)Math.Round(lat * 1_000_000),
                            ["longitudeInMicroDegrees"] = (long)Math.Round(lng * 1_000_000),
                        },
                        ["radius"] = km,
                        ["radiusUnits"] = "KILOMETERS",
                    },
                });
            }
            else if (!string.IsNullOrWhiteSpace(l.GeoTargetConstantId))
            {
                criteria.Add(Location($"geoTargetConstants/{IdOf(l.GeoTargetConstantId)}"));
            }
            else if (!string.IsNullOrWhiteSpace(l.DisplayName))
            {
                toLookUp.Add(l.DisplayName.Trim());
            }
        }

        if (toLookUp.Count > 0)
        {
            var body = new JsonObject
            {
                ["locale"] = "en",
                ["countryCode"] = CountryCode,
                ["locationNames"] = new JsonObject
                {
                    ["names"] = new JsonArray(toLookUp.Select(n => (JsonNode?)n).ToArray()),
                },
            };

            using var response = await SendAsync(HttpMethod.Post, Api("geoTargetConstants:suggest"), body,
                access.RefreshToken, access.LoginCustomerId, ct);

            var suggestions = response.RootElement.TryGetProperty("geoTargetConstantSuggestions", out var s)
                ? s.EnumerateArray().ToList()
                : new List<JsonElement>();

            foreach (var name in toLookUp)
            {
                // Best enabled UK match for this term, by reach.
                var match = suggestions
                    .Where(x => string.Equals(Str(x, "searchTerm"), name, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.TryGetProperty("geoTargetConstant", out var g) ? (JsonElement?)g : null)
                    .Where(g => g is { } e
                                && Str(e, "countryCode") == CountryCode
                                && Str(e, "status") is null or "ENABLED")
                    .Select(g => g!.Value)
                    .FirstOrDefault();

                if (match.ValueKind != JsonValueKind.Object || Str(match, "resourceName") is not { } resource)
                    throw new GoogleAdsApiException(
                        $"Google could not find the area \"{name}\". Try a town, city or postcode district such as LS6.");

                criteria.Add(Location(resource));
            }
        }

        if (criteria.Count == 0)
            throw new GoogleAdsApiException("The campaign has no areas. Without one it would run everywhere.");

        return criteria;
    }

    /* ================================================================ *
     * Status and metrics
     * ================================================================ */

    public async Task<string?> GetAccountCurrencyAsync(AdsAccess access, CancellationToken ct)
    {
        var rows = await SearchAsync(access.CustomerId, access.LoginCustomerId,
            "SELECT customer.currency_code FROM customer LIMIT 1", access.RefreshToken, ct);
        var code = Text(rows.FirstOrDefault()?["customer"]?["currencyCode"]);
        return code.Length == 3 ? code : null;
    }

    public async Task<bool> SetCampaignStatusAsync(
        string campaignId, bool enabled, AdsAccess access, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["operations"] = new JsonArray(new JsonObject
            {
                ["update"] = new JsonObject
                {
                    ["resourceName"] = $"customers/{access.CustomerId}/campaigns/{IdOf(campaignId)}",
                    ["status"] = enabled ? "ENABLED" : "PAUSED",
                },
                ["updateMask"] = "status",
            }),
        };

        using var response = await SendAsync(HttpMethod.Post,
            Api($"customers/{access.CustomerId}/campaigns:mutate"), body,
            access.RefreshToken, access.LoginCustomerId, ct);

        return response.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0;
    }

    public async Task<AdsMetrics?> FetchMetricsAsync(string campaignId, AdsAccess access, CancellationToken ct)
    {
        // No date filter: lifetime totals, which is what the campaign card shows.
        var rows = await SearchAsync(access.CustomerId, access.LoginCustomerId,
            "SELECT metrics.impressions, metrics.clicks, metrics.cost_micros, metrics.conversions " +
            $"FROM campaign WHERE campaign.id = {long.Parse(IdOf(campaignId), CultureInfo.InvariantCulture)}",
            access.RefreshToken, ct);

        var metrics = rows.FirstOrDefault()?["metrics"];
        if (metrics is null) return null;

        return new AdsMetrics(
            Int(metrics["impressions"]),
            Int(metrics["clicks"]),
            Int(metrics["costMicros"]),
            (long)Math.Round(Dbl(metrics["conversions"])),
            DateTime.UtcNow);
    }

    /* ================================================================ *
     * Transport
     * ================================================================ */

    private async Task<List<JsonNode>> SearchAsync(
        string customerId, string? loginCustomerId, string query, string refreshToken, CancellationToken ct)
    {
        var rows = new List<JsonNode>();
        string? pageToken = null;

        // Small result sets here; a page cap stops a runaway loop.
        for (var page = 0; page < 10; page++)
        {
            var body = new JsonObject { ["query"] = query };
            if (pageToken is not null) body["pageToken"] = pageToken;

            using var response = await SendAsync(HttpMethod.Post,
                Api($"customers/{customerId}/googleAds:search"), body, refreshToken, loginCustomerId, ct);

            var node = JsonNode.Parse(response.RootElement.GetRawText());
            if (node?["results"] is JsonArray results)
                rows.AddRange(results.Where(r => r is not null).Select(r => r!.DeepClone()));

            pageToken = node?["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageToken)) break;
        }

        return rows;
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string url, JsonNode? body, string refreshToken, string? loginCustomerId,
        CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.GoogleAds);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(refreshToken, ct));
        // Legacy: access now comes from the Cloud project. Sent only if an
        // old setting still carries one (Google ignores it today).
        if (!string.IsNullOrWhiteSpace(_o.AdsDeveloperToken))
            request.Headers.Add("developer-token", _o.AdsDeveloperToken);
        if (!string.IsNullOrWhiteSpace(loginCustomerId))
            request.Headers.Add("login-customer-id", Digits(loginCustomerId));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw GoogleAdsApiException.From(response.StatusCode, text);

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    /// <summary>
    /// A short-lived access token for this grant, cached until shortly before
    /// it expires. Keyed by a hash of the refresh token, never the token.
    /// </summary>
    private async Task<string> AccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        var key = "gads-at:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        if (cache.TryGetValue(key, out string? cached) && cached is not null) return cached;

        var http = httpFactory.CreateClient(SocialHttpClients.Google);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _o.ClientId,
            ["client_secret"] = _o.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        });

        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("access_token", out var at))
        {
            var error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new GoogleAdsApiException(
                error == "invalid_grant"
                    ? "The connection to this Google account is no longer valid. Reconnect it in Social & Ads."
                    : "Google would not issue an access token for this account.",
                isAuthFailure: error == "invalid_grant");
        }

        var token = at.GetString()!;
        var lifetime = json.RootElement.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var secs)
            ? TimeSpan.FromSeconds(Math.Max(60, secs - 120))
            : TimeSpan.FromMinutes(30);

        cache.Set(key, token, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime, Size = 1 });
        return token;
    }

    /* ================================================================ *
     * JSON helpers
     * ================================================================ */

    private static JsonObject Op(string kind, JsonObject create) =>
        new() { [kind] = new JsonObject { ["create"] = create } };

    private static JsonObject Link(string assetGroup, string asset, string fieldType) =>
        Op("assetGroupAssetOperation", new JsonObject
        {
            ["assetGroup"] = assetGroup, ["asset"] = asset, ["fieldType"] = fieldType,
        });

    private static JsonObject ImageAsset(string resourceName, string name, byte[] data) =>
        Op("assetOperation", new JsonObject
        {
            ["resourceName"] = resourceName,
            // Google drops a new name silently when the same image already
            // exists under another; harmless, the asset is reused.
            ["name"] = name.Length > 120 ? name[..120] : name,
            ["imageAsset"] = new JsonObject { ["data"] = Convert.ToBase64String(data) },
        });

    private static JsonObject Location(string geoTargetConstant) =>
        new() { ["location"] = new JsonObject { ["geoTargetConstant"] = geoTargetConstant } };

    private static JsonObject Budget(GoogleAdsCampaign c, string resourceName)
    {
        var budget = new JsonObject
        {
            ["resourceName"] = resourceName,
            // Budget names are unique per account; the campaign's own id makes
            // this one so even when two campaigns share a name.
            ["name"] = $"{c.Name} — {c.Id}",
            ["deliveryMethod"] = "STANDARD",
            // Never shared: Performance Max and total budgets both refuse a
            // shared budget, and one campaign's budget is the pharmacy's
            // decision about that campaign only.
            ["explicitlyShared"] = false,
        };

        if (c.BudgetKind == AdsBudgetKind.Lifetime)
        {
            // A total over the campaign's dates (start and end are required,
            // and the domain insists on an end date).
            budget["period"] = "CUSTOM_PERIOD";
            budget["totalAmountMicros"] = c.BudgetMicros.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            budget["amountMicros"] = c.BudgetMicros.ToString(CultureInfo.InvariantCulture);
        }

        return budget;
    }

    private static JsonArray TextAssets(IEnumerable<string> texts) =>
        new(texts.Select(t => (JsonNode?)new JsonObject { ["text"] = t }).ToArray());

    /// <summary>Google's format: <c>yyyy-MM-dd HH:mm:ss</c>, in the account's time zone.</summary>
    private static string StartDateTime(DateOnly d) =>
        d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 00:00:00";

    private static string EndDateTime(DateOnly d) =>
        d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 23:59:59";

    private static string? ResourceName(JsonElement result) =>
        result.TryGetProperty("resourceName", out var r) ? r.GetString() : null;

    /// <summary>The last path segment of a resource name, or the value itself.</summary>
    private static string IdOf(string? resourceNameOrId) =>
        string.IsNullOrEmpty(resourceNameOrId) ? string.Empty
        : resourceNameOrId[(resourceNameOrId.LastIndexOf('/') + 1)..];

    private static string Digits(string id) => new(id.Where(char.IsDigit).ToArray());

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>REST sends int64 as strings and doubles as numbers.</summary>
    private static string Text(JsonNode? n) => n switch
    {
        null => string.Empty,
        JsonValue v when v.TryGetValue(out string? s) => s ?? string.Empty,
        _ => n.ToJsonString().Trim('"'),
    };

    private static long Int(JsonNode? n) =>
        long.TryParse(Text(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Dbl(JsonNode? n) =>
        double.TryParse(Text(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

internal static class JsonObjectExtensions
{
    /// <summary>Drops properties whose value is null — Google rejects explicit nulls on some fields.</summary>
    public static JsonObject WithoutNulls(this JsonObject o)
    {
        foreach (var key in o.Where(p => p.Value is null).Select(p => p.Key).ToList())
            o.Remove(key);
        return o;
    }
}

/// <summary>A refusal from the Google Ads API, in Google's own words.</summary>
public sealed class GoogleAdsApiException(string message, bool isAuthFailure = false, bool isProjectAccess = false)
    : Exception(message)
{
    public bool IsAuthFailure { get; } = isAuthFailure;

    /// <summary>Google refused this server's Cloud project (test-account access only), not the pharmacy.</summary>
    public bool IsProjectAccess { get; } = isProjectAccess;

    /// <summary>
    /// Reads Google's error body: the GoogleAdsFailure details, whose messages
    /// say exactly which field is wrong. The request id is logged by the caller's
    /// support process if needed; it is included in the message for that.
    /// </summary>
    public static GoogleAdsApiException From(HttpStatusCode status, string body)
    {
        var messages = new List<string>();
        string? requestId = null;
        var projectAccess = false;

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("details", out var details))
                {
                    foreach (var d in details.EnumerateArray())
                    {
                        if (d.TryGetProperty("requestId", out var rid)) requestId = rid.GetString();
                        if (!d.TryGetProperty("errors", out var errors)) continue;
                        foreach (var e in errors.EnumerateArray())
                        {
                            if (IsProjectAccessError(e)) projectAccess = true;
                            if (e.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } text)
                                messages.Add(Friendly(e, text));
                        }
                    }
                }

                if (messages.Count == 0 && error.TryGetProperty("message", out var top) && top.GetString() is { Length: > 0 } t)
                    messages.Add(t);
            }
        }
        catch (JsonException)
        {
            // Not Google's JSON — a gateway page. Fall through to the status.
        }

        // A 403 for the server's own Cloud project access is not the
        // pharmacy's sign-in: reconnecting would change nothing.
        var auth = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && !projectAccess;
        var summary = messages.Count > 0
            ? string.Join(" ", messages.Distinct().Take(5))
            : $"Google Ads returned {(int)status}.";

        return new GoogleAdsApiException(
            requestId is null ? summary : $"{summary} (Google request {requestId})", auth, projectAccess);
    }

    /// <summary>
    /// Google refusing this server's Cloud project (test-only access), not the
    /// pharmacy. v25+ says CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION; older
    /// versions DEVELOPER_TOKEN_NOT_APPROVED or a TEST_ACCOUNT error.
    /// </summary>
    private static bool IsProjectAccessError(JsonElement error)
    {
        var code = error.TryGetProperty("errorCode", out var c) ? c.GetRawText() : string.Empty;
        return code.Contains("CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION", StringComparison.Ordinal)
               || code.Contains("DEVELOPER_TOKEN_NOT_APPROVED", StringComparison.Ordinal)
               || code.Contains("TEST_ACCOUNT", StringComparison.Ordinal);
    }

    /// <summary>The few errors a pharmacy can act on, said in their terms.</summary>
    private static string Friendly(JsonElement error, string fallback)
    {
        var code = error.TryGetProperty("errorCode", out var c) ? c.GetRawText() : string.Empty;

        if (code.Contains("DUPLICATE_CAMPAIGN_NAME", StringComparison.Ordinal))
            return "You already have a campaign with that name in Google Ads. Choose another name.";
        // v25+: CLOUD_PROJECT_NOT_APPROVED_FOR_PRODUCTION. Older versions said
        // ACTION_NOT_PERMITTED or DEVELOPER_TOKEN_NOT_APPROVED for the same thing.
        if (IsProjectAccessError(error))
            return "This server's Google Ads access only works with test accounts so far " +
                   "(its Google Cloud project needs Basic access, requested on the project's Google Ads API page). " + fallback;
        if (code.Contains("ASPECT_RATIO_NOT_ALLOWED", StringComparison.Ordinal))
            return "Google refused one of the images' shape. " + fallback;

        return fallback;
    }
}
