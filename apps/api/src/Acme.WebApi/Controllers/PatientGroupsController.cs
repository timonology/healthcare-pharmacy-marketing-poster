using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Patients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/patient-groups")]
[Authorize]
public sealed class PatientGroupsController(PatientGroupService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PatientGroupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        return Ok(await service.ListAsync(userId, ct));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PatientGroupDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.GetAsync(id, userId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [HttpGet("{id}/members")]
    [ProducesResponseType(typeof(IReadOnlyList<PatientDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Members(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.ListMembersAsync(id, userId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [HttpPost]
    [ProducesResponseType(typeof(PatientGroupDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] UpsertPatientGroupRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.CreateAsync(userId, request, ct);
        return ToResponse(result, () => StatusCode(StatusCodes.Status201Created, result.Value));
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(PatientGroupDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpsertPatientGroupRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.UpdateAsync(id, userId, request, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.DeleteAsync(id, userId, ct);
        return ToResponse(result, () => NoContent());
    }

    private IActionResult ToResponse<T>(Result<T> result, Func<IActionResult> onSuccess) =>
        result.Kind switch
        {
            ResultKind.Success => onSuccess(),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            ResultKind.Conflict => Conflict(new { error = result.Error }),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            ResultKind.Unauthorized => Unauthorized(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
