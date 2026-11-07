using Acme.Application.Patients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// General (global) patient groups. SuperAdmin only.
/// </summary>
/// <remarks>
/// Class-level policy so a route added later inherits it. Everything here
/// affects every pharmacy at once — creating a group puts it in everyone's
/// list, deleting one removes it from patients across the platform.
///
/// Pharmacies reach these groups through the ordinary
/// <c>/api/patient-groups</c> endpoints, which return global groups alongside
/// their own but refuse to edit them.
/// </remarks>
[ApiController]
[Route("api/admin/global-groups")]
[Authorize(Policy = AuthPolicies.SuperAdminOnly)]
public sealed class GlobalGroupsController(GlobalGroupService groups) : AdminApiController
{
    [ActivityLog("admin.console.global_groups")]
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<GlobalGroupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await groups.ListAsync(actor, ct));
    }

    [Audit("admin.global_group.create", "global_group")]
    [HttpPost]
    [ProducesResponseType(typeof(GlobalGroupDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(
        [FromBody] UpsertPatientGroupRequest body, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (body is null) return BadRequest(new { error = "A name is required." });

        return Respond(await groups.CreateAsync(actor, body, ct));
    }

    [Audit("admin.global_group.update", "global_group")]
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(GlobalGroupDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        string id, [FromBody] UpsertPatientGroupRequest body, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (body is null) return BadRequest(new { error = "A name is required." });

        return Respond(await groups.UpdateAsync(actor, id, body, ct));
    }

    /// <summary>
    /// Deletes the group and removes it from every pharmacy's patients.
    /// </summary>
    /// <remarks>
    /// Not reversible, and it reaches into data owned by other accounts — the
    /// UI is expected to say so before calling this.
    /// </remarks>
    [Audit("admin.global_group.delete", "global_group")]
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await groups.DeleteAsync(actor, id, ct));
    }
}
