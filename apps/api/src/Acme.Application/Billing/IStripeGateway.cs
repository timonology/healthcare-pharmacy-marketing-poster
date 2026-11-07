using Acme.Domain.Subscriptions;

namespace Acme.Application.Billing;

/// <summary>
/// Everything the app needs from Stripe, expressed in our own vocabulary.
/// Keeps the Stripe SDK confined to Infrastructure so the Application layer
/// stays testable without network access or a fake Stripe server.
/// </summary>
public interface IStripeGateway
{
    bool Enabled { get; }

    /// <summary>Creates (or returns) the Stripe customer backing this user.</summary>
    Task<string> EnsureCustomerAsync(
        string userId, string email, string displayName, string? existingCustomerId, CancellationToken ct);

    /// <summary>Hosted Checkout session for a recurring plan. Returns the redirect URL.</summary>
    Task<string> CreateSubscriptionCheckoutAsync(
        string customerId, string priceId, string userId, CancellationToken ct);

    /// <summary>Hosted Checkout session for a one-time credit pack.</summary>
    Task<string> CreateCreditPackCheckoutAsync(
        string customerId, string priceId, string userId, string packKey, CancellationToken ct);

    /// <summary>
    /// Billing portal session — Stripe's own UI for changing card, switching
    /// plan, downloading invoices, and cancelling. Building all of that
    /// ourselves would be weeks of work and a compliance surface we don't want.
    /// </summary>
    Task<string> CreatePortalSessionAsync(string customerId, CancellationToken ct);

    /// <summary>
    /// Verifies the webhook signature and parses the payload. Returns null when
    /// the signature is invalid — callers must treat that as a rejected request,
    /// never as an empty event.
    /// </summary>
    StripeWebhookEvent? ParseWebhook(string payload, string signatureHeader);

    /// <summary>Reads a subscription's current state straight from Stripe.</summary>
    Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct);

    /// <summary>Maps a Stripe price id back to the tier it grants.</summary>
    SubscriptionTier TierForPrice(string priceId);

    /// <summary>Looks up a configured credit pack by its Stripe price id.</summary>
    CreditPack? CreditPackForPrice(string priceId);

    IReadOnlyList<CreditPack> CreditPacks { get; }

    // Configured recurring price ids. Exposed so the Application layer can
    // resolve tier+interval → price without reaching into Infrastructure
    // options, and so a missing id is a plain empty string it can validate.
    string PriceStarterMonthly { get; }
    string PriceStarterYearly { get; }
    string PriceProMonthly { get; }
    string PriceProYearly { get; }
}

/// <param name="PriceId">
/// Frequently null on webhooks: Stripe does not expand line items in the event
/// payload. Treat it as a hint, not a source of truth — resolve the product
/// from <see cref="StripeWebhookEvent.Metadata"/> or by re-reading the
/// subscription instead.
/// </param>
/// <param name="Metadata">
/// Metadata we attached when creating the Checkout session. This is the
/// reliable way to identify what was bought, precisely because it survives
/// into the webhook payload when line items don't.
/// </param>
public sealed record StripeWebhookEvent(
    string Id,
    string Type,
    string? CustomerId,
    string? SubscriptionId,
    string? ClientReferenceId,
    string? PriceId,
    string? Mode,
    string? PaymentStatus,
    StripeSubscriptionSnapshot? Subscription,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record StripeSubscriptionSnapshot(
    string Id,
    string CustomerId,
    string Status,
    string? PriceId,
    DateTime? CurrentPeriodEndUtc,
    bool CancelAtPeriodEnd);

public sealed record CreditPack(
    string Key,
    string PriceId,
    int Credits,
    string DisplayName,
    decimal PriceGbp);
