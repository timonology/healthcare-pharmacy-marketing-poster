using Acme.Domain.Common;

namespace Acme.Domain.Social;

/// <summary>Campaign types worth offering a single-site pharmacy.</summary>
/// <remarks>
/// Deliberately three, not the full Google Ads catalogue. Shopping needs a
/// Merchant Center feed, App is irrelevant, and Video-only wants a YouTube
/// channel the pharmacy probably has not set up. Offering types that cannot
/// succeed is worse than offering fewer.
/// </remarks>
public enum AdsCampaignType
{
    /// <summary>Text ads on search results. The dependable default for "pharmacy near me".</summary>
    Search = 0,

    /// <summary>
    /// Google picks placements across its network from supplied assets.
    /// Uses the poster or video directly, which is why it suits this product.
    /// </summary>
    PerformanceMax = 1,

    /// <summary>Image and video ads in Discover, Gmail and YouTube feeds.</summary>
    DemandGen = 2,
}

public enum AdsBudgetKind
{
    Daily = 0,
    Lifetime = 1,
}

public enum AdsCampaignStatus
{
    /// <summary>Composed here, not yet sent to Google.</summary>
    Draft = 0,

    /// <summary>Being created at Google. Transient.</summary>
    Creating = 1,

    Enabled = 2,
    Paused = 3,

    /// <summary>Removed at Google. Terminal — Google Ads never truly deletes.</summary>
    Removed = 4,

    /// <summary>Creation was rejected. <see cref="GoogleAdsCampaign.LastError"/> says why.</summary>
    Failed = 5,
}

/// <summary>
/// Geographic targeting for a campaign.
/// </summary>
/// <remarks>
/// Two shapes because pharmacies think in both. A radius around the shop is the
/// natural one; a named place matters when the catchment is "this town" rather
/// than "three miles". <c>GeoTargetConstantId</c> is Google's own identifier,
/// resolved through their geo target lookup rather than typed by hand.
/// </remarks>
public sealed record AdsLocationTarget(
    string? GeoTargetConstantId,
    string? DisplayName,
    double? Latitude,
    double? Longitude,
    int? RadiusKm);

/// <summary>Metrics as of the last sync. Never computed here.</summary>
/// <remarks>
/// A snapshot, explicitly. Google is the source of truth and its numbers move
/// for days after the fact as invalid traffic is filtered — so this carries
/// <see cref="LastSyncedAtUtc"/> and the UI shows it. A figure with no "as of"
/// invites someone to reconcile it against the Google Ads console and conclude
/// that one of them is broken.
/// </remarks>
public sealed record AdsMetrics(
    long Impressions,
    long Clicks,
    long CostMicros,
    long Conversions,
    DateTime LastSyncedAtUtc)
{
    public static AdsMetrics Empty() => new(0, 0, 0, 0, DateTime.UtcNow);

    /// <summary>Cost in whole currency units. Google reports micros — millionths.</summary>
    public decimal CostInCurrency => CostMicros / 1_000_000m;
}

/// <summary>
/// A Google Ads campaign created from one of the pharmacy's own posters or videos.
/// </summary>
/// <remarks>
/// <para>
/// This record mirrors a campaign that lives at Google; it does not own it.
/// <see cref="ExternalCampaignId"/> is the join, and where the two disagree
/// Google wins. Anything edited directly in the Google Ads console will differ
/// from what is stored here until the next sync, and the UI says so rather than
/// pretending this is authoritative.
/// </para>
/// <para>
/// Budgets are held in micros, matching the API, so no rounding happens on the
/// way in or out. A budget entered as £12.34 is 12_340_000 and comes back
/// identical.
/// </para>
/// </remarks>
public sealed class GoogleAdsCampaign : Entity
{
    public string OwnerId { get; private set; } = default!;

    /// <summary>The Google Ads connection this was created through.</summary>
    public string ConnectionId { get; private set; } = default!;

    /// <summary>Google Ads customer id, digits only, no dashes.</summary>
    public string CustomerId { get; private set; } = default!;

    public string Name { get; private set; } = default!;
    public AdsCampaignType CampaignType { get; private set; }
    public AdsCampaignStatus Status { get; private set; } = AdsCampaignStatus.Draft;

    /// <summary>Google's campaign id once created. Null while still a draft.</summary>
    public string? ExternalCampaignId { get; private set; }

    /// <summary>Google's budget resource id, needed to change the budget later.</summary>
    public string? ExternalBudgetId { get; private set; }

    public AdsBudgetKind BudgetKind { get; private set; }
    public long BudgetMicros { get; private set; }
    public string CurrencyCode { get; private set; } = "GBP";

    public DateOnly StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }

    public string FinalUrl { get; private set; } = default!;

    private readonly List<AdsLocationTarget> _locations = new();
    public IReadOnlyList<AdsLocationTarget> Locations => _locations;

    /// <summary>The poster or video used as the creative.</summary>
    public PublishSourceKind CreativeKind { get; private set; }
    public string CreativeSourceId { get; private set; } = default!;

    /// <summary>Google's asset id after upload, so a re-run does not upload twice.</summary>
    public string? ExternalAssetId { get; private set; }

    /// <summary>Headlines and descriptions for Search / PMax asset groups.</summary>
    public IReadOnlyList<string> Headlines { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> Descriptions { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Search terms the ad answers. Search campaigns only — without keywords a
    /// Search campaign has nothing to show against and never serves.
    /// </summary>
    public IReadOnlyList<string> Keywords { get; private set; } = Array.Empty<string>();

    /// <summary>Performance Max: the one long headline Google requires (≤ 90).</summary>
    public string? LongHeadline { get; private set; }

    /// <summary>Performance Max: the advertiser's name as shown in the ad (≤ 25).</summary>
    public string? BusinessName { get; private set; }

    public AdsMetrics Metrics { get; private set; } = AdsMetrics.Empty();
    public string? LastError { get; private set; }

    private GoogleAdsCampaign() { }

    public static GoogleAdsCampaign Draft(
        string ownerId,
        string connectionId,
        string customerId,
        string name,
        AdsCampaignType campaignType,
        AdsBudgetKind budgetKind,
        long budgetMicros,
        string currencyCode,
        DateOnly startDate,
        DateOnly? endDate,
        string finalUrl,
        PublishSourceKind creativeKind,
        string creativeSourceId,
        IEnumerable<AdsLocationTarget>? locations = null,
        IEnumerable<string>? headlines = null,
        IEnumerable<string>? descriptions = null,
        IEnumerable<string>? keywords = null,
        string? longHeadline = null,
        string? businessName = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new DomainException("Connect a Google Ads account first.");
        if (string.IsNullOrWhiteSpace(customerId))
            throw new DomainException("A Google Ads customer id is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Campaign name is required.");
        if (name.Length > 128)
            throw new DomainException("Campaign name must be 128 characters or fewer.");

        // A floor, not a guess. Google rejects budgets below its own minimum
        // anyway; catching it here means the pharmacy is told before we spend
        // a round trip and a rejected campaign explaining it.
        if (budgetMicros < 1_000_000)
            throw new DomainException("Budget must be at least 1.00 per day.");

        // Google refuses a start in the past. A day's grace covers the
        // pharmacy being a timezone ahead of the server.
        if (startDate < DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)))
            throw new DomainException("The start date is in the past.");

        if (endDate is { } end && end < startDate)
            throw new DomainException("End date cannot be before the start date.");

        ValidateFinalUrl(finalUrl);

        var targets = (locations ?? Array.Empty<AdsLocationTarget>()).ToList();
        ValidateLocations(targets);

        var cleanHeadlines = Clean(headlines, 30, 15);
        var cleanDescriptions = Clean(descriptions, 90, 5);

        // Google's own minimums for responsive ads, which every type here
        // uses. Refused now, with numbers, rather than by Google later with a
        // policy code.
        if (cleanHeadlines.Count < 3)
            throw new DomainException("Add at least 3 headlines (up to 30 characters each).");
        if (cleanDescriptions.Count < 2)
            throw new DomainException("Add at least 2 descriptions (up to 90 characters each).");

        // A total budget is spread over a known period; Google refuses one
        // without an end date.
        if (budgetKind == AdsBudgetKind.Lifetime && endDate is null)
            throw new DomainException("A total budget needs an end date.");

        var cleanKeywords = Clean(keywords, 80, 20);
        var cleanLongHeadline = string.IsNullOrWhiteSpace(longHeadline) ? null : longHeadline.Trim();
        var cleanBusinessName = string.IsNullOrWhiteSpace(businessName) ? null : businessName.Trim();

        switch (campaignType)
        {
            case AdsCampaignType.Search:
                if (cleanKeywords.Count == 0)
                    throw new DomainException(
                        "Add at least one search term — what people type when they should see this ad.");
                break;

            case AdsCampaignType.PerformanceMax:
                if (creativeKind != PublishSourceKind.Poster)
                    throw new DomainException(
                        "Performance Max uses a poster. Videos need to be on YouTube first, which is not supported yet.");
                if (cleanLongHeadline is null || cleanLongHeadline.Length > 90)
                    throw new DomainException("Add a long headline of up to 90 characters.");
                if (cleanBusinessName is null || cleanBusinessName.Length > 25)
                    throw new DomainException("Add your business name as it should appear, up to 25 characters.");
                break;

            default:
                throw new DomainException("That campaign type is not available yet. Choose Search or Performance Max.");
        }

        var campaign = new GoogleAdsCampaign
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            ConnectionId = connectionId,
            CustomerId = new string(customerId.Where(char.IsDigit).ToArray()),
            Name = name.Trim(),
            CampaignType = campaignType,
            BudgetKind = budgetKind,
            BudgetMicros = budgetMicros,
            CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "GBP" : currencyCode.Trim().ToUpperInvariant(),
            StartDate = startDate,
            EndDate = endDate,
            FinalUrl = finalUrl.Trim(),
            CreativeKind = creativeKind,
            CreativeSourceId = creativeSourceId,
            Headlines = cleanHeadlines,
            Descriptions = cleanDescriptions,
            Keywords = campaignType == AdsCampaignType.Search ? cleanKeywords : Array.Empty<string>(),
            LongHeadline = campaignType == AdsCampaignType.PerformanceMax ? cleanLongHeadline : null,
            BusinessName = campaignType == AdsCampaignType.PerformanceMax ? cleanBusinessName : null,
            Status = AdsCampaignStatus.Draft,
        };

        foreach (var location in targets)
            campaign._locations.Add(location);

        return campaign;
    }

    public void MarkCreating()
    {
        Status = AdsCampaignStatus.Creating;
        LastError = null;
        Touch();
    }

    public void MarkCreated(string externalCampaignId, string? externalBudgetId, string? externalAssetId)
    {
        if (string.IsNullOrWhiteSpace(externalCampaignId))
            throw new DomainException("Google did not return a campaign id.");

        ExternalCampaignId = externalCampaignId;
        ExternalBudgetId = externalBudgetId;
        ExternalAssetId = externalAssetId;
        // Created paused, always. A campaign that starts spending the instant
        // it is created gives nobody a chance to check the targeting, and the
        // money is gone before the mistake is noticed.
        Status = AdsCampaignStatus.Paused;
        LastError = null;
        Touch();
    }

    public void MarkFailed(string error)
    {
        Status = AdsCampaignStatus.Failed;
        LastError = Truncate(error, 1000);
        Touch();
    }

    public void MarkEnabled()
    {
        if (ExternalCampaignId is null)
            throw new DomainException("This campaign has not been created at Google yet.");
        Status = AdsCampaignStatus.Enabled;
        Touch();
    }

    public void MarkPaused()
    {
        if (ExternalCampaignId is null)
            throw new DomainException("This campaign has not been created at Google yet.");
        Status = AdsCampaignStatus.Paused;
        Touch();
    }

    public void MarkRemoved()
    {
        Status = AdsCampaignStatus.Removed;
        Touch();
    }

    public void UpdateMetrics(AdsMetrics metrics)
    {
        Metrics = metrics;
        Touch();
    }

    /// <summary>
    /// Rejects anything that is not an absolute http(s) URL.
    /// </summary>
    /// <remarks>
    /// Note the ordering: the leading-slash test comes first. On Unix,
    /// <c>Uri.TryCreate("/foo", UriKind.Absolute, out _)</c> returns true with
    /// scheme "file", so a relative path would otherwise pass an absolute-URI
    /// check and then be rejected by Google with an opaque message. This
    /// codebase has been bitten by exactly that before, in
    /// <c>TemplateAssetUrls</c>.
    /// </remarks>
    private static void ValidateFinalUrl(string finalUrl)
    {
        if (string.IsNullOrWhiteSpace(finalUrl))
            throw new DomainException("A landing page URL is required — this is where the ad sends people.");

        var trimmed = finalUrl.Trim();

        if (trimmed.StartsWith('/'))
            throw new DomainException("The landing page must be a full URL including https://");

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            throw new DomainException("The landing page must be a full URL including https://");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new DomainException("The landing page must start with http:// or https://");
    }

    /// <summary>
    /// At least one place, and each one complete.
    /// </summary>
    /// <remarks>
    /// A campaign with no location targets runs in every country Google
    /// serves. For a pharmacy that is not a broad campaign, it is money spent
    /// on people who cannot walk in — so it is refused outright.
    /// </remarks>
    private static void ValidateLocations(IReadOnlyList<AdsLocationTarget> locations)
    {
        if (locations.Count == 0)
            throw new DomainException(
                "Choose at least one area. Without one, the ad would run everywhere Google serves ads.");
        if (locations.Count > 20)
            throw new DomainException("Choose 20 areas or fewer.");

        foreach (var l in locations)
        {
            var radius = l.Latitude is not null && l.Longitude is not null;
            var named = !string.IsNullOrWhiteSpace(l.GeoTargetConstantId) || !string.IsNullOrWhiteSpace(l.DisplayName);

            if (!radius && !named)
                throw new DomainException("Each area needs a place name, a postcode district, or a point and radius.");

            if (radius)
            {
                if (l.Latitude is < -90 or > 90 || l.Longitude is < -180 or > 180)
                    throw new DomainException("That location is not a valid point on the map.");
                if (l.RadiusKm is not (>= 1 and <= 80))
                    throw new DomainException("A radius must be between 1 and 80 km.");
            }

            if (l.DisplayName is { Length: > 100 })
                throw new DomainException("Area names must be 100 characters or fewer.");
        }
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string>? values, int maxLength, int maxCount) =>
        (values ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Select(v => v.Length <= maxLength ? v : v[..maxLength])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];
}
