using System.Globalization;
using System.Reflection;
using Acme.Domain.Social;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Document ↔ domain mapping for the social feature.
/// </summary>
/// <remarks>
/// Same reflection approach as the other mappers here: the domain entities keep
/// private setters so the only way to reach an invalid state is through a
/// method that validates, and hydration deliberately bypasses that because a
/// document already in the database is history, not a new decision.
///
/// <see cref="SetField"/> exists because <c>ScheduledPost.Targets</c> and
/// <c>GoogleAdsCampaign.Locations</c> are get-only projections over private
/// readonly fields. Setting the property is impossible; the field has to be
/// written directly.
/// </remarks>
internal static class SocialMapper
{
    private static readonly ConstructorInfo ConnectionCtor = Ctor<SocialConnection>();
    private static readonly ConstructorInfo PostCtor = Ctor<ScheduledPost>();
    private static readonly ConstructorInfo TargetCtor = Ctor<PublishTarget>();
    private static readonly ConstructorInfo CampaignCtor = Ctor<GoogleAdsCampaign>();

    private static ConstructorInfo Ctor<T>() =>
        typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException($"{typeof(T).Name} parameterless ctor missing.");

    /* ---------------------------------------------------------------- *
     * SocialConnection
     * ---------------------------------------------------------------- */

    public static SocialConnection Hydrate(SocialConnectionDocument d)
    {
        var c = (SocialConnection)ConnectionCtor.Invoke(null);
        Set(c, nameof(SocialConnection.Id), d.Id);
        Set(c, nameof(SocialConnection.OwnerId), d.OwnerId);
        Set(c, nameof(SocialConnection.Platform), (SocialPlatform)d.Platform);
        Set(c, nameof(SocialConnection.ExternalAccountId), d.ExternalAccountId);
        Set(c, nameof(SocialConnection.DisplayName), d.DisplayName);
        Set(c, nameof(SocialConnection.AvatarUrl), d.AvatarUrl);
        Set(c, nameof(SocialConnection.Status), (ConnectionStatus)d.Status);
        Set(c, nameof(SocialConnection.AccessToken), Hydrate(d.AccessToken));
        Set(c, nameof(SocialConnection.RefreshToken), Hydrate(d.RefreshToken));
        Set(c, nameof(SocialConnection.ExpiresAtUtc), d.ExpiresAtUtc);
        Set(c, nameof(SocialConnection.Scopes), (IReadOnlyList<string>)(d.Scopes ?? new()).ToList());
        Set(c, nameof(SocialConnection.ParentAccountId), d.ParentAccountId);
        Set(c, nameof(SocialConnection.AuthorizedByUserId), d.AuthorizedByUserId);
        Set(c, nameof(SocialConnection.GrantFingerprint), d.GrantFingerprint);
        Set(c, nameof(SocialConnection.LastVerifiedAtUtc), d.LastVerifiedAtUtc);
        Set(c, nameof(SocialConnection.LastHealthCheckAtUtc), d.LastHealthCheckAtUtc);
        Set(c, nameof(SocialConnection.LastError), d.LastError);
        Set(c, nameof(SocialConnection.CreatedAtUtc), d.CreatedAtUtc);
        Set(c, nameof(SocialConnection.UpdatedAtUtc), d.UpdatedAtUtc);
        return c;
    }

    public static SocialConnectionDocument ToDocument(SocialConnection c) => new()
    {
        Id = c.Id,
        OwnerId = c.OwnerId,
        Platform = (int)c.Platform,
        ExternalAccountId = c.ExternalAccountId,
        DisplayName = c.DisplayName,
        AvatarUrl = c.AvatarUrl,
        Status = (int)c.Status,
        AccessToken = ToDocument(c.AccessToken),
        RefreshToken = ToDocument(c.RefreshToken),
        ExpiresAtUtc = c.ExpiresAtUtc,
        Scopes = c.Scopes.ToList(),
        ParentAccountId = c.ParentAccountId,
        AuthorizedByUserId = c.AuthorizedByUserId,
        GrantFingerprint = c.GrantFingerprint,
        LastVerifiedAtUtc = c.LastVerifiedAtUtc,
        LastHealthCheckAtUtc = c.LastHealthCheckAtUtc,
        LastError = c.LastError,
        CreatedAtUtc = c.CreatedAtUtc,
        UpdatedAtUtc = c.UpdatedAtUtc,
    };

    private static EncryptedSecret? Hydrate(EncryptedSecretDocument? d) =>
        d is null ? null : new EncryptedSecret(d.CipherText, d.Nonce, d.Tag, d.KeyVersion);

    internal static EncryptedSecretDocument? ToSecretDocument(EncryptedSecret? s) => ToDocument(s);

    private static EncryptedSecretDocument? ToDocument(EncryptedSecret? s) =>
        s is null ? null : new EncryptedSecretDocument
        {
            CipherText = s.CipherText,
            Nonce = s.Nonce,
            Tag = s.Tag,
            KeyVersion = s.KeyVersion,
        };

    /* ---------------------------------------------------------------- *
     * ScheduledPost
     * ---------------------------------------------------------------- */

    public static ScheduledPost Hydrate(ScheduledPostDocument d)
    {
        var p = (ScheduledPost)PostCtor.Invoke(null);
        Set(p, nameof(ScheduledPost.Id), d.Id);
        Set(p, nameof(ScheduledPost.OwnerId), d.OwnerId);
        Set(p, nameof(ScheduledPost.SourceKind), (PublishSourceKind)d.SourceKind);
        Set(p, nameof(ScheduledPost.SourceId), d.SourceId);
        Set(p, nameof(ScheduledPost.SourceName), d.SourceName ?? string.Empty);
        Set(p, nameof(ScheduledPost.Caption), d.Caption ?? string.Empty);
        Set(p, nameof(ScheduledPost.ScheduledForUtc), d.ScheduledForUtc);
        Set(p, nameof(ScheduledPost.Status), (PublishStatus)d.Status);
        Set(p, nameof(ScheduledPost.GbpOptions), Hydrate(d.GbpOptions));
        Set(p, nameof(ScheduledPost.TikTokOptions), Hydrate(d.TikTokOptions));
        Set(p, nameof(ScheduledPost.LeaseHolder), d.LeaseHolder);
        Set(p, nameof(ScheduledPost.LeaseExpiresAtUtc), d.LeaseExpiresAtUtc);
        Set(p, nameof(ScheduledPost.NextAttemptAtUtc), d.NextAttemptAtUtc);
        Set(p, nameof(ScheduledPost.CompletedAtUtc), d.CompletedAtUtc);
        Set(p, nameof(ScheduledPost.CreatedAtUtc), d.CreatedAtUtc);
        Set(p, nameof(ScheduledPost.UpdatedAtUtc), d.UpdatedAtUtc);
        Set(p, nameof(ScheduledPost.SeriesId), d.SeriesId);
        Set(p, nameof(ScheduledPost.OccurrenceAtUtc), d.OccurrenceAtUtc);

        // Targets live behind a get-only projection, so the backing field is
        // populated directly rather than through the property.
        var targets = (d.Targets ?? new()).Select(Hydrate).ToList();
        SetField(p, "_targets", targets);

        return p;
    }

    private static PublishTarget Hydrate(PublishTargetDocument d)
    {
        var t = (PublishTarget)TargetCtor.Invoke(null);
        Set(t, nameof(PublishTarget.ConnectionId), d.ConnectionId);
        Set(t, nameof(PublishTarget.Platform), (SocialPlatform)d.Platform);
        Set(t, nameof(PublishTarget.Status), (PublishTargetStatus)d.Status);
        Set(t, nameof(PublishTarget.ExternalPostId), d.ExternalPostId);
        Set(t, nameof(PublishTarget.Permalink), d.Permalink);
        Set(t, nameof(PublishTarget.Error), d.Error);
        Set(t, nameof(PublishTarget.AttemptCount), d.AttemptCount);
        Set(t, nameof(PublishTarget.LastAttemptAtUtc), d.LastAttemptAtUtc);
        Set(t, nameof(PublishTarget.PublishedAtUtc), d.PublishedAtUtc);
        return t;
    }

    public static ScheduledPostDocument ToDocument(ScheduledPost p) => new()
    {
        Id = p.Id,
        OwnerId = p.OwnerId,
        SourceKind = (int)p.SourceKind,
        SourceId = p.SourceId,
        SourceName = p.SourceName,
        Caption = p.Caption,
        ScheduledForUtc = p.ScheduledForUtc,
        Status = (int)p.Status,
        Targets = p.Targets.Select(ToDocument).ToList(),
        GbpOptions = ToDocument(p.GbpOptions),
        TikTokOptions = ToDocument(p.TikTokOptions),
        LeaseHolder = p.LeaseHolder,
        LeaseExpiresAtUtc = p.LeaseExpiresAtUtc,
        NextAttemptAtUtc = p.NextAttemptAtUtc,
        CompletedAtUtc = p.CompletedAtUtc,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc,
        SeriesId = p.SeriesId,
        OccurrenceAtUtc = p.OccurrenceAtUtc,
    };

    private static PublishTargetDocument ToDocument(PublishTarget t) => new()
    {
        ConnectionId = t.ConnectionId,
        Platform = (int)t.Platform,
        Status = (int)t.Status,
        ExternalPostId = t.ExternalPostId,
        Permalink = t.Permalink,
        Error = t.Error,
        AttemptCount = t.AttemptCount,
        LastAttemptAtUtc = t.LastAttemptAtUtc,
        PublishedAtUtc = t.PublishedAtUtc,
    };

    private static TikTokPostOptions? Hydrate(TikTokPostOptionsDocument? d) =>
        d is null ? null : new TikTokPostOptions(
            d.PrivacyLevel, d.AllowComments, d.AllowDuet, d.AllowStitch, d.PromotesOwnBusiness, d.IsPaidPartnership);

    private static TikTokPostOptionsDocument? ToDocument(TikTokPostOptions? o) =>
        o is null ? null : new TikTokPostOptionsDocument
        {
            PrivacyLevel = o.PrivacyLevel,
            AllowComments = o.AllowComments,
            AllowDuet = o.AllowDuet,
            AllowStitch = o.AllowStitch,
            PromotesOwnBusiness = o.PromotesOwnBusiness,
            IsPaidPartnership = o.IsPaidPartnership,
        };

    private static GbpPostOptions? Hydrate(GbpPostOptionsDocument? d) =>
        d is null ? null : new GbpPostOptions(
            (GbpPostType)d.PostType,
            d.CouponCode,
            d.RedeemOnlineUrl,
            d.TermsConditions,
            d.EventTitle,
            d.EventStartUtc,
            d.EventEndUtc,
            d.CallToActionType,
            d.CallToActionUrl);

    private static GbpPostOptionsDocument? ToDocument(GbpPostOptions? o) =>
        o is null ? null : new GbpPostOptionsDocument
        {
            PostType = (int)o.PostType,
            CouponCode = o.CouponCode,
            RedeemOnlineUrl = o.RedeemOnlineUrl,
            TermsConditions = o.TermsConditions,
            EventTitle = o.EventTitle,
            EventStartUtc = o.EventStartUtc,
            EventEndUtc = o.EventEndUtc,
            CallToActionType = o.CallToActionType,
            CallToActionUrl = o.CallToActionUrl,
        };

    /* ---------------------------------------------------------------- *
     * GoogleAdsCampaign
     * ---------------------------------------------------------------- */

    public static GoogleAdsCampaign Hydrate(GoogleAdsCampaignDocument d)
    {
        var c = (GoogleAdsCampaign)CampaignCtor.Invoke(null);
        Set(c, nameof(GoogleAdsCampaign.Id), d.Id);
        Set(c, nameof(GoogleAdsCampaign.OwnerId), d.OwnerId);
        Set(c, nameof(GoogleAdsCampaign.ConnectionId), d.ConnectionId);
        Set(c, nameof(GoogleAdsCampaign.CustomerId), d.CustomerId);
        Set(c, nameof(GoogleAdsCampaign.Name), d.Name);
        Set(c, nameof(GoogleAdsCampaign.CampaignType), (AdsCampaignType)d.CampaignType);
        Set(c, nameof(GoogleAdsCampaign.Status), (AdsCampaignStatus)d.Status);
        Set(c, nameof(GoogleAdsCampaign.ExternalCampaignId), d.ExternalCampaignId);
        Set(c, nameof(GoogleAdsCampaign.ExternalBudgetId), d.ExternalBudgetId);
        Set(c, nameof(GoogleAdsCampaign.BudgetKind), (AdsBudgetKind)d.BudgetKind);
        Set(c, nameof(GoogleAdsCampaign.BudgetMicros), d.BudgetMicros);
        Set(c, nameof(GoogleAdsCampaign.CurrencyCode), d.CurrencyCode ?? "GBP");
        Set(c, nameof(GoogleAdsCampaign.StartDate), ParseDate(d.StartDate) ?? default);
        Set(c, nameof(GoogleAdsCampaign.EndDate), ParseDate(d.EndDate));
        Set(c, nameof(GoogleAdsCampaign.FinalUrl), d.FinalUrl);
        Set(c, nameof(GoogleAdsCampaign.CreativeKind), (PublishSourceKind)d.CreativeKind);
        Set(c, nameof(GoogleAdsCampaign.CreativeSourceId), d.CreativeSourceId);
        Set(c, nameof(GoogleAdsCampaign.ExternalAssetId), d.ExternalAssetId);
        Set(c, nameof(GoogleAdsCampaign.Headlines), (IReadOnlyList<string>)(d.Headlines ?? new()).ToList());
        Set(c, nameof(GoogleAdsCampaign.Descriptions), (IReadOnlyList<string>)(d.Descriptions ?? new()).ToList());
        Set(c, nameof(GoogleAdsCampaign.Keywords), (IReadOnlyList<string>)(d.Keywords ?? new()).ToList());
        Set(c, nameof(GoogleAdsCampaign.LongHeadline), d.LongHeadline);
        Set(c, nameof(GoogleAdsCampaign.BusinessName), d.BusinessName);
        Set(c, nameof(GoogleAdsCampaign.Metrics), Hydrate(d.Metrics));
        Set(c, nameof(GoogleAdsCampaign.LastError), d.LastError);
        Set(c, nameof(GoogleAdsCampaign.CreatedAtUtc), d.CreatedAtUtc);
        Set(c, nameof(GoogleAdsCampaign.UpdatedAtUtc), d.UpdatedAtUtc);

        var locations = (d.Locations ?? new())
            .Select(l => new AdsLocationTarget(
                l.GeoTargetConstantId, l.DisplayName, l.Latitude, l.Longitude, l.RadiusKm))
            .ToList();
        SetField(c, "_locations", locations);

        return c;
    }

    public static GoogleAdsCampaignDocument ToDocument(GoogleAdsCampaign c) => new()
    {
        Id = c.Id,
        OwnerId = c.OwnerId,
        ConnectionId = c.ConnectionId,
        CustomerId = c.CustomerId,
        Name = c.Name,
        CampaignType = (int)c.CampaignType,
        Status = (int)c.Status,
        ExternalCampaignId = c.ExternalCampaignId,
        ExternalBudgetId = c.ExternalBudgetId,
        BudgetKind = (int)c.BudgetKind,
        BudgetMicros = c.BudgetMicros,
        CurrencyCode = c.CurrencyCode,
        StartDate = FormatDate(c.StartDate),
        EndDate = c.EndDate is { } end ? FormatDate(end) : null,
        FinalUrl = c.FinalUrl,
        Locations = c.Locations.Select(l => new AdsLocationDocument
        {
            GeoTargetConstantId = l.GeoTargetConstantId,
            DisplayName = l.DisplayName,
            Latitude = l.Latitude,
            Longitude = l.Longitude,
            RadiusKm = l.RadiusKm,
        }).ToList(),
        CreativeKind = (int)c.CreativeKind,
        CreativeSourceId = c.CreativeSourceId,
        ExternalAssetId = c.ExternalAssetId,
        Headlines = c.Headlines.ToList(),
        Descriptions = c.Descriptions.ToList(),
        Keywords = c.Keywords.ToList(),
        LongHeadline = c.LongHeadline,
        BusinessName = c.BusinessName,
        Metrics = new AdsMetricsDocument
        {
            Impressions = c.Metrics.Impressions,
            Clicks = c.Metrics.Clicks,
            CostMicros = c.Metrics.CostMicros,
            Conversions = c.Metrics.Conversions,
            LastSyncedAtUtc = c.Metrics.LastSyncedAtUtc,
        },
        LastError = c.LastError,
        CreatedAtUtc = c.CreatedAtUtc,
        UpdatedAtUtc = c.UpdatedAtUtc,
    };

    private static AdsMetrics Hydrate(AdsMetricsDocument? d) =>
        d is null
            ? AdsMetrics.Empty()
            : new AdsMetrics(d.Impressions, d.Clicks, d.CostMicros, d.Conversions, d.LastSyncedAtUtc);

    /// <summary>Invariant culture, always. A campaign date must not depend on the server's locale.</summary>
    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
                ? parsed
                : null;

    /* ---------------------------------------------------------------- *
     * Reflection helpers
     * ---------------------------------------------------------------- */

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }

    /// <summary>
    /// Writes a private backing field directly.
    /// </summary>
    /// <remarks>
    /// Needed where a collection is exposed as a get-only projection over a
    /// readonly field — the property has no setter to call. Throws rather than
    /// silently doing nothing if the field is renamed, because a quietly empty
    /// target list would turn every restored post into one with nothing to
    /// publish.
    /// </remarks>
    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(
            fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{target.GetType().Name}.{fieldName} not found — the field was renamed " +
                "and this mapper was not updated.");

        field.SetValue(target, value);
    }
}
