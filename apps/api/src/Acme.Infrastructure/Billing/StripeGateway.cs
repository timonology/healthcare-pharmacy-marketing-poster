using Acme.Application.Billing;
using Acme.Domain.Subscriptions;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
// Stripe.BillingPortal is deliberately NOT imported: it declares its own
// SessionService and SessionCreateOptions which would collide with the
// Checkout types used throughout this file. Portal types are fully qualified.

namespace Acme.Infrastructure.Billing;

/// <summary>
/// Stripe.net wrapper. All Stripe types stay inside this file — everything it
/// hands back is one of our own records.
/// </summary>
public sealed class StripeGateway : IStripeGateway
{
    private readonly StripeOptions _opts;
    private readonly ILogger<StripeGateway> _log;
    private readonly CustomerService _customers;
    private readonly SessionService _checkout;
    private readonly Stripe.BillingPortal.SessionService _portal;
    private readonly SubscriptionService _subscriptions;

    public StripeGateway(IOptions<StripeOptions> opts, ILogger<StripeGateway> log)
    {
        _opts = opts.Value;
        _log = log;

        if (_opts.Enabled)
        {
            if (string.IsNullOrWhiteSpace(_opts.SecretKey))
                throw new InvalidOperationException(
                    "Stripe:Enabled is true but Stripe:SecretKey is empty. Set Stripe__SecretKey.");
            if (string.IsNullOrWhiteSpace(_opts.WebhookSecret))
                throw new InvalidOperationException(
                    "Stripe:Enabled is true but Stripe:WebhookSecret is empty. Set Stripe__WebhookSecret.");
        }

        // StripeConfiguration.ApiKey is a process-wide static in Stripe.net.
        // Setting it here (rather than per-call) matches how the SDK expects
        // to be used and keeps every service instance consistent.
        if (!string.IsNullOrWhiteSpace(_opts.SecretKey))
            StripeConfiguration.ApiKey = _opts.SecretKey;

        _customers = new CustomerService();
        _checkout = new SessionService();
        _portal = new Stripe.BillingPortal.SessionService();
        _subscriptions = new SubscriptionService();

        CreditPacks = _opts.CreditPacks
            .Where(p => !string.IsNullOrWhiteSpace(p.PriceId))
            .Select(p => new CreditPack(p.Key, p.PriceId, p.Credits, p.DisplayName, p.PriceGbp))
            .ToList();
    }

    public bool Enabled => _opts.Enabled;

    public IReadOnlyList<CreditPack> CreditPacks { get; }

    public string PriceStarterMonthly => _opts.Prices.StarterMonthly;
    public string PriceStarterYearly => _opts.Prices.StarterYearly;
    public string PriceProMonthly => _opts.Prices.ProMonthly;
    public string PriceProYearly => _opts.Prices.ProYearly;

    public async Task<string> EnsureCustomerAsync(
        string userId, string email, string displayName, string? existingCustomerId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(existingCustomerId))
            return existingCustomerId;

        var customer = await _customers.CreateAsync(
            new CustomerCreateOptions
            {
                Email = email,
                Name = displayName,
                // Our user id travels with the customer so a human debugging in
                // the Stripe dashboard can always get back to the right row.
                Metadata = new Dictionary<string, string> { ["appUserId"] = userId },
            },
            cancellationToken: ct);

        _log.LogInformation("Created Stripe customer {CustomerId} for user {UserId}", customer.Id, userId);
        return customer.Id;
    }

    public async Task<string> CreateSubscriptionCheckoutAsync(
        string customerId, string priceId, string userId, CancellationToken ct)
    {
        var session = await _checkout.CreateAsync(
            new SessionCreateOptions
            {
                Mode = "subscription",
                Customer = customerId,
                ClientReferenceId = userId,
                LineItems = new List<SessionLineItemOptions>
                {
                    new() { Price = priceId, Quantity = 1 },
                },
                SuccessUrl = AppendSessionId(_opts.SuccessUrl),
                CancelUrl = _opts.CancelUrl,
                // Mirrored onto the subscription so subscription.* webhooks —
                // which carry no client_reference_id — can still identify us.
                SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string> { ["appUserId"] = userId },
                },
                AllowPromotionCodes = true,
            },
            cancellationToken: ct);

        return session.Url;
    }

    public async Task<string> CreateCreditPackCheckoutAsync(
        string customerId, string priceId, string userId, string packKey, CancellationToken ct)
    {
        var session = await _checkout.CreateAsync(
            new SessionCreateOptions
            {
                Mode = "payment",
                Customer = customerId,
                ClientReferenceId = userId,
                LineItems = new List<SessionLineItemOptions>
                {
                    new() { Price = priceId, Quantity = 1 },
                },
                SuccessUrl = AppendSessionId(_opts.SuccessUrl),
                CancelUrl = _opts.CancelUrl,
                Metadata = new Dictionary<string, string>
                {
                    ["appUserId"] = userId,
                    ["creditPack"] = packKey,
                },
                // Saves the card for future purchases without a second entry.
                PaymentIntentData = new SessionPaymentIntentDataOptions { SetupFutureUsage = "on_session" },
            },
            cancellationToken: ct);

        return session.Url;
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, CancellationToken ct)
    {
        var session = await _portal.CreateAsync(
            new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = _opts.PortalReturnUrl,
            },
            cancellationToken: ct);

        return session.Url;
    }

    public StripeWebhookEvent? ParseWebhook(string payload, string signatureHeader)
    {
        try
        {
            // throwOnApiVersionMismatch: false — Stripe upgrades the dashboard's
            // API version independently of this SDK, and a mismatch is not a
            // reason to drop an otherwise valid, correctly-signed event.
            var stripeEvent = EventUtility.ConstructEvent(
                payload, signatureHeader, _opts.WebhookSecret, throwOnApiVersionMismatch: false);

            return stripeEvent.Type switch
            {
                "checkout.session.completed" => FromCheckoutSession(stripeEvent),
                "customer.subscription.created" or
                "customer.subscription.updated" or
                "customer.subscription.deleted" => FromSubscription(stripeEvent),
                "invoice.payment_failed" or
                "invoice.payment_succeeded" => FromInvoice(stripeEvent),
                _ => new StripeWebhookEvent(
                    stripeEvent.Id, stripeEvent.Type, null, null, null, null, null, null, null, null),
            };
        }
        catch (StripeException ex)
        {
            // Signature mismatch, malformed body, or wrong webhook secret. All
            // are "reject the request", never "process it anyway".
            _log.LogWarning("Rejected Stripe webhook: {Message}", ex.Message);
            return null;
        }
    }

    public async Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(
        string subscriptionId, CancellationToken ct)
    {
        try
        {
            var sub = await _subscriptions.GetAsync(subscriptionId, cancellationToken: ct);
            return ToSnapshot(sub);
        }
        catch (StripeException ex)
        {
            _log.LogWarning("Could not fetch Stripe subscription {Id}: {Message}", subscriptionId, ex.Message);
            return null;
        }
    }

    public SubscriptionTier TierForPrice(string priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId)) return SubscriptionTier.Free;
        var p = _opts.Prices;

        if (priceId == p.StarterMonthly || priceId == p.StarterYearly) return SubscriptionTier.Starter;
        if (priceId == p.ProMonthly || priceId == p.ProYearly) return SubscriptionTier.Pro;

        // An unmapped price means someone created a price in the dashboard and
        // didn't add it to config. Granting Free is the safe failure: the
        // customer contacts support rather than silently getting nothing or,
        // worse, silently getting Pro.
        _log.LogWarning("Stripe price {PriceId} is not mapped to any tier — treating as Free", priceId);
        return SubscriptionTier.Free;
    }

    public CreditPack? CreditPackForPrice(string priceId) =>
        CreditPacks.FirstOrDefault(p => p.PriceId == priceId);

    /* ---------------- mapping helpers ---------------- */

    private static StripeWebhookEvent FromCheckoutSession(Event e)
    {
        var s = e.Data.Object as Session;
        return new StripeWebhookEvent(
            e.Id,
            e.Type,
            s?.CustomerId,
            s?.SubscriptionId,
            s?.ClientReferenceId,
            PriceIdOf(s),
            s?.Mode,
            s?.PaymentStatus,
            null,
            s?.Metadata);
    }

    private static StripeWebhookEvent FromSubscription(Event e)
    {
        var sub = e.Data.Object as Stripe.Subscription;
        var snapshot = sub is null ? null : ToSnapshot(sub);
        return new StripeWebhookEvent(
            e.Id, e.Type, sub?.CustomerId, sub?.Id, null, snapshot?.PriceId, null, null,
            snapshot, sub?.Metadata);
    }

    private static StripeWebhookEvent FromInvoice(Event e)
    {
        var inv = e.Data.Object as Invoice;
        return new StripeWebhookEvent(
            e.Id, e.Type, inv?.CustomerId, inv?.SubscriptionId, null, null, null, inv?.Status,
            null, null);
    }

    private static StripeSubscriptionSnapshot ToSnapshot(Stripe.Subscription sub) => new(
        sub.Id,
        sub.CustomerId,
        sub.Status,
        sub.Items?.Data?.FirstOrDefault()?.Price?.Id,
        sub.CurrentPeriodEnd,
        sub.CancelAtPeriodEnd);

    private static string? PriceIdOf(Session? s)
    {
        // Line items aren't expanded on the webhook payload by default. When
        // absent we fall back to reading the subscription, which the caller
        // does via GetSubscriptionAsync.
        return s?.LineItems?.Data?.FirstOrDefault()?.Price?.Id;
    }

    /// <summary>
    /// Stripe substitutes {CHECKOUT_SESSION_ID} server-side. Appending it lets
    /// the success page confirm the payment rather than trusting the redirect —
    /// a user can reach the success URL by typing it in.
    /// </summary>
    private static string AppendSessionId(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var sep = url.Contains('?') ? '&' : '?';
        return $"{url}{sep}session_id={{CHECKOUT_SESSION_ID}}";
    }
}
