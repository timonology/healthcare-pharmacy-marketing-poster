using System.Text.RegularExpressions;
using Acme.Application.Subscriptions;
using Acme.Domain.Subscriptions;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Subscriptions;

/// <summary>
/// Pricing copy says what the product enforces, and nothing else.
/// </summary>
/// <remarks>
/// Pro used to advertise "Priority AI responses", "Team collaboration" and
/// "Custom domain branding" — none of which exist — and Free/Starter promised
/// template and export tiers nothing checked. The plan lines are now written
/// from the enforced limits (<see cref="PlanCopy"/>); these pin that, and pin
/// the static copy the web keeps for pages that cannot call the API.
/// </remarks>
public class PlanCopyTests
{
    /// <summary>Things the product does not do. None may be sold.</summary>
    private static readonly string[] NotOffered =
    [
        "custom domain",
        "priority ai",
        "team collaboration",
        "collaboration",
        "custom branding",
        "basic templates",
        "all templates",
        "custom uploads",
        "email export",
    ];

    public static TheoryData<SubscriptionTier> Tiers => new()
    {
        SubscriptionTier.Free, SubscriptionTier.Starter, SubscriptionTier.Pro,
    };

    [Theory]
    [MemberData(nameof(Tiers))]
    public void No_plan_sells_a_feature_the_product_does_not_have(SubscriptionTier tier)
    {
        foreach (var line in Plans.For(tier).Highlights)
            foreach (var phrase in NotOffered)
                line.Should().NotContainEquivalentOf(phrase,
                    "{0} advertises \"{1}\", which the product does not do", tier, line);
    }

    [Fact]
    public void Pro_lists_what_Pro_actually_has()
    {
        Plans.For(SubscriptionTier.Pro).Highlights.Should().Contain(new[]
        {
            "Unlimited AI poster designs",
            "30 marketing videos a month, including AI imagery",
            "Unlimited in-pharmacy screens",
            "Email campaigns to 5,000 patients a month",
        });
    }

    [Fact]
    public void Each_line_follows_the_enforced_limit()
    {
        // Change a limit and the copy changes with it; nothing is hand-written.
        var starter = Plans.For(SubscriptionTier.Starter) with
        {
            MaxPosters = 40, MaxAiGenerationsPerMonth = 60, MaxVideosPerMonth = 6, MaxDisplayScreens = 2,
        };

        starter.Highlights.Should().Contain(new[]
        {
            "Up to 40 saved posters",
            "60 AI poster designs a month",
            "6 marketing videos a month",
            "2 in-pharmacy screens",
        });
    }

    [Fact]
    public void A_zero_allowance_is_left_out_not_advertised()
    {
        var free = Plans.For(SubscriptionTier.Free).Highlights;

        free.Should().NotContain(l => l.Contains("video", StringComparison.OrdinalIgnoreCase));
        free.Should().NotContain(l => l.Contains("screen", StringComparison.OrdinalIgnoreCase));
        free.Should().NotContain(l => l.Contains("campaign", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void The_web_copy_matches_the_api(SubscriptionTier tier)
    {
        // The marketing teaser and onboarding render without the API, from
        // packages/shared-types/src/plan-highlights.ts. It must say exactly
        // what the API says.
        WebHighlights().Should().ContainKey(tier.ToString());
        WebHighlights()[tier.ToString()].Should().Equal(Plans.For(tier).Highlights,
            "plan-highlights.ts has drifted from PlanCopy for {0}; copy the API's lines across", tier);
    }

    [Theory]
    [MemberData(nameof(Tiers))]
    public void The_web_prices_match_the_api(SubscriptionTier tier)
    {
        // Shown on the home page and in its structured data, where a search
        // engine may repeat them: they must be what Stripe will charge.
        var file = Path.Combine(RepoRoot(), "packages", "shared-types", "src", "plan-highlights.ts");
        var block = Regex.Match(File.ReadAllText(file), @"PLAN_PRICES_GBP[^{]*\{(?<body>[^}]*)\}").Groups["body"].Value;
        var price = Regex.Match(block, $@"\b{tier}:\s*(?<p>\d+(\.\d+)?)").Groups["p"].Value;

        price.Should().NotBeEmpty("PLAN_PRICES_GBP must list {0}", tier);
        decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(Plans.For(tier).MonthlyPriceGbp);
    }

    [Fact]
    public void No_web_pricing_copy_names_the_removed_features()
    {
        var web = Path.Combine(RepoRoot(), "apps", "web", "src");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(web, "*.ts*", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(".test.", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            foreach (var phrase in new[] { "Custom domain branding", "Priority AI", "Team collaboration" })
                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetRelativePath(web, file)}: \"{phrase}\"");
        }

        offenders.Should().BeEmpty("these are not features of any plan");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Dictionary<string, List<string>> WebHighlights()
    {
        var file = Path.Combine(RepoRoot(), "packages", "shared-types", "src", "plan-highlights.ts");
        var text = File.ReadAllText(file);

        var tiers = new Dictionary<string, List<string>>();
        foreach (Match tier in Regex.Matches(text, @"\b(Free|Starter|Pro):\s*\[(?<lines>[^\]]*)\]"))
        {
            tiers[tier.Groups[1].Value] = Regex.Matches(tier.Groups["lines"].Value, "\"((?:[^\"\\\\]|\\\\.)*)\"")
                .Select(m => m.Groups[1].Value)
                .ToList();
        }
        return tiers;
    }

    /// <summary>Walks up from the test binary to the folder holding <c>packages/</c>.</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "packages", "shared-types")))
                return dir.FullName;

        throw new InvalidOperationException(
            "Could not find packages/shared-types above the test output directory.");
    }
}
