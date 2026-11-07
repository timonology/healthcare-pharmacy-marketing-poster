using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Ciphertext as stored. Deliberately has no relationship to the plaintext
/// beyond being the output of the cipher — nothing here is readable without a
/// configured key.
/// </summary>
internal sealed class EncryptedSecretDocument
{
    [BsonElement("cipherText")] public string CipherText { get; set; } = default!;
    [BsonElement("nonce")] public string Nonce { get; set; } = default!;
    [BsonElement("tag")] public string Tag { get; set; } = default!;

    /// <summary>Which key encrypted this. Without it, rotation is impossible.</summary>
    [BsonElement("keyVersion")] public int KeyVersion { get; set; }
}

internal sealed class SocialConnectionDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("platform")] public int Platform { get; set; }
    [BsonElement("externalAccountId")] public string ExternalAccountId { get; set; } = default!;
    [BsonElement("displayName")] public string DisplayName { get; set; } = default!;
    [BsonElement("avatarUrl")] public string? AvatarUrl { get; set; }

    [BsonElement("status")]
    [BsonDefaultValue(0)]
    public int Status { get; set; }

    [BsonElement("accessToken")] public EncryptedSecretDocument? AccessToken { get; set; }
    [BsonElement("refreshToken")] public EncryptedSecretDocument? RefreshToken { get; set; }
    [BsonElement("expiresAtUtc")] public DateTime? ExpiresAtUtc { get; set; }
    [BsonElement("scopes")] public List<string> Scopes { get; set; } = new();
    [BsonElement("parentAccountId")] public string? ParentAccountId { get; set; }
    [BsonElement("authorizedByUserId")] public string? AuthorizedByUserId { get; set; }
    [BsonElement("grantFingerprint")] public string? GrantFingerprint { get; set; }
    [BsonElement("lastVerifiedAtUtc")] public DateTime? LastVerifiedAtUtc { get; set; }
    [BsonElement("lastHealthCheckAtUtc")] public DateTime? LastHealthCheckAtUtc { get; set; }
    [BsonElement("lastError")] public string? LastError { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class PublishTargetDocument
{
    [BsonElement("connectionId")] public string ConnectionId { get; set; } = default!;
    [BsonElement("platform")] public int Platform { get; set; }
    [BsonElement("status")] public int Status { get; set; }
    [BsonElement("externalPostId")] public string? ExternalPostId { get; set; }
    [BsonElement("permalink")] public string? Permalink { get; set; }
    [BsonElement("error")] public string? Error { get; set; }
    [BsonElement("attemptCount")] public int AttemptCount { get; set; }
    [BsonElement("lastAttemptAtUtc")] public DateTime? LastAttemptAtUtc { get; set; }
    [BsonElement("publishedAtUtc")] public DateTime? PublishedAtUtc { get; set; }
}

internal sealed class GbpPostOptionsDocument
{
    [BsonElement("postType")] public int PostType { get; set; }
    [BsonElement("couponCode")] public string? CouponCode { get; set; }
    [BsonElement("redeemOnlineUrl")] public string? RedeemOnlineUrl { get; set; }
    [BsonElement("termsConditions")] public string? TermsConditions { get; set; }
    [BsonElement("eventTitle")] public string? EventTitle { get; set; }
    [BsonElement("eventStartUtc")] public DateTime? EventStartUtc { get; set; }
    [BsonElement("eventEndUtc")] public DateTime? EventEndUtc { get; set; }
    [BsonElement("callToActionType")] public string? CallToActionType { get; set; }
    [BsonElement("callToActionUrl")] public string? CallToActionUrl { get; set; }
}

internal sealed class TikTokPostOptionsDocument
{
    [BsonElement("privacyLevel")] public string PrivacyLevel { get; set; } = string.Empty;
    [BsonElement("allowComments")] public bool AllowComments { get; set; }
    [BsonElement("allowDuet")] public bool AllowDuet { get; set; }
    [BsonElement("allowStitch")] public bool AllowStitch { get; set; }
    [BsonElement("promotesOwnBusiness")] public bool PromotesOwnBusiness { get; set; }
    [BsonElement("isPaidPartnership")] public bool IsPaidPartnership { get; set; }
}

internal sealed class ScheduledPostDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("sourceKind")] public int SourceKind { get; set; }
    [BsonElement("sourceId")] public string SourceId { get; set; } = default!;
    [BsonElement("sourceName")] public string SourceName { get; set; } = string.Empty;
    [BsonElement("caption")] public string Caption { get; set; } = string.Empty;
    [BsonElement("scheduledForUtc")] public DateTime? ScheduledForUtc { get; set; }
    [BsonElement("status")] public int Status { get; set; }
    [BsonElement("targets")] public List<PublishTargetDocument> Targets { get; set; } = new();
    [BsonElement("gbpOptions")] public GbpPostOptionsDocument? GbpOptions { get; set; }
    [BsonElement("tikTokOptions")] public TikTokPostOptionsDocument? TikTokOptions { get; set; }

    /* ---- worker lease ---- */

    /// <summary>
    /// Which instance currently owns this post, or null.
    /// </summary>
    /// <remarks>
    /// These two fields are what <c>ClaimNextDueAsync</c> matches and sets in a
    /// single findAndModify. They are the reason two API instances cannot both
    /// publish the same post.
    /// </remarks>
    [BsonElement("leaseHolder")] public string? LeaseHolder { get; set; }
    [BsonElement("leaseExpiresAtUtc")] public DateTime? LeaseExpiresAtUtc { get; set; }

    /// <summary>Automatic-retry backoff. Absent on documents written before it existed, which reads as null.</summary>
    [BsonElement("nextAttemptAtUtc")] public DateTime? NextAttemptAtUtc { get; set; }

    [BsonElement("completedAtUtc")] public DateTime? CompletedAtUtc { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }

    /* ---- recurring series (absent on one-off posts) ---- */

    [BsonElement("seriesId")] [BsonIgnoreIfNull] public string? SeriesId { get; set; }
    [BsonElement("occurrenceAtUtc")] [BsonIgnoreIfNull] public DateTime? OccurrenceAtUtc { get; set; }

    /// <summary>
    /// When the pharmacy was told how this occurrence went. Set only by the
    /// series notifier, with a findAndModify, so each outcome is emailed once.
    /// </summary>
    /// <remarks>
    /// Not in the domain on purpose. Every save by the publish worker or a
    /// pharmacy's Retry replaces the document and so clears it — which is
    /// right: a retried occurrence has a new outcome worth telling them about.
    /// </remarks>
    [BsonElement("seriesNotifiedAtUtc")] [BsonIgnoreIfNull] public DateTime? SeriesNotifiedAtUtc { get; set; }
}

/// <summary>A recurring series. See <c>RecurringSeries</c>.</summary>
internal sealed class RecurringSeriesDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = string.Empty;
    [BsonElement("sourceKind")] public int SourceKind { get; set; }
    [BsonElement("sourceId")] public string SourceId { get; set; } = default!;
    [BsonElement("sourceName")] public string SourceName { get; set; } = string.Empty;
    [BsonElement("caption")] public string Caption { get; set; } = string.Empty;
    [BsonElement("destinations")] public List<SeriesDestinationDocument> Destinations { get; set; } = new();

    [BsonElement("cadenceKind")] public int CadenceKind { get; set; }
    /// <summary>Minutes after local midnight.</summary>
    [BsonElement("timeOfDayMinutes")] public int TimeOfDayMinutes { get; set; }
    [BsonElement("days")] public List<int> Days { get; set; } = new();
    [BsonElement("intervalDays")] public int IntervalDays { get; set; } = 1;
    /// <summary>yyyy-MM-dd, so they read as the calendar days they are.</summary>
    [BsonElement("dates")] public List<string> Dates { get; set; } = new();

    [BsonElement("timeZoneId")] public string TimeZoneId { get; set; } = "Europe/London";
    [BsonElement("startDate")] public string StartDate { get; set; } = default!;
    [BsonElement("endDate")] [BsonIgnoreIfNull] public string? EndDate { get; set; }

    [BsonElement("status")] public int Status { get; set; }
    [BsonElement("statusReason")] [BsonIgnoreIfNull] public string? StatusReason { get; set; }
    [BsonElement("pausedBySystem")] public bool PausedBySystem { get; set; }
    [BsonElement("notify")] public int Notify { get; set; }

    /// <summary>What the scheduler polls on: active series with this in the past are due.</summary>
    [BsonElement("nextOccurrenceUtc")] public DateTime? NextOccurrenceUtc { get; set; }
    [BsonElement("lastOccurrenceUtc")] public DateTime? LastOccurrenceUtc { get; set; }
    [BsonElement("missedCount")] public int MissedCount { get; set; }
    [BsonElement("lastMissedAtUtc")] public DateTime? LastMissedAtUtc { get; set; }

    /// <summary>Saves are conditional on this, so two writers cannot silently overwrite each other.</summary>
    [BsonElement("revision")] public int Revision { get; set; }

    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class SeriesDestinationDocument
{
    [BsonElement("connectionId")] public string ConnectionId { get; set; } = default!;
    [BsonElement("platform")] public int Platform { get; set; }
}

internal sealed class AdsLocationDocument
{
    [BsonElement("geoTargetConstantId")] public string? GeoTargetConstantId { get; set; }
    [BsonElement("displayName")] public string? DisplayName { get; set; }
    [BsonElement("latitude")] public double? Latitude { get; set; }
    [BsonElement("longitude")] public double? Longitude { get; set; }
    [BsonElement("radiusKm")] public int? RadiusKm { get; set; }
}

internal sealed class AdsMetricsDocument
{
    [BsonElement("impressions")] public long Impressions { get; set; }
    [BsonElement("clicks")] public long Clicks { get; set; }
    [BsonElement("costMicros")] public long CostMicros { get; set; }
    [BsonElement("conversions")] public long Conversions { get; set; }
    [BsonElement("lastSyncedAtUtc")] public DateTime LastSyncedAtUtc { get; set; }
}

internal sealed class GoogleAdsCampaignDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("connectionId")] public string ConnectionId { get; set; } = default!;
    [BsonElement("customerId")] public string CustomerId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("campaignType")] public int CampaignType { get; set; }
    [BsonElement("status")] public int Status { get; set; }
    [BsonElement("externalCampaignId")] public string? ExternalCampaignId { get; set; }
    [BsonElement("externalBudgetId")] public string? ExternalBudgetId { get; set; }
    [BsonElement("budgetKind")] public int BudgetKind { get; set; }
    [BsonElement("budgetMicros")] public long BudgetMicros { get; set; }
    [BsonElement("currencyCode")] public string CurrencyCode { get; set; } = "GBP";

    /// <summary>
    /// ISO yyyy-MM-dd, not a BSON date.
    /// </summary>
    /// <remarks>
    /// A campaign start date is a calendar day, not an instant. Storing it as a
    /// UTC datetime means a campaign starting "1 September" reads back as
    /// 31 August in any timezone behind UTC — and Google would then be told the
    /// wrong day. The driver has no native DateOnly support in this version
    /// anyway.
    /// </remarks>
    [BsonElement("startDate")] public string StartDate { get; set; } = default!;
    [BsonElement("endDate")] public string? EndDate { get; set; }

    [BsonElement("finalUrl")] public string FinalUrl { get; set; } = default!;
    [BsonElement("locations")] public List<AdsLocationDocument> Locations { get; set; } = new();
    [BsonElement("creativeKind")] public int CreativeKind { get; set; }
    [BsonElement("creativeSourceId")] public string CreativeSourceId { get; set; } = default!;
    [BsonElement("externalAssetId")] public string? ExternalAssetId { get; set; }
    [BsonElement("headlines")] public List<string> Headlines { get; set; } = new();
    [BsonElement("descriptions")] public List<string> Descriptions { get; set; } = new();
    [BsonElement("keywords")] public List<string>? Keywords { get; set; }
    [BsonElement("longHeadline")] public string? LongHeadline { get; set; }
    [BsonElement("businessName")] public string? BusinessName { get; set; }
    [BsonElement("metrics")] public AdsMetricsDocument? Metrics { get; set; }
    [BsonElement("lastError")] public string? LastError { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}
