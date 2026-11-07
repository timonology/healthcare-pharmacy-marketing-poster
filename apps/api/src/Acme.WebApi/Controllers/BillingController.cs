using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Billing;
using Acme.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/billing")]
[Authorize]
public sealed class BillingController(
    BillingService billing,
    IStripeGateway stripe,
    ILogger<BillingController> log) : ControllerBase
{
    /// <summary>Current plan, renewal date, credit balance, available packs.</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(BillingStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await billing.GetStatusAsync(userId, ct);

        // Visible to somebody working as this pharmacy — a head office may need
        // to see that a branch is out of videos — but not changeable by them.
        // Said here so the page can explain instead of offering buttons that
        // would only be refused.
        if (result.Kind == ResultKind.Success && User.IsActingForAnother())
            return Ok(result.Value! with { CanManageBilling = false });

        return ToResponse(result);
    }

    /// <summary>
    /// Starts a subscription checkout. The body names a tier and interval —
    /// never a price or an amount, so a tampered request can only ever pick a
    /// different published plan, not a different price.
    /// </summary>
    [Audit("subscription.checkout.start", "subscription")]
    [OwnAccountOnly]
    [HttpPost("checkout")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(CheckoutSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Checkout([FromBody] StartCheckoutRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await billing.StartSubscriptionCheckoutAsync(userId, request.Tier, request.Interval, ct);
        return ToResponse(result);
    }

    /// <summary>Starts a one-time credit-pack purchase.</summary>
    [Audit("subscription.credits.checkout", "subscription")]
    [OwnAccountOnly]
    [HttpPost("credits")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(CheckoutSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BuyCredits([FromBody] BuyCreditsRequest request, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await billing.StartCreditPackCheckoutAsync(userId, request.PackKey, ct);
        return ToResponse(result);
    }

    /// <summary>Opens Stripe's billing portal for card, plan, invoices, cancellation.</summary>
    [Audit("subscription.portal.open", "subscription")]
    [OwnAccountOnly]
    [HttpPost("portal")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(CheckoutSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Portal(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await billing.CreatePortalSessionAsync(userId, ct);
        return ToResponse(result);
    }

    /// <summary>
    /// Stripe webhook receiver.
    ///
    /// Anonymous by necessity — Stripe has no bearer token. Authentication is
    /// the HMAC signature in <c>Stripe-Signature</c>, verified against the
    /// endpoint's signing secret before a single byte of the payload is
    /// trusted. An unsigned or wrongly-signed request is rejected outright:
    /// without that check this endpoint would be a free "make me Pro" button.
    /// </summary>
    [Audit("subscription.webhook", "subscription", SystemActor = "stripe")]
    [HttpPost("webhook")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // Read the raw body. Model binding would deserialise and re-serialise,
        // changing the bytes and invalidating the signature.
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (string.IsNullOrEmpty(signature))
        {
            log.LogWarning("Stripe webhook received with no signature header — rejected");
            return BadRequest();
        }

        var evt = stripe.ParseWebhook(payload, signature);
        if (evt is null) return BadRequest();

        try
        {
            var ok = await billing.HandleWebhookAsync(evt, ct);
            // A non-2xx tells Stripe to retry with backoff, which is what we
            // want for transient faults (Mongo blip) and not what we want for
            // events we simply don't handle — hence HandleWebhookAsync
            // returning true for those.
            return ok ? Ok() : StatusCode(StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to process Stripe event {EventId} ({Type})", evt.Id, evt.Type);
            return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    private IActionResult ToResponse<T>(Result<T> result) => result.Kind switch
    {
        ResultKind.Success => Ok(result.Value),
        ResultKind.NotFound => NotFound(new { error = result.Error }),
        ResultKind.Invalid => BadRequest(new { error = result.Error }),
        ResultKind.Forbidden => Forbid(),
        _ => BadRequest(new { error = result.Error }),
    };

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
