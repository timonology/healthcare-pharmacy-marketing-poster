using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Displays;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Screens and playlists, for the signed-in pharmacy.
/// </summary>
/// <remarks>
/// Owner id is the token subject, so a head office working as a branch manages
/// that branch's screens with no extra plumbing — the same way every other
/// resource in the app already behaves.
/// </remarks>
[ApiController]
[Route("api/displays")]
[Authorize]
public sealed class DisplaysController(DisplayService displays) : ControllerBase
{
    /// <summary>
    /// The pharmacy these screens belong to.
    /// </summary>
    /// <remarks>
    /// <b>Sub first, and the order is load-bearing.</b> A delegated token puts
    /// the effective pharmacy in <c>sub</c> and the person acting in <c>act</c>;
    /// reading NameIdentifier first would resolve a head office working as a
    /// branch back to the head office, and every screen would be filed against
    /// the wrong pharmacy. Every other controller does it this way.
    /// </remarks>
    private string? OwnerId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    /* ── screens ──────────────────────────────────────────────────────── */

    [HttpGet("screens")]
    public async Task<IActionResult> ListScreens(CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Ok(await displays.ListScreensAsync(ownerId, ct));
    }

    [Audit("display.create", "display")]
    [HttpPost("screens")]
    public async Task<IActionResult> RegisterScreen(
        [FromBody] RegisterScreenRequest request, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.RegisterScreenAsync(ownerId, request, ct));
    }

    [Audit("display.update", "display")]
    [HttpPut("screens/{id}")]
    public async Task<IActionResult> UpdateScreen(
        string id, [FromBody] UpdateScreenRequest request, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.UpdateScreenAsync(ownerId, id, request, ct));
    }

    [Audit("display.playlist.assign", "display")]
    [HttpPut("screens/{id}/playlist")]
    public async Task<IActionResult> AssignPlaylist(
        string id, [FromBody] AssignPlaylistRequest request, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.AssignPlaylistAsync(ownerId, id, request, ct));
    }

    [Audit("display.delete", "display")]
    [HttpDelete("screens/{id}")]
    public async Task<IActionResult> DeleteScreen(string id, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.DeleteScreenAsync(ownerId, id, ct));
    }

    [Audit("display.pairing_code.issue", "display")]
    [HttpPost("screens/{id}/pairing-code")]
    public async Task<IActionResult> RefreshPairingCode(string id, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.RefreshPairingCodeAsync(ownerId, id, ct));
    }

    /// <summary>Mints a direct link. The token is in the response and nowhere else.</summary>
    [Audit("display.direct_link.issue", "display")]
    [HttpPost("screens/{id}/direct-link")]
    public async Task<IActionResult> IssueDirectLink(string id, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.IssueDirectLinkAsync(ownerId, id, ct));
    }

    [Audit("display.credential.revoke", "display")]
    [HttpDelete("screens/{id}/credentials/{credentialId}")]
    public async Task<IActionResult> RevokeCredential(
        string id, string credentialId, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.RevokeCredentialAsync(ownerId, id, credentialId, ct));
    }

    /* ── playlists ────────────────────────────────────────────────────── */

    [HttpGet("playlists")]
    public async Task<IActionResult> ListPlaylists(CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Ok(await displays.ListPlaylistsAsync(ownerId, ct));
    }

    [Audit("display.playlist.create", "playlist")]
    [HttpPost("playlists")]
    public async Task<IActionResult> CreatePlaylist(
        [FromBody] UpsertPlaylistRequest request, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.CreatePlaylistAsync(ownerId, request, ct));
    }

    [Audit("display.playlist.update", "playlist")]
    [HttpPut("playlists/{id}")]
    public async Task<IActionResult> UpdatePlaylist(
        string id, [FromBody] UpsertPlaylistRequest request, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.UpdatePlaylistAsync(ownerId, id, request, ct));
    }

    [Audit("display.playlist.delete", "playlist")]
    [HttpDelete("playlists/{id}")]
    public async Task<IActionResult> DeletePlaylist(string id, CancellationToken ct)
    {
        if (OwnerId() is not { } ownerId) return Unauthorized();
        return Respond(await displays.DeletePlaylistAsync(ownerId, id, ct));
    }

    private IActionResult Respond<T>(Result<T> result) => result.Kind switch
    {
        ResultKind.Success => Ok(result.Value),
        ResultKind.NotFound => NotFound(new { error = result.Error }),
        ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
        ResultKind.Conflict => Conflict(new { error = result.Error }),
        _ => BadRequest(new { error = result.Error }),
    };
}
