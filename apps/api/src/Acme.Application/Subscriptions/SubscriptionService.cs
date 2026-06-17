using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Domain.Subscriptions;

namespace Acme.Application.Subscriptions;

public sealed class SubscriptionService(
    IUserRepository users,
    IPosterRepository posters)
{
    public async Task<Result<CurrentSubscriptionDto>> GetCurrentAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<CurrentSubscriptionDto>.NotFound("User not found.");

        var plan = Plans.For(user.Tier);
        var posterCount = await posters.CountAsync(new PosterQuery(userId), ct);

        return Result<CurrentSubscriptionDto>.Success(new CurrentSubscriptionDto(
            ToDto(plan),
            new UsageDto(posterCount, AiGenerationsThisMonth: 0)));
    }

    public IReadOnlyList<PlanDto> GetPlans() =>
        Plans.All.Values.OrderBy(p => p.MonthlyPriceGbp).Select(ToDto).ToList();

    public async Task<Result<CurrentSubscriptionDto>> UpgradeAsync(
        string userId,
        SubscriptionTier targetTier,
        CancellationToken ct)
    {
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
        p.Watermark,
        p.CustomTemplates,
        p.EmailExport,
        p.TeamCollaboration,
        p.CustomBranding,
        p.AnalyticsDashboard,
        p.PriorityAi,
        p.Highlights);
}
