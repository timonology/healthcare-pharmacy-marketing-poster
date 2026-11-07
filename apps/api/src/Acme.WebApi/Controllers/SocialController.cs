using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Acme.Application.Common;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Connecting external accounts, and publishing to them.
/// </summary>
/// <remarks>
/// <para>
/// Pharmacy-facing. Everything is scoped to the caller's own id, taken from the
/// validated token and never from the request — a connection id is authority
/// over someone's Facebook Page, so it is not something to trust from a body.
/// </para>
/// <para>
/// <b>No response from this controller contains a token.</b> The DTOs have no
/// field for one, and the OAuth flow keeps credentials server-side throughout:
/// the browser sees opaque handles and account names, never the values behind
/// them.
/// </para>
/// </remarks>
[ApiController]
[Route("api/social")]
[Authorize]
public sealed class SocialController(
    SocialConnectionService connections,
    PublishService publishing,
    TikTokCreatorService tikTok) : ControllerBase
{
    /* ---------------- connections ---------------- */

    [HttpGet("connections")]
    [ProducesResponseType(typeof(IReadOnlyList<SocialConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Connections(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await connections.ListAsync(userId, ct));
    }

    /// <summary>What the pharmacy can connect here, and why not when it cannot.</summary>
    [HttpGet("availability")]
    [ProducesResponseType(typeof(SocialAvailabilityDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Availability(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await connections.AvailabilityAsync(userId, ct));
    }

    /// <summary>
    /// Begins an OAuth flow and returns the URL to send the pharmacy to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>state</c> is generated here, not by the client, and recorded
    /// server-side against the caller and provider. The callback is refused
    /// unless it brings back a state issued here, to the same pharmacy, unused.
    /// Accepting a client-supplied state would let an attacker fix it in
    /// advance — the setup for a CSRF that attaches their account to someone
    /// else's pharmacy.
    /// </para>
    /// <para>
    /// The redirect URI is the server's own (the configured public web
    /// address). A client may send what it expects, and is refused if that
    /// differs, but cannot choose it.
    /// </para>
    /// <para>
    /// The state is returned so the browser can also check it on return —
    /// the second lock, not the first.
    /// </para>
    /// </remarks>
    [Audit("social.connect.start", "social_connection")]
    [HttpPost("connections/start")]
    [ProducesResponseType(typeof(OAuthStartDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Start(
        [FromBody] OAuthStartRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (body is null) return BadRequest(new { error = "Choose what to connect." });

        // 32 bytes of CSPRNG, url-safe. Doubles as the session id, so the
        // callback can find the pending flow without a second parameter.
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));

        return Respond(await connections.StartAsync(userId, body.Kind, body.RedirectUri, state, ct));
    }

    /// <summary>
    /// Completes an OAuth flow and lists the accounts that were found.
    /// </summary>
    /// <remarks>
    /// Nothing is connected here. The pharmacy may administer five Pages and
    /// want one; storing credentials for all of them because they appeared in a
    /// response is not consent. Any redirect URI in the body is ignored: the
    /// exchange uses the one recorded at Start.
    /// </remarks>
    [Audit("social.connect.complete", "social_connection")]
    [HttpPost("connections/callback")]
    [ProducesResponseType(typeof(IReadOnlyList<DiscoveredAccountDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Callback(
        [FromBody] OAuthCallbackRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (body is null || string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.State))
            return BadRequest(new { error = "The callback was missing its code or state." });

        return Respond(await connections.CompleteAsync(userId, body.Kind, body.Code, body.State, ct));
    }

    /// <summary>Persists the accounts the pharmacy chose from the callback.</summary>
    [Audit("social.connect", "social_connection")]
    [HttpPost("connections")]
    [ProducesResponseType(typeof(IReadOnlyList<SocialConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Connect(
        [FromBody] ConnectAccountsRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (body is null || body.Handles is null || body.Handles.Count == 0)
            return BadRequest(new { error = "Choose at least one account." });

        return Respond(await connections.ConnectAsync(userId, body, ct));
    }

    [Audit("social.disconnect", "social_connection")]
    [HttpDelete("connections/{id}")]
    public async Task<IActionResult> Disconnect(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await connections.DisconnectAsync(userId, id, ct));
    }

    /// <summary>
    /// TikTok's live creator info for one of the caller's TikTok accounts: the
    /// share dialog shows it, as TikTok's sharing guidelines require.
    /// </summary>
    [HttpGet("connections/{id}/tiktok-creator")]
    [ProducesResponseType(typeof(TikTokCreatorInfo), StatusCodes.Status200OK)]
    public async Task<IActionResult> TikTokCreator(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await tikTok.GetAsync(userId, id, ct));
    }

    /* ---------------- publishing ---------------- */

    [HttpGet("posts")]
    [ProducesResponseType(typeof(ScheduledPostListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Posts(
        [FromQuery] PublishStatus? status,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        // "upcoming" or "history". Filtered here, so a busy history cannot
        // push upcoming posts off the page the UI asked for.
        [FromQuery] string? view = null,
        CancellationToken ct = default)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await publishing.ListAsync(
            userId, status, skip, take, ScheduledPostViews.For(view), ct));
    }

    [HttpGet("posts/{id}")]
    [ProducesResponseType(typeof(ScheduledPostDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Post(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await publishing.GetAsync(userId, id, ct));
    }

    /// <summary>
    /// Queues a poster or video for publishing, now or later.
    /// </summary>
    /// <remarks>
    /// "Now" still goes through the queue rather than publishing inline. A
    /// request that blocked for the length of an Instagram video ingestion
    /// would time out at the proxy long before it finished, and the pharmacy
    /// would retry — producing exactly the duplicate the whole design avoids.
    /// </remarks>
    [Audit("social.post.schedule", "social_post")]
    [HttpPost("posts")]
    [ProducesResponseType(typeof(ScheduledPostDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreatePost(
        [FromBody] CreatePublishRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (body is null) return BadRequest(new { error = "A publish request is required." });

        return Respond(await publishing.CreateAsync(userId, body, ct));
    }

    [Audit("social.post.retry", "social_post")]
    [HttpPost("posts/{id}/retry")]
    [ProducesResponseType(typeof(ScheduledPostDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Retry(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await publishing.RetryAsync(userId, id, ct));
    }

    [Audit("social.post.cancel", "social_post")]
    [HttpPost("posts/{id}/cancel")]
    public async Task<IActionResult> Cancel(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await publishing.CancelAsync(userId, id, ct));
    }

    [Audit("social.post.reschedule", "social_post")]
    [HttpPost("posts/{id}/reschedule")]
    [ProducesResponseType(typeof(ScheduledPostDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reschedule(
        string id, [FromBody] ReschedulePostRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await publishing.RescheduleAsync(userId, id, body?.ScheduledForUtc, ct));
    }

    /* ---------------- plumbing ---------------- */

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>Base64url — safe in a query string without further escaping.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private IActionResult Respond<T>(Result<T> result) => result.Kind switch
    {
        ResultKind.Success => Ok(result.Value),
        ResultKind.NotFound => NotFound(new { error = result.Error }),
        ResultKind.Invalid => BadRequest(new { error = result.Error }),
        ResultKind.Conflict => Conflict(new { error = result.Error }),
        ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
        ResultKind.Unauthorized => Unauthorized(new { error = result.Error }),
        _ => BadRequest(new { error = result.Error }),
    };
}

/// <param name="RedirectUri">Optional: what the browser expects. Checked, never used as given.</param>
public sealed record OAuthStartRequest(SocialOAuthProviderKind Kind, string? RedirectUri = null);

/// <param name="RedirectUri">Ignored; kept so older clients still bind. The exchange uses the URI recorded at Start.</param>
public sealed record OAuthCallbackRequest(
    SocialOAuthProviderKind Kind,
    string Code,
    string State,
    string? RedirectUri = null);
