using Acme.Domain.Subscriptions;

namespace Acme.Application.Subscriptions;

public sealed record PlanDto(
    SubscriptionTier Tier,
    string DisplayName,
    decimal MonthlyPriceGbp,
    int MaxPosters,
    int MaxAiGenerationsPerMonth,
    bool Watermark,
    bool CustomTemplates,
    bool EmailExport,
    bool TeamCollaboration,
    bool CustomBranding,
    bool AnalyticsDashboard,
    bool PriorityAi,
    IReadOnlyList<string> Highlights);

public sealed record UsageDto(
    int Posters,
    int AiGenerationsThisMonth);

public sealed record CurrentSubscriptionDto(
    PlanDto Plan,
    UsageDto Usage);

public sealed record UpgradeRequest(SubscriptionTier Tier);
