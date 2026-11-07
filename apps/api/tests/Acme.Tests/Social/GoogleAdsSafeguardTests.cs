using System.Reflection;
using Acme.Application.Common;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// The checks around spending money: a campaign is switched on only by a
/// pharmacy whose plan allows it at that moment, and only in the Ads account
/// its connection belongs to.
/// </summary>
public class GoogleAdsSafeguardTests
{
    [Fact]
    public async Task A_pharmacy_that_dropped_to_free_cannot_switch_a_campaign_on()
    {
        var h = new Harness();
        var campaign = h.CreatedCampaign();
        h.Pharmacy.ChangeTier(SubscriptionTier.Free);

        var result = await h.Service.SetStatusAsync(h.Pharmacy.Id, campaign.Id, enabled: true, default);

        Assert.Equal(ResultKind.Forbidden, result.Kind);
        Assert.Equal(0, h.StatusChanges);
    }

    [Fact]
    public async Task Pausing_is_always_allowed_because_it_stops_spending()
    {
        var h = new Harness();
        var campaign = h.CreatedCampaign();
        h.Pharmacy.ChangeTier(SubscriptionTier.Free);

        var result = await h.Service.SetStatusAsync(h.Pharmacy.Id, campaign.Id, enabled: false, default);

        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.Equal(1, h.StatusChanges);
    }

    [Fact]
    public async Task A_campaign_goes_in_the_connections_own_ads_account()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), customerId: null, default);

        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.Equal("1234567890", h.CreatedFor);
    }

    [Fact]
    public async Task Naming_a_different_ads_account_is_refused_before_google_is_called()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), "999-999-9999", default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Null(h.CreatedFor);
    }

    [Fact]
    public async Task The_same_account_written_with_dashes_is_accepted()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), "123-456-7890", default);

        Assert.Equal(ResultKind.Success, result.Kind);
    }

    [Fact]
    public async Task A_campaign_with_no_area_is_refused_because_it_would_run_everywhere()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(
            h.Pharmacy.Id, h.Request(Array.Empty<AdsLocationDto>()), null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Null(h.CreatedFor);
    }

    [Fact]
    public async Task A_radius_over_80km_is_refused()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id,
            h.Request(new[] { new AdsLocationDto(null, "Shop", 53.8, -1.55, 500) }), null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task Too_few_headlines_are_refused_before_google_is_called()
    {
        var h = new Harness();
        var request = h.Request() with { Headlines = new[] { "Flu jabs" } };

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, request, null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Contains("3 headlines", result.Error);
    }

    [Fact]
    public async Task Performance_max_is_sent_with_its_images()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.PerformanceMax(), null, default);

        Assert.Equal(ResultKind.Success, result.Kind);
        Assert.NotNull(h.CreatedWith);
    }

    [Fact]
    public async Task Performance_max_without_a_logo_is_refused_and_nothing_is_saved()
    {
        var h = new Harness { Creative = Result<AdsCreative>.Invalid("Performance Max needs your logo.") };

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.PerformanceMax(), null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Contains("logo", result.Error);
        Assert.Empty(h.Campaigns);
        Assert.Null(h.CreatedFor);
    }

    [Fact]
    public async Task Search_needs_search_terms()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request() with { Keywords = null }, null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Contains("search term", result.Error);
    }

    [Fact]
    public async Task Search_campaigns_never_get_images()
    {
        var h = new Harness();

        await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), null, default);

        Assert.Null(h.CreatedWith);
    }

    [Fact]
    public async Task A_total_budget_needs_an_end_date()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id,
            h.Request() with { BudgetKind = AdsBudgetKind.Lifetime, EndDate = null }, null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task Demand_gen_is_refused_until_it_is_supported()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id,
            h.Request() with { CampaignType = AdsCampaignType.DemandGen }, null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
    }

    [Fact]
    public async Task A_budget_in_the_wrong_currency_is_refused()
    {
        // A US-dollar test account given "5" would spend $5 against a £5 label.
        var h = new Harness { AccountCurrency = "USD" };

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Contains("USD", result.Error);
        Assert.Null(h.CreatedFor);
    }

    [Fact]
    public async Task A_budget_with_an_extra_zero_is_refused()
    {
        var h = new Harness();

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request() with { Budget = 5000m }, null, default);

        Assert.Equal(ResultKind.Invalid, result.Kind);
        Assert.Null(h.CreatedFor);
    }

    [Fact]
    public async Task A_validate_only_check_leaves_no_campaign_record()
    {
        var h = new Harness { ValidateOnly = true };

        var result = await h.Service.CreateAsync(h.Pharmacy.Id, h.Request(), null, default);

        Assert.Contains("nothing was created", result.Error);
        Assert.Empty(h.Campaigns);
    }

    private static readonly string[] Headlines = { "Flu jabs now", "Walk in today", "Free for over-65s" };
    private static readonly string[] Descriptions =
    {
        "Book your flu jab at your local pharmacy.",
        "Quick, friendly, no appointment needed most days.",
    };

    /* ================================================================ */

    private sealed class Harness
    {
        public User Pharmacy { get; } = Paid();
        public SocialConnection Connection { get; }
        public Dictionary<string, GoogleAdsCampaign> Campaigns { get; } = new();
        public int StatusChanges { get; private set; }
        public string? CreatedFor { get; private set; }

        public Harness()
        {
            Connection = SocialConnection.Create(
                Pharmacy.Id, SocialPlatform.GoogleAds, "1234567890", "Ads",
                EncryptedSecret.Create("access", "n", "t", 1),
                EncryptedSecret.Create("refresh", "n", "t", 1), null);
        }

        public GoogleAdsService Service => new(
            Answer<IGoogleAdsCampaignRepository>.With(new()
            {
                ["FindByIdAsync"] = a => Task.FromResult(Campaigns.GetValueOrDefault((string)a[0]!)),
                ["AddAsync"] = a => { var c = (GoogleAdsCampaign)a[0]!; Campaigns[c.Id] = c; return Task.CompletedTask; },
                ["UpdateAsync"] = _ => Task.CompletedTask,
                ["DeleteAsync"] = a => { Campaigns.Remove((string)a[0]!); return Task.CompletedTask; },
            }),
            Answer<ISocialConnectionRepository>.With(new()
            {
                ["FindByIdAsync"] = a => Task.FromResult<SocialConnection?>(
                    (string)a[0]! == Connection.Id ? Connection : null),
            }),
            OAuthFlowTests.UsersOf.These(Pharmacy),
            new OAuthFlowTests.PlainCipher(),
            Answer<IPublishMediaResolver>.With(new()
            {
                ["ResolveAsync"] = _ => Task.FromResult<ResolvedMedia?>(
                    new ResolvedMedia("https://blob/poster.png", IsVideo: false, "Flu clinic")),
            }),
            Answer<IGoogleAdsClient>.With(new()
            {
                ["get_IsConfigured"] = _ => true,
                ["SetCampaignStatusAsync"] = _ => { StatusChanges++; return Task.FromResult(true); },
                ["GetAccountCurrencyAsync"] = _ => Task.FromResult<string?>(AccountCurrency),
                ["CreateCampaignAsync"] = a =>
                {
                    CreatedFor = ((GoogleAdsCampaign)a[0]!).CustomerId;
                    CreatedWith = (AdsCreative?)a[2];
                    return Task.FromResult(ValidateOnly
                        ? new AdsCreateResult(false, null, null, null, "Checked by Google and valid — nothing was created.", ValidatedOnly: true)
                        : new AdsCreateResult(true, "camp-1", null, null, null));
                },
            }),
            Answer<IAdsCreativeBuilder>.With(new()
            {
                ["BuildAsync"] = _ => Task.FromResult(Creative),
            }),
            NullLogger<GoogleAdsService>.Instance);

        public AdsCreative? CreatedWith { get; private set; }

        public string? AccountCurrency { get; set; } = "GBP";

        public bool ValidateOnly { get; set; }

        /// <summary>What the creative builder answers. Defaults to a complete set.</summary>
        public Result<AdsCreative> Creative { get; set; } =
            Result<AdsCreative>.Success(new AdsCreative(new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 }));

        public GoogleAdsCampaign CreatedCampaign()
        {
            var c = GoogleAdsCampaign.Draft(
                Pharmacy.Id, Connection.Id, "1234567890", "Flu jabs", AdsCampaignType.Search,
                AdsBudgetKind.Daily, 5_000_000, "GBP", DateOnly.FromDateTime(DateTime.UtcNow), null,
                "https://pharmacy.example/flu", PublishSourceKind.Poster, "poster-1",
                new[] { new AdsLocationTarget(null, "Leeds", null, null, null) },
                Headlines, Descriptions, new[] { "flu jab" });
            c.MarkCreated("camp-1", null, null);
            Campaigns[c.Id] = c;
            return c;
        }

        public CreateAdsCampaignRequest Request(IReadOnlyList<AdsLocationDto>? locations = null) => new(
            Connection.Id, "Flu jabs", AdsCampaignType.Search, AdsBudgetKind.Daily, 5m,
            DateOnly.FromDateTime(DateTime.UtcNow), null, "https://pharmacy.example/flu",
            PublishSourceKind.Poster, "poster-1",
            locations ?? new[] { new AdsLocationDto(null, "Leeds", null, null, null) },
            Headlines, Descriptions,
            Keywords: new[] { "flu jab" });

        public CreateAdsCampaignRequest PerformanceMax() => Request() with
        {
            CampaignType = AdsCampaignType.PerformanceMax,
            Keywords = null,
            LongHeadline = "Flu jabs at your local pharmacy — walk in today",
            BusinessName = "Main St Pharmacy",
        };

        private static User Paid()
        {
            var u = User.Register(Email.Create($"ads-{Guid.NewGuid():N}@example.test"), "Pharmacy", PasswordHash.FromHash("x"));
            u.ChangeTier(SubscriptionTier.Starter);
            return u;
        }
    }

    /// <summary>A fake that answers only the calls a test names, and refuses the rest.</summary>
    public class Answer<T> : DispatchProxy where T : class
    {
        private Dictionary<string, Func<object?[], object?>> _answers = new();

        public static T With(Dictionary<string, Func<object?[], object?>> answers)
        {
            var proxy = Create<T, Answer<T>>();
            ((Answer<T>)(object)proxy)._answers = answers;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is not null && _answers.TryGetValue(targetMethod.Name, out var answer)
                ? answer(args ?? Array.Empty<object?>())
                : throw new NotSupportedException($"{typeof(T).Name}.{targetMethod?.Name} was not expected.");
    }
}
