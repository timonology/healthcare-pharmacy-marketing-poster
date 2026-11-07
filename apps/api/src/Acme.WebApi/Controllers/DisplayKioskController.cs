using Acme.Application.Common;
using Acme.Application.Displays;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// What a screen on a wall is allowed to ask for. Anonymous by necessity.
/// </summary>
/// <remarks>
/// <para>
/// A kiosk has no cookie and no user. It holds one long random token, which it
/// sends in a header — not a query string, so the credential never lands in an
/// access log, a Referer, or anything a proxy writes down.
/// </para>
/// <para>
/// <b>Every route here is anonymous, so every route resolves the token first
/// and gets exactly one screen back.</b> Nothing accepts an id that identifies
/// a pharmacy, and nothing takes a parameter that widens what a token can
/// reach.
/// </para>
/// </remarks>
[ApiController]
[Route("api/display")]
[AllowAnonymous]
public sealed class DisplayKioskController(DisplayKioskService kiosk) : ControllerBase
{
    /// <summary>Where a device presents its token.</summary>
    public const string TokenHeader = "X-Display-Token";

    /// <summary>Exchanges a typed pairing code for a device token.</summary>
    [Audit("display.pair", "display", FailedAction = "display.pair_failed")]
    [EnableRateLimiting(RateLimits.DisplayClaim)]
    [HttpPost("claim")]
    [ProducesResponseType(typeof(DisplayTokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Claim(
        [FromBody] ClaimScreenRequest request, CancellationToken ct)
    {
        var result = await kiosk.ClaimAsync(request, ct);
        return result.Kind == ResultKind.Success
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// The playlist, or 304 when nothing has changed.
    /// </summary>
    /// <remarks>
    /// A screen polls this every thirty seconds for years. The ETag is what
    /// keeps that nearly free: an unchanged playlist costs one indexed lookup
    /// and an empty response, and the device keeps what it already has.
    /// </remarks>
    [Audit("display.content_denied", "display", OnlyWhenDenied = true)]
    [HttpGet("content")]
    [ProducesResponseType(typeof(KioskContentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Content(CancellationToken ct)
    {
        if (NoTokenSent()) return NoToken();
        if (await Session(ct) is not { } session) return UnknownToken();

        var content = await kiosk.ContentAsync(
            session, ct,
            canPlayVideo: DisplayMedia.CanPlayVideo(
                Request.Headers[DisplayMedia.CapabilitiesHeader].ToString()));
        var etag = $"\"{content.Revision}\"";

        // Recorded before the 304 short-circuit, so a screen that has been
        // showing the same loop for a month still counts as alive. Doing it
        // after would make every healthy screen look dead.
        await kiosk.NoteSeenAsync(session, ct);

        if (Request.Headers.TryGetValue(HeaderNames.IfNoneMatch, out var sent)
            && sent.Contains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers[HeaderNames.ETag] = etag;

        // No shared caching. A proxy holding one screen's playlist and handing
        // it to the next screen that asks would be a cross-tenant leak by
        // infrastructure rather than by code.
        Response.Headers[HeaderNames.CacheControl] = "private, no-store";

        return Ok(content);
    }

    /// <summary>One poster image, if it is on this screen's playlist.</summary>
    [Audit("display.content_denied", "poster", IdRoute = "posterId", OnlyWhenDenied = true)]
    [HttpGet("slides/{posterId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Slide(string posterId, CancellationToken ct)
    {
        if (NoTokenSent()) return NoToken();
        if (await Session(ct) is not { } session) return UnknownToken();

        var result = await kiosk.SlideAsync(session, posterId, ct);

        return result.Kind switch
        {
            ResultKind.Success => File(result.Value!.Content, result.Value!.ContentType),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private Task<KioskSession?> Session(CancellationToken ct) =>
        kiosk.ResolveAsync(Request.Headers[TokenHeader].ToString(), ct);

    private bool NoTokenSent() =>
        string.IsNullOrWhiteSpace(Request.Headers[TokenHeader].ToString());

    /// <summary>
    /// No credential was sent at all. A bad request, not a rejected one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from <see cref="UnknownToken"/> because the two mean different
    /// things to a screen, and it acts on the difference: a 401 makes it forget
    /// its token and ask to be paired again. If a missing header also answered
    /// 401, a proxy that stripped the header — or a single malformed request —
    /// would make a working screen destroy a perfectly good credential and
    /// demand a visit from an installer.
    /// </para>
    /// <para>
    /// It also keeps this endpoint honest about being anonymous. The pipeline
    /// lets an uncredentialed caller through; it is this handler that has
    /// nothing to say to one, and a handler saying "your request was
    /// incomplete" is a 400.
    /// </para>
    /// </remarks>
    private IActionResult NoToken() =>
        BadRequest(new { error = "This request needs a screen token." });

    /// <summary>
    /// One answer for every way a token can be wrong.
    /// </summary>
    /// <remarks>
    /// Revoked, never issued, and malformed are indistinguishable to the caller
    /// on purpose. The device's job is the same in all three cases — forget the
    /// token and ask to be paired again — and telling them apart would let
    /// somebody holding a stolen token learn whether it had been noticed.
    /// </remarks>
    private IActionResult UnknownToken() =>
        Unauthorized(new { error = "This screen is not paired. Pair it again from the Screens page." });
}
