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
                MaxVideosPerMonth: 0,
                MaxDisplayScreens: 0,
                AiVideoEnabled: false,
                Watermark: true,
                AnalyticsDashboard: false,
                MaxRecurringSeries: 0),

            [SubscriptionTier.Starter] = new(
                Tier: SubscriptionTier.Starter,
                DisplayName: "Starter",
                MonthlyPriceGbp: 7m,
                MaxPosters: 30,
                MaxAiGenerationsPerMonth: 50,
                MaxCampaignRecipientsPerMonth: 500,
                MaxPatients: 500,
                MaxVideosPerMonth: 5,
                // One counter screen. A single-site pharmacy is the whole of
                // this tier, and a single site rarely has two.
                MaxDisplayScreens: 1,
                AiVideoEnabled: false,
                Watermark: false,
                AnalyticsDashboard: false,
                MaxRecurringSeries: 2),

            [SubscriptionTier.Pro] = new(
                Tier: SubscriptionTier.Pro,
                DisplayName: "Pro",
                MonthlyPriceGbp: 25m,
                MaxPosters: Unlimited,
                MaxAiGenerationsPerMonth: Unlimited,
                MaxCampaignRecipientsPerMonth: 5000,
                MaxPatients: Unlimited,
                MaxVideosPerMonth: 30,
                MaxDisplayScreens: Unlimited,
                AiVideoEnabled: true,
                Watermark: false,
                AnalyticsDashboard: true,
                MaxRecurringSeries: 20),
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
    int MaxVideosPerMonth,
    /// <summary>In-pharmacy display screens. 0 refuses the feature outright.</summary>
    int MaxDisplayScreens,
    bool AiVideoEnabled,
    bool Watermark,
    bool AnalyticsDashboard,
    /// <summary>
    /// Recurring social series that may be active at once. 0 refuses the
    /// feature. Paused and ended series do not count.
    /// </summary>
    int MaxRecurringSeries = 0)
{
    /// <summary>
    /// The plan's selling points, written from the limits above rather than
    /// alongside them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These were a hand-written list, and it drifted: Pro advertised "Priority
    /// AI responses", "Team collaboration" and "Custom domain branding", none
    /// of which the product does, and Free and Starter promised template and
    /// export tiers nothing enforced. Deriving each line from the number that
    /// is actually enforced means a limit cannot change without its copy.
    /// </para>
    /// <para>
    /// Only what is enforced gets a line. A zero is left out rather than
    /// written as "0 videos": the card for a plan says what it includes.
    /// The marketing teaser and onboarding show the same lines from
    /// <c>packages/shared-types/src/plan-highlights.ts</c>, and
    /// <c>PlanCopyTests</c> fails if the two differ.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Highlights => PlanCopy.Highlights(this);
}

/// <summary>Writes a plan's selling points from its enforced limits.</summary>
public static class PlanCopy
{
    private static string N(int value) =>
        value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> Highlights(PlanDefinition p)
    {
        var lines = new List<string>
        {
            p.MaxPosters == Plans.Unlimited ? "Unlimited posters" : $"Up to {N(p.MaxPosters)} saved posters",
            p.MaxAiGenerationsPerMonth == Plans.Unlimited
                ? "Unlimited AI poster designs"
                : $"{N(p.MaxAiGenerationsPerMonth)} AI poster designs a month",
        };

        if (p.MaxVideosPerMonth == Plans.Unlimited)
            lines.Add("Unlimited marketing videos" + (p.AiVideoEnabled ? ", including AI imagery" : ""));
        else if (p.MaxVideosPerMonth > 0)
            lines.Add($"{N(p.MaxVideosPerMonth)} marketing videos a month"
                + (p.AiVideoEnabled ? ", including AI imagery" : ""));

        if (p.MaxDisplayScreens == Plans.Unlimited)
            lines.Add("Unlimited in-pharmacy screens");
        else if (p.MaxDisplayScreens > 0)
            lines.Add(p.MaxDisplayScreens == 1
                ? "1 in-pharmacy screen"
                : $"{N(p.MaxDisplayScreens)} in-pharmacy screens");

        if (p.MaxCampaignRecipientsPerMonth == Plans.Unlimited)
            lines.Add("Unlimited email campaign recipients");
        else if (p.MaxCampaignRecipientsPerMonth > 0)
            lines.Add($"Email campaigns to {N(p.MaxCampaignRecipientsPerMonth)} patients a month");

        lines.Add(p.MaxPatients == Plans.Unlimited
            ? "Unlimited patient contacts"
            : $"Up to {N(p.MaxPatients)} patient contacts");

        lines.Add(p.Watermark ? "Watermark on exports" : "No watermark");

        if (p.AnalyticsDashboard)
            lines.Add("Analytics dashboard");

        return lines;
    }
}
