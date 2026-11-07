using System.Text;
using Acme.Application.Invitations;
using Acme.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Inviting pharmacists. SuperAdmin only.
/// </summary>
/// <remarks>
/// Class-level policy so a route added later inherits it. Creating an account
/// and emailing a credential to a typed-in address is not something an ordinary
/// administrator should be able to do, and the service re-checks the role.
/// </remarks>
[ApiController]
[Route("api/admin/invitations")]
[Authorize(Policy = AuthPolicies.SuperAdminOnly)]
public sealed class InvitationsController(
    InvitationService invitations,
    IHostEnvironment env) : AdminApiController
{
    /// <summary>Invited pharmacists and where each one has got to.</summary>
    [ActivityLog("admin.console.invitations")]
    [HttpGet]
    [ProducesResponseType(typeof(InvitedListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] InvitationStatus? status,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await invitations.ListAsync(actor, search, status, skip, take, ct));
    }

    [Audit("admin.invite", "user")]
    [HttpPost]
    [ProducesResponseType(typeof(InviteResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Invite(
        [FromBody] InvitePharmacistRequest body, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (body is null) return BadRequest(new { error = "Pharmacist details are required." });

        return Respond(await invitations.InviteAsync(actor, body, ct));
    }

    /// <summary>Invites a list of pharmacists, reporting each row separately.</summary>
    [Audit("admin.invite.bulk", "user")]
    [HttpPost("bulk")]
    [ProducesResponseType(typeof(BulkInviteResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> InviteMany(
        [FromBody] BulkInviteRequest body, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (body is null) return BadRequest(new { error = "A list of pharmacists is required." });

        return Respond(await invitations.InviteManyAsync(actor, body, ct));
    }

    /// <summary>
    /// Parses an uploaded CSV without creating anything.
    /// </summary>
    /// <remarks>
    /// Separate from the import on purpose. A 200-row file pointed at the wrong
    /// columns should be visible before it creates 200 accounts and sends 200
    /// emails, neither of which can be taken back.
    /// </remarks>
    [NoAudit("Parses an uploaded CSV for preview; nothing is stored or sent.")]
    [HttpPost("preview-csv")]
    [ProducesResponseType(typeof(CsvPreviewDto), StatusCodes.Status200OK)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> PreviewCsv(IFormFile file, CancellationToken ct)
    {
        if (Context() is null) return Unauthorized();
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose a CSV file." });

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);

        return Ok(PharmacistCsv.Parse(content));
    }

    /// <summary>A blank CSV with the expected header and one example row.</summary>
    [HttpGet("csv-template")]
    public IActionResult CsvTemplate() =>
        File(Encoding.UTF8.GetBytes(PharmacistCsv.TemplateFile()),
             "text/csv", "pharmacist-import-template.csv");

    /// <summary>Reissues an invitation. Works on expired ones — that is the point.</summary>
    [Audit("admin.invite.resend", "user", IdRoute = "userId", PharmacyRoute = "userId")]
    [HttpPost("{userId}/resend")]
    [ProducesResponseType(typeof(InviteResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resend(string userId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await invitations.ResendAsync(actor, userId, ct));
    }

    /// <summary>
    /// Development only: reissues an invitation and returns the link.
    /// </summary>
    /// <remarks>
    /// So the end-to-end suite can complete a real activation. Without it the
    /// suite can only prove an invitation was created and that a bad token is
    /// refused — never that a genuine link opens the account, which is the only
    /// part a pharmacist experiences.
    /// <para>
    /// 404 outside Development, matching <c>/api/diagnostics/jwt</c>. This
    /// hands out a live credential for an account, so the check is on the host
    /// environment rather than on a config flag somebody could set by accident.
    /// The route is registered either way and simply refuses, because a route
    /// that exists and says no is easier to reason about than one that appears
    /// conditionally.
    /// </para>
    /// </remarks>
    [Audit("admin.invite.activation_link", "user", IdRoute = "userId", PharmacyRoute = "userId")]
    [HttpPost("{userId}/activation-link")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public async Task<IActionResult> ActivationLink(string userId, CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();
        if (Context() is not { } actor) return Unauthorized();

        var result = await invitations.ReissueLinkAsync(actor, userId, ct);
        return result.Kind == ResultKind.Success
            ? Ok(new { link = result.Value })
            : Respond(result);
    }
}

/// <summary>
/// Claiming an invitation. Anonymous by necessity.
/// </summary>
/// <remarks>
/// The pharmacist has no account to authenticate with, so the token is the only
/// credential. Both routes treat it as one: every failure returns the same
/// message, so someone holding a list of guessed links cannot learn which ones
/// exist.
/// </remarks>
[ApiController]
[Route("api/invitations")]
[AllowAnonymous]
public sealed class PublicInvitationsController(InvitationService invitations) : ControllerBase
{
    /// <summary>Confirms a link is live, and whose account it opens.</summary>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(InvitationPreviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview([FromQuery] string token, CancellationToken ct)
    {
        var result = await invitations.PreviewAsync(token, ct);

        return result.Kind == Application.Common.ResultKind.Success
            ? Ok(result.Value)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Sets the password and opens the account.</summary>
    [Audit("auth.invitation.accept", "user", FailedAction = "auth.invitation.accept_failed")]
    [EnableRateLimiting(RateLimits.Auth)]
    [HttpPost("accept")]
    public async Task<IActionResult> Accept(
        [FromBody] AcceptInvitationRequest body, CancellationToken ct)
    {
        if (body is null) return BadRequest(new { error = "A token and password are required." });

        var result = await invitations.AcceptAsync(body, ct);

        return result.Kind == Application.Common.ResultKind.Success
            ? Ok(new { activated = true })
            : BadRequest(new { error = result.Error });
    }
}
