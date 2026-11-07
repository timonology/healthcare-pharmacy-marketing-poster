using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Billing;

/// <summary>
/// Owns the money-touching flows. Two rules shape everything here:
///
/// 1. The browser never states what it should be charged, only which plan it
///    picked. Prices live in Stripe; the client sends a tier + interval and we
///    look up the price id server-side. A client that could name its own price
///    could name £0.
///
/// 2. Tier changes come from Stripe webhooks only. Checkout returning
///    "success" means the customer's browser reached a URL, not that money
///    moved — with SCA the payment can still fail after the redirect. The
///    webhook is the authoritative signal.
/// </summary>
public sealed class BillingService(
    IUserRepository users,
    IStripeGateway stripe,
    IProcessedWebhookRepository processed,
    ILogger<BillingService> log)
{
    public async Task<Result<BillingStatusDto>> GetStatusAsync(string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<BillingStatusDto>.NotFound("User not found.");

        return Result<BillingStatusDto>.Success(new BillingStatusDto(
            StripeEnabled: stripe.Enabled,
            Tier: user.Tier,
            Status: user.Billing.Status.ToString(),
            CurrentPeriodEndUtc: user.Billing.CurrentPeriodEndUtc,
            CancelAtPeriodEnd: user.Billing.CancelAtPeriodEnd,
            VideoCredits: user.Billing.VideoCredits,
            HasPaymentMethod: user.Billing.StripeCustomerId is not null,
            CreditPacks: stripe.CreditPacks
                .Select(p => new CreditPackDto(p.Key, p.DisplayName, p.Credits, p.PriceGbp))
                .ToList()));
    }

    public async Task<Result<CheckoutSessionDto>> StartSubscriptionCheckoutAsync(
        string userId, SubscriptionTier tier, BillingInterval interval, CancellationToken ct)
    {
        if (!stripe.Enabled)
            return Result<CheckoutSessionDto>.Invalid("Billing is not configured.");

        if (tier == SubscriptionTier.Free)
            return Result<CheckoutSessionDto>.Invalid("The Free plan does not require checkout.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<CheckoutSessionDto>.NotFound("User not found.");

        var priceId = PriceIdFor(tier, interval);
        if (string.IsNullOrWhiteSpace(priceId))
            return Result<CheckoutSessionDto>.Invalid(
                $"No Stripe price configured for {tier} {interval}.");

        var customerId = await EnsureCustomerAsync(user, ct);
        var url = await stripe.CreateSubscriptionCheckoutAsync(customerId, priceId, userId, ct);

        log.LogInformation(
            "Checkout started user={UserId} tier={Tier} interval={Interval}", userId, tier, interval);

        return Result<CheckoutSessionDto>.Success(new CheckoutSessionDto(url));
    }

    public async Task<Result<CheckoutSessionDto>> StartCreditPackCheckoutAsync(
        string userId, string packKey, CancellationToken ct)
    {
        if (!stripe.Enabled)
            return Result<CheckoutSessionDto>.Invalid("Billing is not configured.");

        var pack = stripe.CreditPacks.FirstOrDefault(p =>
            string.Equals(p.Key, packKey, StringComparison.OrdinalIgnoreCase));
        if (pack is null)
            return Result<CheckoutSessionDto>.Invalid("Unknown credit pack.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<CheckoutSessionDto>.NotFound("User not found.");

        var customerId = await EnsureCustomerAsync(user, ct);
        var url = await stripe.CreateCreditPackCheckoutAsync(customerId, pack.PriceId, userId, pack.Key, ct);

        return Result<CheckoutSessionDto>.Success(new CheckoutSessionDto(url));
    }

    public async Task<Result<CheckoutSessionDto>> CreatePortalSessionAsync(
        string userId, CancellationToken ct)
    {
        if (!stripe.Enabled)
            return Result<CheckoutSessionDto>.Invalid("Billing is not configured.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<CheckoutSessionDto>.NotFound("User not found.");

        if (user.Billing.StripeCustomerId is not { } customerId)
            return Result<CheckoutSessionDto>.Invalid("No billing account yet — subscribe first.");

        var url = await stripe.CreatePortalSessionAsync(customerId, ct);
        return Result<CheckoutSessionDto>.Success(new CheckoutSessionDto(url));
    }

    /* ---------------------------------------------------------------- *
     * Webhooks
     * ---------------------------------------------------------------- */

    /// <summary>
    /// Processes a verified webhook. Returns false only for genuinely
    /// unexpected failures — the caller turns that into a 500 so Stripe
    /// retries. Unknown event types and already-seen ids return true, because
    /// asking Stripe to redeliver something we intentionally ignore just
    /// generates noise and eventually disables the endpoint.
    /// </summary>
    public async Task<bool> HandleWebhookAsync(StripeWebhookEvent evt, CancellationToken ct)
    {
        if (!await processed.TryMarkProcessedAsync(evt.Id, evt.Type, ct))
        {
            log.LogInformation("Ignoring duplicate Stripe event {EventId} ({Type})", evt.Id, evt.Type);
            return true;
        }

        switch (evt.Type)
        {
            case "checkout.session.completed":
                await OnCheckoutCompletedAsync(evt, ct);
                return true;

            case "customer.subscription.created":
            case "customer.subscription.updated":
                await OnSubscriptionChangedAsync(evt, ct);
                return true;

            case "customer.subscription.deleted":
                await OnSubscriptionDeletedAsync(evt, ct);
                return true;

            case "invoice.payment_failed":
                log.LogWarning(
                    "Payment failed for Stripe customer {CustomerId}. Stripe will retry; " +
                    "tier is retained until the subscription is cancelled.", evt.CustomerId);
                return true;

            default:
                log.LogDebug("Unhandled Stripe event type {Type}", evt.Type);
                return true;
        }
    }

    private async Task OnCheckoutCompletedAsync(StripeWebhookEvent evt, CancellationToken ct)
    {
        var user = await ResolveUserAsync(evt, ct);
        if (user is null)
        {
            log.LogWarning("checkout.session.completed for unknown user (customer {CustomerId})", evt.CustomerId);
            return;
        }

        // First purchase — bind the Stripe customer to our user so later
        // subscription.* events (which have no client_reference_id) resolve.
        if (user.Billing.StripeCustomerId is null && evt.CustomerId is { } cid)
            user.LinkStripeCustomer(cid);

        if (evt.Mode == "payment")
        {
            // One-time credit pack. Only grant when Stripe says the money
            // actually arrived — "complete" sessions can still be unpaid.
            if (!string.Equals(evt.PaymentStatus, "paid", StringComparison.OrdinalIgnoreCase))
            {
                log.LogWarning("Credit-pack session for {UserId} not paid ({Status}) — no credits granted",
                    user.Id, evt.PaymentStatus);
                await users.UpdateAsync(user, ct);
                return;
            }

            var pack = ResolveCreditPack(evt);
            if (pack is null)
            {
                log.LogError("Paid credit-pack session for {UserId} but pack could not be resolved", user.Id);
                await users.UpdateAsync(user, ct);
                return;
            }

            user.AddVideoCredits(pack.Credits);
            log.LogInformation("Granted {Credits} video credits to {UserId} ({Pack})",
                pack.Credits, user.Id, pack.Key);
        }
        else if (evt.SubscriptionId is { } subId)
        {
            // Re-read from Stripe rather than trusting the session payload:
            // line items aren't expanded on webhooks, so the price id we need
            // to derive the tier usually isn't there.
            var snapshot = await stripe.GetSubscriptionAsync(subId, ct);
            if (snapshot is not null) ApplySnapshot(user, snapshot);
        }

        await users.UpdateAsync(user, ct);
    }

    private async Task OnSubscriptionChangedAsync(StripeWebhookEvent evt, CancellationToken ct)
    {
        var user = await ResolveUserAsync(evt, ct);
        if (user is null) return;

        var snapshot = evt.Subscription
            ?? (evt.SubscriptionId is { } id ? await stripe.GetSubscriptionAsync(id, ct) : null);
        if (snapshot is null) return;

        ApplySnapshot(user, snapshot);
        await users.UpdateAsync(user, ct);

        log.LogInformation("Subscription {Status} for {UserId} → tier {Tier}",
            snapshot.Status, user.Id, user.Tier);
    }

    private async Task OnSubscriptionDeletedAsync(StripeWebhookEvent evt, CancellationToken ct)
    {
        var user = await ResolveUserAsync(evt, ct);
        if (user is null) return;

        user.ClearSubscription();
        await users.UpdateAsync(user, ct);
        log.LogInformation("Subscription ended for {UserId} — dropped to Free", user.Id);
    }

    private void ApplySnapshot(User user, StripeSubscriptionSnapshot snapshot)
    {
        var tier = snapshot.PriceId is { } price ? stripe.TierForPrice(price) : SubscriptionTier.Free;

        user.ApplySubscription(
            snapshot.Id,
            BillingProfile.ParseStatus(snapshot.Status),
            tier,
            snapshot.CurrentPeriodEndUtc,
            snapshot.CancelAtPeriodEnd);
    }

    /// <summary>
    /// Identifies which pack was bought.
    ///
    /// Metadata first, deliberately: Stripe does not expand line items in
    /// webhook payloads, so the price id is usually absent on
    /// <c>checkout.session.completed</c>. The <c>creditPack</c> key we set when
    /// creating the session does survive, which makes it the reliable path.
    /// Price lookup stays as a fallback for sessions created before this
    /// metadata existed.
    /// </summary>
    private CreditPack? ResolveCreditPack(StripeWebhookEvent evt)
    {
        if (evt.Metadata is not null
            && evt.Metadata.TryGetValue("creditPack", out var key)
            && !string.IsNullOrWhiteSpace(key))
        {
            var byKey = stripe.CreditPacks.FirstOrDefault(p =>
                string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (byKey is not null) return byKey;

            log.LogError(
                "Checkout metadata names credit pack '{Key}' but no such pack is configured. " +
                "A price was almost certainly removed from config while sessions were in flight.", key);
        }

        return evt.PriceId is { } price ? stripe.CreditPackForPrice(price) : null;
    }

    /// <summary>
    /// Two ways back to our user: the client_reference_id we set on Checkout,
    /// or the Stripe customer id we stored on first purchase. Checkout events
    /// have the first; subscription events only ever have the second.
    /// </summary>
    private async Task<User?> ResolveUserAsync(StripeWebhookEvent evt, CancellationToken ct)
    {
        if (evt.ClientReferenceId is { Length: > 0 } userId)
        {
            var byId = await users.FindByIdAsync(userId, ct);
            if (byId is not null) return byId;
        }

        if (evt.CustomerId is { Length: > 0 } customerId)
            return await users.FindByStripeCustomerIdAsync(customerId, ct);

        return null;
    }

    private async Task<string> EnsureCustomerAsync(User user, CancellationToken ct)
    {
        if (user.Billing.StripeCustomerId is { } existing) return existing;

        var customerId = await stripe.EnsureCustomerAsync(
            user.Id, user.Email.Value, user.DisplayName, null, ct);

        // Persist immediately. If the user abandons checkout we still want the
        // customer bound, otherwise the next attempt creates a duplicate
        // customer and their payment history fragments.
        user.LinkStripeCustomer(customerId);
        await users.UpdateAsync(user, ct);
        return customerId;
    }

    private string PriceIdFor(SubscriptionTier tier, BillingInterval interval) =>
        (tier, interval) switch
        {
            (SubscriptionTier.Starter, BillingInterval.Monthly) => stripe.PriceStarterMonthly,
            (SubscriptionTier.Starter, BillingInterval.Yearly) => stripe.PriceStarterYearly,
            (SubscriptionTier.Pro, BillingInterval.Monthly) => stripe.PriceProMonthly,
            (SubscriptionTier.Pro, BillingInterval.Yearly) => stripe.PriceProYearly,
            _ => string.Empty,
        };
}
