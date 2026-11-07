using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Google Ads campaigns built from the pharmacy's own posters and videos.
/// </summary>
/// <remarks>
/// <para>
/// Enabling a campaign is a separate call from creating one, deliberately.
/// Campaigns are created paused, and starting the spend is its own decision
/// with its own request — a wizard whose final step begins charging a card is
/// a wizard nobody can safely explore.
/// </para>
/// <para>
/// Healthcare advertising is regulated, and the platform rules are stricter
/// than general advertising: Google restricts prescription drug terms, and
/// personalised advertising based on health conditions is prohibited. This API
/// does not police ad copy — the pharmacy is the advertiser of record — but the
/// UI carries that warning where the copy is written.
/// </para>
/// </remarks>
[ApiController]
[Route("api/ads")]
[Authorize]
public sealed class GoogleAdsController(GoogleAdsService ads) : ControllerBase
{
    [HttpGet("campaigns")]
    [ProducesResponseType(typeof(IReadOnlyList<GoogleAdsCampaignDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Campaigns(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await ads.ListAsync(userId, ct));
    }

    /// <summary>Google Ads accounts reachable through a connection.</summary>
    [HttpGet("connections/{connectionId}/accounts")]
    [ProducesResponseType(typeof(IReadOnlyList<AdsAccountDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Accounts(string connectionId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await ads.ListAccountsAsync(userId, connectionId, ct));
    }

    /// <summary>Creates a campaign. It will be paused until explicitly enabled.</summary>
    /// <remarks>
    /// The Ads account is the connection's own. <c>customerId</c> is optional
    /// and, when given, must match it.
    /// </remarks>
    [Audit("campaign.ads.create", "ads_campaign")]
    [HttpPost("campaigns")]
    [ProducesResponseType(typeof(GoogleAdsCampaignDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(
        [FromBody] CreateAdsCampaignRequest body,
        [FromQuery] string? customerId,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (body is null) return BadRequest(new { error = "A campaign is required." });

        return Respond(await ads.CreateAsync(userId, body, customerId, ct));
    }

    [Audit("campaign.ads.enable", "ads_campaign")]
    [HttpPost("campaigns/{id}/enable")]
    [ProducesResponseType(typeof(GoogleAdsCampaignDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Enable(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await ads.SetStatusAsync(userId, id, enabled: true, ct));
    }

    [Audit("campaign.ads.pause", "ads_campaign")]
    [HttpPost("campaigns/{id}/pause")]
    [ProducesResponseType(typeof(GoogleAdsCampaignDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Pause(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await ads.SetStatusAsync(userId, id, enabled: false, ct));
    }

    /// <summary>
    /// Pulls the latest metrics from Google.
    /// </summary>
    /// <remarks>
    /// On demand rather than on a timer. Google's figures move for days after
    /// the fact as invalid traffic is filtered, so a background sync would
    /// produce numbers that change without anyone asking and invite someone to
    /// reconcile them against the Ads console.
    /// </remarks>
    [NoAudit("Refreshes cached figures from Google; changes nothing in the ad account.")]
    [HttpPost("campaigns/{id}/sync")]
    [ProducesResponseType(typeof(GoogleAdsCampaignDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Sync(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await ads.SyncMetricsAsync(userId, id, ct));
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

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
