namespace Acme.Infrastructure.Options;

/// <summary>
/// Stripe wiring. Everything here except <see cref="Enabled"/> is a secret or
/// an environment-specific id, so nothing has a meaningful default — misconfiguration
/// should surface loudly at startup rather than at a customer's checkout.
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    /// <summary>
    /// Master switch. When false the billing endpoints return 503 and the
    /// pricing page shows "contact us" instead of a broken checkout button.
    /// Lets the app run locally without Stripe credentials.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Server-side key: <c>sk_test_…</c> or <c>sk_live_…</c>. Never sent to the browser.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Signing secret for the webhook endpoint: <c>whsec_…</c>. Without this
    /// anyone who knows the URL could POST a fake "payment succeeded" event
    /// and grant themselves a Pro subscription.
    /// </summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Recurring price ids, one per plan/interval combination.</summary>
    public StripePriceIds Prices { get; set; } = new();

    /// <summary>One-time credit-pack price ids, keyed by pack size.</summary>
    public List<CreditPackOption> CreditPacks { get; set; } = new();

    /// <summary>
    /// Where Stripe sends the customer back to. Must be an absolute URL —
    /// Stripe rejects relative paths, and a localhost value left in production
    /// strands every customer who completes a payment.
    /// </summary>
    public string SuccessUrl { get; set; } = string.Empty;
    public string CancelUrl { get; set; } = string.Empty;
    public string PortalReturnUrl { get; set; } = string.Empty;
}

public sealed class StripePriceIds
{
    public string StarterMonthly { get; set; } = string.Empty;
    public string StarterYearly { get; set; } = string.Empty;
    public string ProMonthly { get; set; } = string.Empty;
    public string ProYearly { get; set; } = string.Empty;
}

public sealed class CreditPackOption
{
    /// <summary>Stable identifier used by the frontend, e.g. "credits_25".</summary>
    public string Key { get; set; } = string.Empty;

    public string PriceId { get; set; } = string.Empty;

    /// <summary>How many video credits this pack grants.</summary>
    public int Credits { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Display only — Stripe remains the source of truth for what is charged.</summary>
    public decimal PriceGbp { get; set; }
}
