namespace Acme.Domain.Subscriptions;

/// <summary>
/// Lifecycle of a Stripe subscription, narrowed to the states this app
/// actually reacts to. Stripe has more (`paused`, `unpaid`, …) — anything
/// unrecognised maps to <see cref="Unknown"/> and is treated as "no active
/// entitlement" so an unexpected state can never silently grant access.
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>No Stripe subscription — the user is on the Free tier.</summary>
    None = 0,

    /// <summary>Paid and current. The only status that grants a paid tier.</summary>
    Active = 1,

    /// <summary>
    /// Payment failed but Stripe is still retrying (dunning). We deliberately
    /// keep the paid tier during this window — pulling access on the first
    /// failed card is hostile when the usual cause is an expired card, and
    /// Stripe cancels for us once retries are exhausted.
    /// </summary>
    PastDue = 2,

    /// <summary>Cancelled or expired. Falls back to Free.</summary>
    Canceled = 3,

    /// <summary>Awaiting first successful payment (e.g. SCA challenge pending).</summary>
    Incomplete = 4,

    Unknown = 99,
}

/// <summary>
/// Everything the app needs to know about a user's commercial relationship
/// with Stripe. Kept as a value object hanging off <c>User</c> so the entity
/// doesn't sprout six loose nullable strings.
/// </summary>
/// <param name="VideoCredits">
/// One-off video credits bought outside the monthly allowance. Consumed only
/// after the plan's monthly quota is exhausted, and never expired at the period
/// boundary — the customer paid for them separately, so silently voiding them
/// each month would be theft.
/// </param>
public sealed record BillingProfile(
    string? StripeCustomerId,
    string? StripeSubscriptionId,
    SubscriptionStatus Status,
    DateTime? CurrentPeriodEndUtc,
    bool CancelAtPeriodEnd,
    int VideoCredits)
{
    public static BillingProfile Empty() =>
        new(null, null, SubscriptionStatus.None, null, false, 0);

    /// <summary>
    /// True when Stripe says this subscription should currently entitle the
    /// user to their paid tier. <see cref="SubscriptionStatus.PastDue"/>
    /// counts — see the note on that member.
    /// </summary>
    public bool GrantsPaidTier =>
        Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    public static SubscriptionStatus ParseStatus(string? stripeStatus) => stripeStatus switch
    {
        "active" => SubscriptionStatus.Active,
        "trialing" => SubscriptionStatus.Active,
        "past_due" => SubscriptionStatus.PastDue,
        "canceled" => SubscriptionStatus.Canceled,
        "incomplete_expired" => SubscriptionStatus.Canceled,
        "incomplete" => SubscriptionStatus.Incomplete,
        null or "" => SubscriptionStatus.None,
        _ => SubscriptionStatus.Unknown,
    };
}
