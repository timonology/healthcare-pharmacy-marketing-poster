using Acme.Domain.Subscriptions;

namespace Acme.Application.Subscriptions;

public sealed record PlanDto(
    SubscriptionTier Tier,
    string DisplayName,
    decimal MonthlyPriceGbp,
    int MaxPosters,
    int MaxAiGenerationsPerMonth,
    int MaxCampaignRecipientsPerMonth,
    int MaxPatients,
    int MaxVideosPerMonth,
    bool AiVideoEnabled,
    bool Watermark,
    bool AnalyticsDashboard,
    IReadOnlyList<string> Highlights,
    /// <summary>Recurring social series active at once. 0: not on this plan.</summary>
    int MaxRecurringSeries = 0);

public sealed record UsageDto(
    int Posters,
    int AiGenerationsThisMonth,
    int CampaignRecipientsThisMonth,
    int Patients,
    int VideosThisMonth);

public sealed record CurrentSubscriptionDto(
    PlanDto Plan,
    UsageDto Usage);

public sealed record UpgradeRequest(SubscriptionTier Tier);
