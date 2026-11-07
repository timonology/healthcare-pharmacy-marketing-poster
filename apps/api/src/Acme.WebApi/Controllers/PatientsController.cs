using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Common;
using Acme.Application.Patients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/patients")]
[Authorize]
public sealed class PatientsController(PatientService service, Acme.Application.Audit.IAuditScope audit) : ControllerBase
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
        [FromQuery] string? sort,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        // Date added: newest first unless sort=oldest.
        var query = new PatientQuery(userId, search, groupId, skip, take,
            Window: ListWindow.From(sort, from, to));
        var listResult = await service.ListAsync(query, ct);
        var total = await service.CountAsync(query, ct);

        return Ok(new PatientListResponse(total, skip, take, listResult.Value!));
    }

    [ActivityLog("patient.view", "patient")]
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PatientDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.GetAsync(id, userId, ct);
        return ToResponse(result, () => Ok(result.Value));
    }

    [Audit("patient.create", "patient")]
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

    [Audit("patient.update", "patient")]
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

    [Audit("patient.delete", "patient")]
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        var result = await service.DeleteAsync(id, userId, ct);
        return ToResponse(result, () => NoContent());
    }

    [Audit("patient.bulk_delete", "patient")]
    [HttpPost("bulk-delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> BulkDelete(
        [FromBody] BulkDeleteRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        // A body without the list binds it as null despite the declared type;
        // treat that as "none" rather than a 500.
        var ids = request.Ids ?? Array.Empty<string>();
        audit.AddMeta("patientCount", ids.Count);
        var result = await service.DeleteManyAsync(userId, ids, ct);
        return ToResponse(result, () => NoContent());
    }

    [Audit("patient.group.bulk_add", "patient_group")]
    [HttpPost("bulk-add-to-group")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> BulkAddToGroup(
        [FromBody] BulkAddToGroupRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        audit.SetEntity("patient_group", request.GroupId);
        var patientIds = request.PatientIds ?? Array.Empty<string>();
        audit.AddMeta("patientCount", patientIds.Count);
        var result = await service.BulkAddToGroupAsync(userId, patientIds, request.GroupId, ct);
        return ToResponse(result, () => Ok(new { added = result.Value }));
    }

    [Audit("patient.group.add", "patient")]
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

    [Audit("patient.group.remove", "patient")]
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

    [Audit("patient.import", "patient")]
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

    /* ── import and export ───────────────────────────────────────────── */

    /// <summary>
    /// Imports contacts from a CSV into the caller's own list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The owner is always the caller, never a parameter.</b> There is no
    /// way to name a target pharmacy on this endpoint, so no request can reach
    /// another pharmacy's contacts — the platform-admin equivalent lives on the
    /// admin controller behind its own policy.
    /// </para>
    /// <para>
    /// During manage-as the caller <i>is</i> the pharmacy being managed, because
    /// the delegated token's subject is the effective pharmacy. That is the
    /// behaviour wanted: a head office importing on behalf of a branch files the
    /// contacts under the branch.
    /// </para>
    /// </remarks>
    [Audit("patient.import", "patient")]
    [HttpPost("import")]
    [ProducesResponseType(typeof(PatientImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> Import(
        IFormFile file,
        [FromServices] PatientImportService import,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose a CSV file to import." });

        using var reader = new StreamReader(file.OpenReadStream());
        var csv = await reader.ReadToEndAsync(ct);

        var result = await import.ImportAsync(userId, csv, ct);

        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>The caller's own contacts as a CSV download.</summary>
    [Audit("patient.export", "patient")]
    [HttpGet("export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Export(
        [FromServices] PatientImportService import, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await import.ExportAsync(userId, ct);
        if (result.Kind != ResultKind.Success)
            return BadRequest(new { error = result.Error });

        return File(Encoding.UTF8.GetBytes(result.Value!), "text/csv", "contacts.csv");
    }

    /// <summary>A blank CSV in the shape the importer expects.</summary>
    [HttpGet("csv-template")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult CsvTemplate() =>
        File(Encoding.UTF8.GetBytes(PatientCsv.TemplateFile()),
             "text/csv", "contacts-import-template.csv");

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
