using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Common;
using Acme.Domain.Social;
using Acme.Domain.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>
/// Creating and managing Google Ads campaigns from the pharmacy's own creative.
/// </summary>
/// <remarks>
/// <para>
/// Google is authoritative. This service records intent and mirrors state; it
/// never assumes a campaign is in the shape we last left it. Anything changed
/// in the Google Ads console will differ here until the next metrics sync, and
/// the UI is expected to say so rather than presenting stale local state as
/// fact.
/// </para>
/// <para>
/// Campaigns are created <b>paused</b>, without exception. Enabling is a
/// separate, deliberate act. Advertising spends real money against a live
/// budget, and a wizard whose last step starts spending gives nobody a chance
/// to notice that the radius was set to 500km instead of 5.
/// </para>
/// </remarks>
public sealed class GoogleAdsService(
    IGoogleAdsCampaignRepository campaigns,
    ISocialConnectionRepository connections,
    IUserRepository users,
    ITokenCipher cipher,
    IPublishMediaResolver media,
    IGoogleAdsClient client,
    // Builds the Performance Max image set (landscape, square, logo).
    IAdsCreativeBuilder creativeBuilder,
    ILogger<GoogleAdsService> log)
{
    /// <summary>Largest daily budget accepted here, in account currency units.</summary>
    public const decimal MaxDailyBudget = 1_000m;

    /// <summary>Largest campaign total accepted here.</summary>
    public const decimal MaxTotalBudget = 20_000m;

    public async Task<Result<IReadOnlyList<GoogleAdsCampaignDto>>> ListAsync(
        string ownerId, CancellationToken ct)
    {
        var items = await campaigns.ListByOwnerAsync(ownerId, ct);
        return Result<IReadOnlyList<GoogleAdsCampaignDto>>.Success(
            items.OrderByDescending(c => c.CreatedAtUtc).Select(ToDto).ToList());
    }

    /// <summary>Google Ads accounts this connection can manage.</summary>
    public async Task<Result<IReadOnlyList<AdsAccountDto>>> ListAccountsAsync(
        string ownerId, string connectionId, CancellationToken ct)
    {
        var resolved = await ResolveConnectionAsync(ownerId, connectionId, ct);
        if (resolved.Error is { } error)
            return Result<IReadOnlyList<AdsAccountDto>>.Invalid(error);

        try
        {
            var accounts = await client.ListAccessibleCustomersAsync(resolved.Access!.RefreshToken, ct);
            return Result<IReadOnlyList<AdsAccountDto>>.Success(
                accounts.Select(a => new AdsAccountDto(a.CustomerId, a.DescriptiveName, a.CurrencyCode)).ToList());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Listing Google Ads customers failed for owner {Owner}", ownerId);
            return Result<IReadOnlyList<AdsAccountDto>>.Invalid($"Google Ads refused the request: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a campaign at Google from a poster or video.
    /// </summary>
    /// <remarks>
    /// The local record is written before the call and updated after, rather
    /// than only being written on success. If Google creates the campaign and
    /// the response is lost, a record with status Creating is a lead to follow;
    /// no record at all is a campaign spending money that nothing here knows
    /// about.
    /// </remarks>
    /// <param name="customerId">
    /// Optional. The Ads account is the connection's own; a value here is only
    /// checked against it, so a request cannot point a campaign at another
    /// customer the same Google login happens to reach.
    /// </param>
    public async Task<Result<GoogleAdsCampaignDto>> CreateAsync(
        string ownerId, CreateAdsCampaignRequest request, string? customerId, CancellationToken ct)
    {
        if (await WhyCannotSpendAsync(ownerId, ct) is { } denial)
            return Result<GoogleAdsCampaignDto>.Forbidden(denial);

        if (!client.IsConfigured)
            return Result<GoogleAdsCampaignDto>.Invalid(
                "Google Ads is not configured on this server. The Google OAuth client is required.");

        var resolved = await ResolveConnectionAsync(ownerId, request.ConnectionId, ct);
        if (resolved.Error is { } error) return Result<GoogleAdsCampaignDto>.Invalid(error);

        var access = resolved.Access!;
        var accountId = access.CustomerId;
        if (!string.IsNullOrWhiteSpace(customerId)
            && !string.Equals(Digits(customerId), Digits(accountId), StringComparison.Ordinal))
            return Result<GoogleAdsCampaignDto>.Invalid(
                "That Google Ads account does not match the chosen connection.");

        var creative = await media.ResolveAsync(
            request.CreativeKind, request.CreativeSourceId, ownerId, forInstagram: false, ct);
        if (creative is null)
            return Result<GoogleAdsCampaignDto>.NotFound("That poster or video no longer exists.");

        // Typo protection. A local pharmacy campaign at £5,000 a day is a
        // misplaced zero, not a plan, and Google would start spending it the
        // moment the campaign is switched on.
        var cap = request.BudgetKind == AdsBudgetKind.Lifetime ? MaxTotalBudget : MaxDailyBudget;
        if (request.Budget > cap)
            return Result<GoogleAdsCampaignDto>.Invalid(
                $"That budget is above the {cap:N0} limit for a {(request.BudgetKind == AdsBudgetKind.Lifetime ? "campaign total" : "daily budget")}. " +
                "Check for an extra zero; larger budgets can be set in Google Ads directly.");

        // Every amount Google sees is in the account's own currency, whatever
        // the request says. A US-dollar test account given "5" spends $5, so
        // the label the pharmacy typed against has to match.
        var currency = request.CurrencyCode;
        try
        {
            if (await client.GetAccountCurrencyAsync(access, ct) is { } accountCurrency)
            {
                if (!string.Equals(accountCurrency, request.CurrencyCode, StringComparison.OrdinalIgnoreCase))
                    return Result<GoogleAdsCampaignDto>.Invalid(
                        $"This Google Ads account works in {accountCurrency}, so the budget must be in {accountCurrency}, not {request.CurrencyCode}.");
                currency = accountCurrency;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not knowing is not a reason to refuse; the campaign creation
            // itself will report a real problem with the account.
            log.LogWarning(ex, "Could not read the currency of Google Ads account {Customer}", access.CustomerId);
        }


        GoogleAdsCampaign campaign;
        try
        {
            campaign = GoogleAdsCampaign.Draft(
                ownerId,
                request.ConnectionId,
                accountId,
                request.Name,
                request.CampaignType,
                request.BudgetKind,
                // Micros conversion happens once, here. Doing it at the client
                // boundary as well would eventually mean doing it twice.
                (long)Math.Round(request.Budget * 1_000_000m),
                currency,
                request.StartDate,
                request.EndDate,
                request.FinalUrl,
                request.CreativeKind,
                request.CreativeSourceId,
                request.Locations?.Select(l => new AdsLocationTarget(
                    l.GeoTargetConstantId, l.DisplayName, l.Latitude, l.Longitude, l.RadiusKm)),
                request.Headlines,
                request.Descriptions,
                request.Keywords,
                request.LongHeadline,
                request.BusinessName);
        }
        catch (DomainException ex)
        {
            return Result<GoogleAdsCampaignDto>.Invalid(ex.Message);
        }

        // Performance Max shows the poster itself, so it needs it in Google's
        // shapes and the pharmacy's logo — checked before anything is saved,
        // so a missing logo is a message, not a failed campaign record.
        AdsCreative? images = null;
        if (campaign.CampaignType == AdsCampaignType.PerformanceMax)
        {
            var built = await creativeBuilder.BuildAsync(ownerId, campaign.CreativeSourceId, ct);
            if (built.Kind != ResultKind.Success)
                return Result<GoogleAdsCampaignDto>.Invalid(built.Error ?? "The poster could not be prepared.");
            images = built.Value;
        }

        campaign.MarkCreating();
        await campaigns.AddAsync(campaign, ct);

        try
        {
            var result = await client.CreateCampaignAsync(campaign, access, images, ct);

            if (result.ValidatedOnly)
            {
                // A check, not a campaign: nothing to list or follow up.
                await campaigns.DeleteAsync(campaign.Id, CancellationToken.None);
                return Result<GoogleAdsCampaignDto>.Invalid(result.Error ?? "Checked by Google; nothing was created.");
            }

            if (result.Success && !string.IsNullOrWhiteSpace(result.ExternalCampaignId))
            {
                campaign.MarkCreated(result.ExternalCampaignId!, result.ExternalBudgetId, result.ExternalAssetId);
                log.LogInformation(
                    "Created Google Ads campaign {External} for pharmacy {Owner} (paused)",
                    result.ExternalCampaignId, ownerId);
            }
            else
            {
                campaign.MarkFailed(result.Error ?? "Google Ads rejected the campaign without saying why.");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Google Ads campaign creation threw for owner {Owner}", ownerId);
            campaign.MarkFailed(ex.Message);
        }

        await campaigns.UpdateAsync(campaign, ct);

        return campaign.Status == AdsCampaignStatus.Failed
            ? Result<GoogleAdsCampaignDto>.Invalid(campaign.LastError ?? "Campaign creation failed.")
            : Result<GoogleAdsCampaignDto>.Success(ToDto(campaign));
    }

    /// <summary>Enables or pauses a campaign at Google.</summary>
    public async Task<Result<GoogleAdsCampaignDto>> SetStatusAsync(
        string ownerId, string campaignId, bool enabled, CancellationToken ct)
    {
        var campaign = await campaigns.FindByIdAsync(campaignId, ct);
        if (campaign is null || campaign.OwnerId != ownerId)
            return Result<GoogleAdsCampaignDto>.NotFound("Campaign not found.");
        if (campaign.ExternalCampaignId is null)
            return Result<GoogleAdsCampaignDto>.Invalid("This campaign was never created at Google.");

        // Pausing is always allowed — it stops spending. Enabling starts it,
        // so the plan is checked now, not only when the campaign was made.
        if (enabled && await WhyCannotSpendAsync(ownerId, ct) is { } denial)
            return Result<GoogleAdsCampaignDto>.Forbidden(denial);

        var resolved = await ResolveConnectionAsync(ownerId, campaign.ConnectionId, ct);
        if (resolved.Error is { } error) return Result<GoogleAdsCampaignDto>.Invalid(error);

        try
        {
            var ok = await client.SetCampaignStatusAsync(
                campaign.ExternalCampaignId, enabled, resolved.Access! with { CustomerId = campaign.CustomerId }, ct);

            if (!ok)
                return Result<GoogleAdsCampaignDto>.Invalid("Google Ads did not accept the status change.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Google Ads status change failed for campaign {Id}", campaignId);
            return Result<GoogleAdsCampaignDto>.Invalid($"Google Ads refused the request: {ex.Message}");
        }

        // Local state is updated only after Google confirms. Flipping the badge
        // optimistically would show "Enabled" on a campaign that is still
        // paused and spending nothing, which is the kind of discrepancy people
        // only find at the end of the month.
        if (enabled) campaign.MarkEnabled(); else campaign.MarkPaused();
        await campaigns.UpdateAsync(campaign, ct);

        log.LogInformation(
            "Pharmacy {Owner} {Action} Google Ads campaign {External}",
            ownerId, enabled ? "enabled" : "paused", campaign.ExternalCampaignId);

        return Result<GoogleAdsCampaignDto>.Success(ToDto(campaign));
    }

    /// <summary>Pulls the latest metrics from Google.</summary>
    public async Task<Result<GoogleAdsCampaignDto>> SyncMetricsAsync(
        string ownerId, string campaignId, CancellationToken ct)
    {
        var campaign = await campaigns.FindByIdAsync(campaignId, ct);
        if (campaign is null || campaign.OwnerId != ownerId)
            return Result<GoogleAdsCampaignDto>.NotFound("Campaign not found.");
        if (campaign.ExternalCampaignId is null)
            return Result<GoogleAdsCampaignDto>.Invalid("This campaign was never created at Google.");

        var resolved = await ResolveConnectionAsync(ownerId, campaign.ConnectionId, ct);
        if (resolved.Error is { } error) return Result<GoogleAdsCampaignDto>.Invalid(error);

        try
        {
            var metrics = await client.FetchMetricsAsync(
                campaign.ExternalCampaignId, resolved.Access! with { CustomerId = campaign.CustomerId }, ct);

            // Null means Google returned no rows, which for a campaign with no
            // impressions yet is the normal answer rather than an error. The
            // previous snapshot is left alone.
            if (metrics is not null)
            {
                campaign.UpdateMetrics(metrics);
                await campaigns.UpdateAsync(campaign, ct);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Google Ads metrics sync failed for campaign {Id}", campaignId);
            return Result<GoogleAdsCampaignDto>.Invalid($"Could not read metrics: {ex.Message}");
        }

        return Result<GoogleAdsCampaignDto>.Success(ToDto(campaign));
    }

    /// <summary>
    /// Loads a Google Ads connection and decrypts its refresh token.
    /// </summary>
    /// <remarks>
    /// The Google Ads API authenticates with a refresh token rather than the
    /// access token, exchanging it per call. A connection with no refresh token
    /// cannot work at all, so that is checked here rather than surfacing later
    /// as an opaque authentication error from the client library.
    /// </remarks>
    private async Task<(AdsAccess? Access, string? Error)> ResolveConnectionAsync(
        string ownerId, string connectionId, CancellationToken ct)
    {
        var connection = await connections.FindByIdAsync(connectionId, ct);

        if (connection is null || connection.OwnerId != ownerId)
            return (null, "That Google Ads connection was not found.");
        if (connection.Platform != SocialPlatform.GoogleAds)
            return (null, "That connection is not a Google Ads account.");
        if (connection.Status == ConnectionStatus.Revoked)
            return (null, "That Google Ads account has been disconnected.");
        if (connection.RefreshToken is null)
            return (null, "That Google Ads connection has no refresh token. Reconnect the account.");

        try
        {
            // Each Google Ads connection is one Ads account: its external id is
            // the customer id, and the only one it may act on. ParentAccountId
            // is the manager it is reached through, when there is one.
            return (new AdsAccess(
                cipher.Decrypt(connection.RefreshToken),
                Digits(connection.ExternalAccountId),
                string.IsNullOrWhiteSpace(connection.ParentAccountId) ? null : Digits(connection.ParentAccountId)),
                null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not decrypt the Google Ads refresh token for connection {Id}", connectionId);
            return (null, "Stored credentials could not be read. Reconnect the account.");
        }
    }

    /// <summary>
    /// Plan and account checks for anything that spends money, repeated at the
    /// moment of spending — a pharmacy that has since dropped to Free, or been
    /// deactivated, must not be able to switch a campaign on.
    /// </summary>
    private async Task<string?> WhyCannotSpendAsync(string ownerId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null || !user.IsActive) return "Account not found.";
        return user.Tier == SubscriptionTier.Free
            ? "Google Ads campaigns are available on the Starter plan and above."
            : null;
    }

    /// <summary>Customer ids are shown as 123-456-7890 and stored as digits.</summary>
    private static string Digits(string id) => new(id.Where(char.IsDigit).ToArray());

    private static GoogleAdsCampaignDto ToDto(GoogleAdsCampaign c) => new(
        c.Id,
        c.ConnectionId,
        c.CustomerId,
        c.Name,
        c.CampaignType,
        c.Status,
        c.ExternalCampaignId,
        c.BudgetKind,
        c.BudgetMicros / 1_000_000m,
        c.CurrencyCode,
        c.StartDate,
        c.EndDate,
        c.FinalUrl,
        c.Locations.Select(l => new AdsLocationDto(
            l.GeoTargetConstantId, l.DisplayName, l.Latitude, l.Longitude, l.RadiusKm)).ToList(),
        c.CreativeKind,
        c.CreativeSourceId,
        c.Headlines,
        c.Descriptions,
        ToDto(c.Metrics),
        c.LastError,
        c.CreatedAtUtc,
        c.Keywords,
        c.LongHeadline,
        c.BusinessName);

    private static AdsMetricsDto ToDto(AdsMetrics m) => new(
        m.Impressions,
        m.Clicks,
        m.CostInCurrency,
        m.Conversions,
        // Guarded because a campaign with no impressions is the common case on
        // day one, and a divide by zero there would take out the whole list.
        ClickThroughRate: m.Impressions == 0 ? 0m : Math.Round(m.Clicks * 100m / m.Impressions, 2),
        CostPerClick: m.Clicks == 0 ? 0m : Math.Round(m.CostInCurrency / m.Clicks, 2),
        m.LastSyncedAtUtc);
}
