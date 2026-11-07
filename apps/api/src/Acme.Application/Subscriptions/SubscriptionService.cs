using Acme.Application.Ai;
using Acme.Application.Auth;
using Acme.Application.Billing;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Application.Videos;
using Acme.Domain.Subscriptions;

namespace Acme.Application.Subscriptions;

public sealed class SubscriptionService(
    IUserRepository users,
    IPosterRepository posters,
    ICampaignRepository campaigns,
    IPatientRepository patients,
    IVideoRepository videos,
    IStripeGateway stripe,
    IAiGenerationRepository generations)
{
    public async Task<Result<CurrentSubscriptionDto>> GetCurrentAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<CurrentSubscriptionDto>.NotFound("User not found.");

        var plan = Plans.For(user.Tier);
        var posterCount = await posters.CountAsync(new PosterQuery(userId), ct);
        var recipientsThisMonth = await campaigns.CountRecipientsThisMonthAsync(userId, ct);
        var patientCount = await patients.CountByOwnerAsync(userId, ct);
        var videoCount = await videos.CountByOwnerThisMonthAsync(userId, ct);

        // Counted properly now: one record per call, written by PosterAiService
        // when a design is returned. This used to report -1 ("not applicable")
        // because the nearest proxy available — posters created this month from
        // an AI-generated template — missed every design a pharmacist generated
        // and threw away, which are exactly the ones that cost money.
        //
        // The same query the limit uses, so the number a pharmacy sees here and
        // the number it is refused on cannot disagree. Successful generations
        // only: a failed call is rate-limited but never billed, so counting
        // attempts here would show a pharmacy an allowance it had not spent.
        var aiGenerations = await generations.CountSucceededThisMonthAsync(userId, ct);

        return Result<CurrentSubscriptionDto>.Success(new CurrentSubscriptionDto(
            ToDto(plan),
            new UsageDto(
                posterCount,
                AiGenerationsThisMonth: aiGenerations,
                recipientsThisMonth,
                patientCount,
                videoCount)));
    }

    public IReadOnlyList<PlanDto> GetPlans() =>
        Plans.All.Values.OrderBy(p => p.MonthlyPriceGbp).Select(ToDto).ToList();

    /// <summary>
    /// Development-only tier switch.
    ///
    /// This used to be the real upgrade path: any authenticated user could POST
    /// their desired tier and get it, free. That was fine when there was no
    /// billing and is a critical hole now — so once Stripe is configured this
    /// refuses outright and the only route to a paid tier is a Stripe webhook.
    ///
    /// It stays available with Stripe disabled so local development and
    /// automated tests can exercise paid-tier behaviour without a card.
    /// </summary>
    public async Task<Result<CurrentSubscriptionDto>> UpgradeAsync(
        string userId,
        SubscriptionTier targetTier,
        CancellationToken ct)
    {
        if (stripe.Enabled)
            return Result<CurrentSubscriptionDto>.Forbidden(
                "Plan changes go through checkout. Use /api/billing/checkout.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<CurrentSubscriptionDto>.NotFound("User not found.");

        if (!Enum.IsDefined(typeof(SubscriptionTier), targetTier))
            return Result<CurrentSubscriptionDto>.Invalid("Unknown tier.");

        user.ChangeTier(targetTier);
        await users.UpdateAsync(user, ct);

        return await GetCurrentAsync(userId, ct);
    }

    private static PlanDto ToDto(PlanDefinition p) => new(
        p.Tier,
        p.DisplayName,
        p.MonthlyPriceGbp,
        p.MaxPosters,
        p.MaxAiGenerationsPerMonth,
        p.MaxCampaignRecipientsPerMonth,
        p.MaxPatients,
        p.MaxVideosPerMonth,
        p.AiVideoEnabled,
        p.Watermark,
        p.AnalyticsDashboard,
        p.Highlights,
        p.MaxRecurringSeries);
}
