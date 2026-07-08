using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Patients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/patients")]
[Authorize]
public sealed class PatientsController(PatientService service) : ControllerBase
{
    public sealed record PatientListResponse(
        int Total,
        int Skip,
        int Take,
        IReadOnlyList<PatientDto> Items);

    [HttpGet]
    [ProducesResponseType(typeof(PatientListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] string? groupId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        var query = new PatientQuery(userId, search, groupId, skip, take);
        var listResult = await service.ListAsync(query, ct);
        var total = await service.CountAsync(query, ct);

        return Ok(new PatientListResponse(total, skip, take, listResult.Value!));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PatientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.GetAsync(id, userId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [HttpPost]
    [ProducesResponseType(typeof(PatientDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        [FromBody] UpsertPatientRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.CreateAsync(userId, request, ct);
        return ToResponse(result, () => StatusCode(StatusCodes.Status201Created, result.Value));
    }

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(PatientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpsertPatientRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.UpdateAsync(id, userId, request, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.DeleteAsync(id, userId, ct);
        return ToResponse(result, () => NoContent());
    }

    [HttpPost("bulk-delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> BulkDelete(
        [FromBody] BulkDeleteRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.DeleteManyAsync(userId, request.Ids, ct);
        return ToResponse(result, () => NoContent());
    }

    [HttpPost("bulk-add-to-group")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkAddToGroup(
        [FromBody] BulkAddToGroupRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.BulkAddToGroupAsync(userId, request.PatientIds, request.GroupId, ct);
        return ToResponse(result, () => Ok(new { added = result.Value }));
    }

    [HttpPost("{id}/groups")]
    [ProducesResponseType(typeof(PatientDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddToGroup(
        string id,
        [FromBody] AddRemoveGroupRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.AddToGroupAsync(id, userId, request.GroupId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [HttpDelete("{id}/groups/{groupId}")]
    [ProducesResponseType(typeof(PatientDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RemoveFromGroup(
        string id,
        string groupId,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.RemoveFromGroupAsync(id, userId, groupId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [HttpPost("bulk-import")]
    [ProducesResponseType(typeof(BulkImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BulkImport(
        [FromBody] BulkImportRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.BulkImportAsync(userId, request.Patients, ct);
        return ToResponse(result, () => Ok(result.Value));
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
