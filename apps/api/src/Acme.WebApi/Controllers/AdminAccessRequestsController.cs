using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Hierarchy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Reviewing requests for access to a pharmacy.
/// </summary>
/// <remarks>
/// <para>
/// Approving one of these grants an account the ability to read another
/// pharmacy's patient list. That puts it in the same category as a tier
/// override or a contact reveal — a platform action taken on a customer's
/// behalf — so it lives behind the admin policy and lands in the audit log.
/// </para>
/// <para>
/// Class-level policy so a route added later inherits it. The service re-checks
/// the role as well, because a controller attribute is one edit away from
/// being wrong and this decision is not one to get wrong quietly.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/access-requests")]
[Authorize(Policy = AuthPolicies.AdminArea)]
public sealed class AdminAccessRequestsController(
    PharmacyAccessRequestService accessRequests) : ControllerBase
{
    /// <summary>The review queue. Pending first.</summary>
    [ActivityLog("admin.console.access_requests")]
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AccessRequestReviewDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] bool pendingOnly = true,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var result = await accessRequests.ListForReviewAsync(pendingOnly, take, ct);
        return Respond(result);
    }

    /// <summary>Approves or rejects one request.</summary>
    /// <remarks>
    /// Approval creates the membership; rejection creates nothing. Either way
    /// the decision is recorded once and cannot be revisited — a request that
    /// could be re-decided would let an approval be quietly replaced while the
    /// membership it created stayed behind.
    /// </remarks>
    [Audit("admin.access.review", "access_request")]
    [HttpPost("{id}/review")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Review(
        string id, [FromBody] ReviewAccessRequestBody body, CancellationToken ct)
    {
        if (CurrentUserId() is not { } reviewerId) return Unauthorized();

        var result = await accessRequests.ReviewAsync(
            reviewerId, id, body?.Approve ?? false, body?.Note, ct);

        return result.Kind switch
        {
            ResultKind.Success => NoContent(),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    private IActionResult Respond<T>(Result<T> result) => result.Kind switch
    {
        ResultKind.Success => Ok(result.Value),
        ResultKind.NotFound => NotFound(new { error = result.Error }),
        ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
        _ => BadRequest(new { error = result.Error }),
    };
}

/// <summary>A decision on a request.</summary>
public sealed record ReviewAccessRequestBody(bool Approve, string? Note);
