using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Hierarchy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Which pharmacy you are working as, and moving between them.
/// </summary>
/// <remarks>
/// <para>
/// Its own controller rather than a pair of methods on
/// <c>PharmaciesController</c>, for a reason that is easy to miss: that
/// controller refuses delegated tokens outright, because a parent managing a
/// child must not be able to reorganise accounts while wearing the child's
/// identity. Switching is the opposite — it has to work <i>while</i> delegated,
/// or there is no way back to your own pharmacy short of signing out.
/// </para>
/// <para>
/// <b>Identity here is the actor, not the subject.</b> Every other controller
/// reads <c>sub</c> to answer "whose data is this", and when a token is
/// delegated <c>sub</c> is the pharmacy being worked as. These two endpoints
/// need the human instead, so they read <c>act</c> first. Getting that backwards
/// would list the pharmacy's memberships rather than the person's — and make
/// switching back impossible.
/// </para>
/// </remarks>
[ApiController]
[Route("api/workspaces")]
[Authorize]
public sealed class WorkspacesController(
    PharmacyContextService context,
    PharmacyAccessRequestService accessRequests) : ControllerBase
{
    /// <summary>The pharmacies the signed-in person may work as.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(WorkspacesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (ActorId() is not { } actorId) return Unauthorized();

        var result = await context.ListWorkspacesAsync(actorId, EffectivePharmacyId() ?? actorId, ct);
        return Respond(result);
    }

    /// <summary>
    /// Starts working as a pharmacy.
    /// </summary>
    /// <remarks>
    /// The pharmacy id in the body is a request, not a fact — it is resolved
    /// against a membership row before any token exists. Switching to your own
    /// pharmacy goes through the same path, so there is no special case to
    /// forget.
    /// </remarks>
    [Audit("auth.act_as.start", "pharmacy")]
    [HttpPost("switch")]
    [ProducesResponseType(typeof(SwitchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Switch(
        [FromBody] SwitchWorkspaceRequest request, CancellationToken ct)
    {
        if (ActorId() is not { } actorId) return Unauthorized();

        return Respond(await context.SwitchAsync(actorId, request?.PharmacyId ?? "", ct));
    }

    /// <summary>
    /// Records the end of a stint working as another pharmacy.
    /// </summary>
    /// <remarks>
    /// The browser leaves by deleting its own cookie, which needs no server
    /// call — so this exists only to close the audit window. It answers 204
    /// whatever happens: failing here would leave the caller unable to leave a
    /// pharmacy over a logging problem.
    /// </remarks>
    [Audit("auth.act_as.stop", "pharmacy")]
    [HttpPost("stop")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Stop(CancellationToken ct)
    {
        if (ActorId() is not { } actorId) return Unauthorized();

        await context.StopActingAsAsync(actorId, EffectivePharmacyId() ?? "", ct);
        return NoContent();
    }

    /* ── asking for access to somebody else's pharmacy ───────────────── */

    /// <summary>
    /// Asks to be given access to a pharmacy, by its email address.
    /// </summary>
    /// <remarks>
    /// Always 202, whatever the address. Reporting "no such pharmacy" would
    /// make this a way to test which addresses are registered — the same
    /// reasoning as the password-reset endpoint, and the same uniform answer.
    ///
    /// The reason is validated here rather than in the service, because a
    /// complaint about your own input is safe to reflect and gives the person
    /// something to fix.
    /// </remarks>
    [Audit("pharmacy.access.request", "access_request")]
    [HttpPost("access-requests")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RequestAccess(
        [FromBody] AccessRequestBody body, CancellationToken ct)
    {
        if (ActorId() is not { } actorId) return Unauthorized();

        var reason = (body?.Reason ?? "").Trim();
        if (reason.Length < 10)
            return BadRequest(new { error = "Please say why you need access, in a sentence or two." });
        if (reason.Length > 500)
            return BadRequest(new { error = "Please keep the reason under 500 characters." });

        await accessRequests.RequestAsync(actorId, body?.PharmacyEmail ?? "", reason, ct);

        return Accepted(new
        {
            message = "Request submitted. A Marketing25 administrator will review it.",
        });
    }

    /// <summary>What this person has asked for, and how it went.</summary>
    [HttpGet("access-requests")]
    [ProducesResponseType(typeof(IReadOnlyList<AccessRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> MyAccessRequests(CancellationToken ct)
    {
        if (ActorId() is not { } actorId) return Unauthorized();

        return Respond(await accessRequests.ListMineAsync(actorId, ct));
    }

    /// <summary>
    /// The real person behind the request.
    /// </summary>
    /// <remarks>
    /// The actor claim when the token is delegated, the subject otherwise. This
    /// is the one place in the API where that order is deliberate.
    /// </remarks>
    private string? ActorId() =>
        User.FindFirstValue(DelegationClaims.Actor)
        ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>The pharmacy currently being worked as — always the subject.</summary>
    private string? EffectivePharmacyId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    private IActionResult Respond<T>(Result<T> result) => result.Kind switch
    {
        ResultKind.Success => Ok(result.Value),
        ResultKind.NotFound => NotFound(new { error = result.Error }),
        ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
        ResultKind.Invalid => BadRequest(new { error = result.Error }),
        _ => BadRequest(new { error = result.Error }),
    };
}

/// <summary>Which pharmacy to start working as.</summary>
public sealed record SwitchWorkspaceRequest(string PharmacyId);

/// <summary>A request for access to somebody else's pharmacy.</summary>
public sealed record AccessRequestBody(string PharmacyEmail, string Reason);
