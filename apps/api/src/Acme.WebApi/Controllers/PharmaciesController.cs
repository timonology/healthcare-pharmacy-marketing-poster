using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.Application.Admin;
using Acme.Application.Common;
using Acme.Application.Hierarchy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// A pharmacy's own sub-pharmacies.
/// </summary>
/// <remarks>
/// <para>
/// Pharmacy-facing, and scoped to the caller throughout — every method uses the
/// subject from the validated token as the parent, never an id from the
/// request. A parent id taken from a body would let anyone name someone else's
/// account and adopt it.
/// </para>
/// <para>
/// <b>Nothing here may be reached with a delegated token.</b> A parent managing
/// a child must not be able to create sub-pharmacies under that child, detach
/// them, or hop to a third account — see <see cref="IsDelegated"/>.
/// </para>
/// </remarks>
[ApiController]
[Route("api/pharmacies")]
[Authorize]
public sealed class PharmaciesController(
    PharmacyHierarchyService hierarchy,
    // Shares the staff overview implementation so a head office and an
    // administrator never see different numbers for the same branch.
    PharmacySupportService support) : ControllerBase
{
    [HttpGet("children")]
    [ProducesResponseType(typeof(IReadOnlyList<ChildPharmacyDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Children(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await hierarchy.ListChildrenAsync(userId, ct));
    }

    /// <summary>
    /// Whether this pharmacy is a branch of another, and of whom.
    /// </summary>
    /// <remarks>
    /// Replaces an earlier <c>GET parent</c> that answered with the parent or a
    /// bare null. This one always returns a body, because "no parent" and "the
    /// request did not produce a value" have to be distinguishable and a null
    /// body is not.
    /// </remarks>
    [HttpGet("context")]
    [ProducesResponseType(typeof(PharmacyContextDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Context(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await hierarchy.GetContextAsync(userId, ct));
    }

    /// <summary>Creates a sub-pharmacy and emails it an activation link.</summary>
    [Audit("pharmacy.branch.create", "pharmacy")]
    [HttpPost("children")]
    [ProducesResponseType(typeof(ChildPharmacyDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateChild(
        [FromBody] CreateChildPharmacyRequest body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (IsDelegated) return DelegationRefused();
        if (body is null) return BadRequest(new { error = "Pharmacy details are required." });

        return Respond(await hierarchy.CreateChildAsync(userId, body, ct));
    }

    /// <summary>Removes a sub-pharmacy from the group. The account survives.</summary>
    [Audit("pharmacy.branch.detach", "pharmacy", IdRoute = "childId")]
    [HttpDelete("children/{childId}")]
    public async Task<IActionResult> DetachChild(string childId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (IsDelegated) return DelegationRefused();

        return Respond(await hierarchy.DetachChildAsync(userId, childId, ct));
    }

    /// <summary>
    /// Starts a "manage as" session over a sub-pharmacy.
    /// </summary>
    /// <remarks>
    /// Returns a short-lived token whose subject is the child, so every
    /// existing endpoint operates on the child's data without modification,
    /// while the actor claim keeps the parent's identity attached for logging.
    ///
    /// No refresh token is issued. When it lapses the parent is back in their
    /// own account rather than holding an open-ended session over someone
    /// else's.
    /// </remarks>
    /// <summary>
    /// A branch's records, for the head office that runs it.
    /// </summary>
    /// <remarks>
    /// <b>The read is audited.</b> A head office can see across its branches
    /// without switching into them, and switching is what used to leave the
    /// trail — so this records who looked at which branch, and when. The
    /// pharmacy is the data controller for its patients; every route to that
    /// data has to be answerable, including the ones inside the same group.
    /// </remarks>
    [ActivityLog("pharmacy.branch.view", "pharmacy", IdRoute = "childId", PharmacyRoute = "childId")]
    [HttpGet("children/{childId}/overview")]
    [ProducesResponseType(typeof(PharmacySupportOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BranchOverview(string childId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Respond(await support.GetBranchOverviewAsync(userId, childId, ct));
    }

    [Audit("auth.act_as.issue", "pharmacy", IdRoute = "childId", PharmacyRoute = "childId")]
    [HttpPost("children/{childId}/manage-as")]
    [ProducesResponseType(typeof(ManageAsTokenDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ManageAs(string childId, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        return Respond(
            await hierarchy.IssueManageAsTokenAsync(userId, IsDelegated, childId, ct));
    }

    /// <summary>
    /// True when the caller is already acting on someone else's behalf.
    /// </summary>
    /// <remarks>
    /// The presence of the actor claim is the marker. Everything that changes
    /// the shape of the hierarchy checks this, because a parent managing a
    /// child must not be able to reorganise accounts while wearing the child's
    /// identity — the child would appear to have done it.
    ///
    /// Someone back in their own account through the workspace switcher holds
    /// a token whose actor is themselves; that is not delegation, and refusing
    /// it told people "you are managing another pharmacy" in their own account.
    /// See <see cref="Delegation.IsActingForAnother"/>.
    /// </remarks>
    private bool IsDelegated => User.IsActingForAnother();

    private IActionResult DelegationRefused() =>
        StatusCode(StatusCodes.Status403Forbidden, new
        {
            error = "You are managing another pharmacy. Return to your own account to do this.",
        });

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
