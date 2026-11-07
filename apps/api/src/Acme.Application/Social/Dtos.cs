using Acme.Domain.Social;

namespace Acme.Application.Social;

/* ------------------------------------------------------------------ *
 * Connections
 * ------------------------------------------------------------------ */

/// <summary>
/// A connected account as the pharmacy sees it.
/// </summary>
/// <remarks>
/// There is no token field, and there must never be one. The frontend has no
/// use for a credential, and a DTO with somewhere to put one is how it
/// eventually ends up in a browser's network log. If something in the UI seems
/// to need the token, it needs a new endpoint instead.
/// </remarks>
public sealed record SocialConnectionDto(
    string Id,
    SocialPlatform Platform,
    string PlatformName,
    string ExternalAccountId,
    string DisplayName,
    string? AvatarUrl,
    ConnectionStatus Status,
    DateTime? ExpiresAtUtc,
    DateTime? LastVerifiedAtUtc,
    string? LastError,
    bool CanPublish,
    bool SupportsVideo,
    DateTime CreatedAtUtc,
    /// <summary>False for video-only platforms (YouTube): posters are not offered there.</summary>
    bool SupportsImages = true);

/// <summary>An account offered during OAuth, before the pharmacy picks one.</summary>
/// <remarks>
/// Held server-side between the callback and the pharmacy's choice, keyed by a
/// short-lived handle. The credentials stay on the server throughout; the
/// browser only ever sees the handle and the names.
/// </remarks>
public sealed record DiscoveredAccountDto(
    string Handle,
    string ExternalAccountId,
    SocialPlatform Platform,
    string PlatformName,
    string DisplayName,
    string? AvatarUrl,
    bool AlreadyConnected);

public sealed record OAuthStartDto(string AuthorizationUrl, string State);

/// <param name="PlanAllows">False on plans without social publishing.</param>
/// <param name="PlanReason">What to tell the pharmacy when it does not.</param>
/// <param name="Providers">Whether each Connect button can work, and why not.</param>
public sealed record SocialAvailabilityDto(
    bool PlanAllows,
    string? PlanReason,
    IReadOnlyList<SocialProviderAvailabilityDto> Providers);

public sealed record SocialProviderAvailabilityDto(
    SocialOAuthProviderKind Kind,
    bool Available,
    string? Reason);

public sealed record ConnectAccountsRequest(string SessionId, IReadOnlyList<string> Handles);

/* ------------------------------------------------------------------ *
 * Publishing
 * ------------------------------------------------------------------ */

public sealed record PublishTargetDto(
    string ConnectionId,
    SocialPlatform Platform,
    string PlatformName,
    string AccountName,
    PublishTargetStatus Status,
    string? ExternalPostId,
    string? Permalink,
    string? Error,
    int AttemptCount,
    DateTime? PublishedAtUtc);

public sealed record ScheduledPostDto(
    string Id,
    PublishSourceKind SourceKind,
    string SourceId,
    string SourceName,
    string Caption,
    DateTime? ScheduledForUtc,
    PublishStatus Status,
    IReadOnlyList<PublishTargetDto> Targets,
    GbpPostOptions? GbpOptions,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc,
    bool CanRetry,
    bool CanCancel,
    /// <summary>When an automatic retry is waiting out its backoff, when it will run.</summary>
    DateTime? NextAttemptAtUtc = null,
    /// <summary>The recurring series this post is an occurrence of, or null for a one-off.</summary>
    string? SeriesId = null,
    TikTokPostOptions? TikTokOptions = null);

public sealed record ScheduledPostListDto(
    IReadOnlyList<ScheduledPostDto> Items,
    long Total,
    int Skip,
    int Take);

public sealed record CreatePublishRequest(
    PublishSourceKind SourceKind,
    string SourceId,
    string Caption,
    IReadOnlyList<string> ConnectionIds,

    /// <summary>
    /// UTC instant, or null to publish now.
    /// </summary>
    /// <remarks>
    /// The client converts from the pharmacy's local time. Accepting a
    /// wall-clock string here would make the server guess a timezone, and it
    /// would guess wrong twice a year.
    /// </remarks>
    DateTime? ScheduledForUtc = null,

    GbpPostOptions? GbpOptions = null,

    /// <summary>Required when a TikTok account is among the targets.</summary>
    TikTokPostOptions? TikTokOptions = null);

public sealed record ReschedulePostRequest(DateTime? ScheduledForUtc);

/* ------------------------------------------------------------------ *
 * Google Ads
 * ------------------------------------------------------------------ */

public sealed record AdsLocationDto(
    string? GeoTargetConstantId,
    string? DisplayName,
    double? Latitude,
    double? Longitude,
    int? RadiusKm);

public sealed record AdsMetricsDto(
    long Impressions,
    long Clicks,
    decimal Cost,
    long Conversions,
    decimal ClickThroughRate,
    decimal CostPerClick,
    DateTime LastSyncedAtUtc);

public sealed record GoogleAdsCampaignDto(
    string Id,
    string ConnectionId,
    string CustomerId,
    string Name,
    AdsCampaignType CampaignType,
    AdsCampaignStatus Status,
    string? ExternalCampaignId,
    AdsBudgetKind BudgetKind,
    decimal Budget,
    string CurrencyCode,
    DateOnly StartDate,
    DateOnly? EndDate,
    string FinalUrl,
    IReadOnlyList<AdsLocationDto> Locations,
    PublishSourceKind CreativeKind,
    string CreativeSourceId,
    IReadOnlyList<string> Headlines,
    IReadOnlyList<string> Descriptions,
    AdsMetricsDto Metrics,
    string? LastError,
    DateTime CreatedAtUtc,
    IReadOnlyList<string>? Keywords = null,
    string? LongHeadline = null,
    string? BusinessName = null);

public sealed record CreateAdsCampaignRequest(
    string ConnectionId,
    string Name,
    AdsCampaignType CampaignType,
    AdsBudgetKind BudgetKind,

    /// <summary>In whole currency units. Converted to micros before it reaches Google.</summary>
    decimal Budget,

    DateOnly StartDate,
    DateOnly? EndDate,
    string FinalUrl,
    PublishSourceKind CreativeKind,
    string CreativeSourceId,
    IReadOnlyList<AdsLocationDto>? Locations = null,
    IReadOnlyList<string>? Headlines = null,
    IReadOnlyList<string>? Descriptions = null,
    string CurrencyCode = "GBP",

    /// <summary>Search: what people type. 1–20, up to 80 characters each.</summary>
    IReadOnlyList<string>? Keywords = null,

    /// <summary>Performance Max: one long headline, up to 90 characters.</summary>
    string? LongHeadline = null,

    /// <summary>Performance Max: the business name shown in the ad, up to 25 characters.</summary>
    string? BusinessName = null);

public sealed record AdsAccountDto(string CustomerId, string DescriptiveName, string CurrencyCode);
