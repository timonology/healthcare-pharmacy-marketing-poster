using Acme.Domain.Subscriptions;

namespace Acme.Application.Subscriptions;

public static class Plans
{
    public const int Unlimited = -1;

    public static readonly IReadOnlyDictionary<SubscriptionTier, PlanDefinition> All =
        new Dictionary<SubscriptionTier, PlanDefinition>
        {
            [SubscriptionTier.Free] = new(
                Tier: SubscriptionTier.Free,
                DisplayName: "Free",
                MonthlyPriceGbp: 0m,
                MaxPosters: 5,
                MaxAiGenerationsPerMonth: 3,
                MaxCampaignRecipientsPerMonth: 0,
                MaxPatients: 50,
                Watermark: true,
                CustomTemplates: false,
                EmailExport: false,
                TeamCollaboration: false,
                CustomBranding: false,
                AnalyticsDashboard: false,
                PriorityAi: false,
                Highlights: new[]
                {
                    "Up to 5 saved posters",
                    "3 AI generations / month",
                    "Basic templates",
                    "Watermark on exports",
                }),

            [SubscriptionTier.Starter] = new(
                Tier: SubscriptionTier.Starter,
                DisplayName: "Starter",
                MonthlyPriceGbp: 7m,
                MaxPosters: 30,
                MaxAiGenerationsPerMonth: 50,
                MaxCampaignRecipientsPerMonth: 500,
                MaxPatients: 500,
                Watermark: false,
                CustomTemplates: true,
                EmailExport: true,
                TeamCollaboration: false,
                CustomBranding: false,
                AnalyticsDashboard: false,
                PriorityAi: false,
                Highlights: new[]
                {
                    "Up to 30 saved posters",
                    "50 AI generations / month",
                    "All templates + custom uploads",
                    "No watermark",
                    "Email export",
                }),

            [SubscriptionTier.Pro] = new(
                Tier: SubscriptionTier.Pro,
                DisplayName: "Pro",
                MonthlyPriceGbp: 25m,
                MaxPosters: Unlimited,
                MaxAiGenerationsPerMonth: Unlimited,
                MaxCampaignRecipientsPerMonth: 5000,
                MaxPatients: Unlimited,
                Watermark: false,
                CustomTemplates: true,
                EmailExport: true,
                TeamCollaboration: true,
                CustomBranding: true,
                AnalyticsDashboard: true,
                PriorityAi: true,
                Highlights: new[]
                {
                    "Unlimited posters",
                    "Unlimited AI generations",
                    "Priority AI responses",
                    "Team collaboration",
                    "Custom domain branding",
                    "Analytics dashboard",
                }),
        };

    public static PlanDefinition For(SubscriptionTier tier) =>
        All.TryGetValue(tier, out var plan) ? plan : All[SubscriptionTier.Free];
}

public sealed record PlanDefinition(
    SubscriptionTier Tier,
    string DisplayName,
    decimal MonthlyPriceGbp,
    int MaxPosters,
    int MaxAiGenerationsPerMonth,
    int MaxCampaignRecipientsPerMonth,
    int MaxPatients,
    bool Watermark,
    bool CustomTemplates,
    bool EmailExport,
    bool TeamCollaboration,
    bool CustomBranding,
    bool AnalyticsDashboard,
    bool PriorityAi,
    IReadOnlyList<string> Highlights);
