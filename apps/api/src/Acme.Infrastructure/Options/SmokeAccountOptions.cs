using Acme.Domain.Subscriptions;

namespace Acme.Infrastructure.Options;

/// <summary>
/// The account <c>pnpm smoke</c> signs in as.
/// </summary>
/// <remarks>
/// <para>
/// The smoke test needs an account it can create posters, campaigns, videos and
/// screens with on every run. Using the bootstrap SuperAdmin for that is wrong
/// twice over: it accumulates test clutter on the one account somebody has to
/// trust, and it exercises the product as staff rather than as a pharmacy — so
/// the plan gates, which are the expensive doors the smoke exists to check,
/// never actually get tested.
/// </para>
/// <para>
/// So this is an ordinary <see cref="Acme.Domain.Users.UserRole.PharmacyUser"/>
/// on a paid tier. Least privilege: if the credential ever leaks, what leaks is
/// one pharmacy account with test data in it, not the admin console.
/// </para>
/// </remarks>
public sealed class SmokeAccountOptions
{
    public const string SectionName = "SmokeAccount";

    /// <summary>
    /// Off by default. A seeder that creates a known-credential account should
    /// never run because somebody forgot to configure it.
    /// </summary>
    public bool Enabled { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string PharmacyName { get; set; } = "Smoke Test Pharmacy";
    public string ContactName { get; set; } = "Smoke Test";

    /// <summary>
    /// The plan it runs on. Pro by default, because every paid door has to be
    /// open for the smoke to mean anything — on Free the video step reports
    /// "no allowance" and skips, which is a pass that proves nothing.
    /// </summary>
    public SubscriptionTier Tier { get; set; } = SubscriptionTier.Pro;

    /// <summary>Extra video credits, on top of the plan's monthly allowance.</summary>
    public int VideoCredits { get; set; }
}
