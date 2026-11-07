using Acme.Domain.Subscriptions;

namespace Acme.Application.Billing;

/// <summary>Which plan and interval the customer picked on the pricing page.</summary>
public sealed record StartCheckoutRequest(SubscriptionTier Tier, BillingInterval Interval);

public enum BillingInterval
{
    Monthly = 0,
    Yearly = 1,
}

public sealed record BuyCreditsRequest(string PackKey);

/// <summary>Returned to the browser, which then does a top-level redirect.</summary>
public sealed record CheckoutSessionDto(string Url);

public sealed record CreditPackDto(
    string Key,
    string DisplayName,
    int Credits,
    decimal PriceGbp);

/// <summary>Billing state surfaced on the account/pricing screens.</summary>
public sealed record BillingStatusDto(
    bool StripeEnabled,
    SubscriptionTier Tier,
    string Status,
    DateTime? CurrentPeriodEndUtc,
    bool CancelAtPeriodEnd,
    int VideoCredits,
    bool HasPaymentMethod,
    IReadOnlyList<CreditPackDto> CreditPacks,
    // False while somebody is working as this pharmacy rather than as its own
    // account holder: they may see the plan but not change it (R3-06). Set by
    // the controller, which is where the caller's token is known.
    bool CanManageBilling = true);
