using System.Text;
using Acme.Application.Common;
using Acme.Application.Patients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Importing and exporting a chosen pharmacy's contacts, as platform staff.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>PatientsController</c> precisely because it takes a
/// pharmacy id. The pharmacy-facing endpoints have no such parameter — the
/// owner is always the caller — which is what makes them impossible to misuse.
/// Putting a staff-only override on the same controller would have removed
/// that property, and the review that noticed would have to happen every time.
/// </para>
/// <para>
/// Class-level policy so a route added later inherits it. Reading a pharmacy's
/// entire contact list is exactly the operation that must not become available
/// by someone forgetting an attribute.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/patients")]
[Authorize(Policy = AuthPolicies.AdminArea)]
public sealed class AdminPatientsController(PatientImportService import) : ControllerBase
{
    /// <summary>Imports contacts into the named pharmacy.</summary>
    /// <remarks>
    /// Used when a pharmacy sends their list to support rather than uploading
    /// it themselves, which for a single-handed pharmacist is common. The
    /// import is attributed to the pharmacy, not to the staff member.
    /// </remarks>
    [Audit("admin.patient.import", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpPost("{pharmacyId}/import")]
    [ProducesResponseType(typeof(PatientImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(5 * 1024 * 1024)]
    public async Task<IActionResult> Import(string pharmacyId, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose a CSV file to import." });

        using var reader = new StreamReader(file.OpenReadStream());
        var csv = await reader.ReadToEndAsync(ct);

        var result = await import.ImportAsync(pharmacyId, csv, ct);

        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>The named pharmacy's contacts as a CSV download.</summary>
    [Audit("admin.patient.export", "pharmacy", IdRoute = "pharmacyId", PharmacyRoute = "pharmacyId")]
    [HttpGet("{pharmacyId}/export.csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Export(string pharmacyId, CancellationToken ct)
    {
        var result = await import.ExportAsync(pharmacyId, ct);
        if (result.Kind != ResultKind.Success)
            return BadRequest(new { error = result.Error });

        return File(
            Encoding.UTF8.GetBytes(result.Value!),
            "text/csv",
            $"contacts-{pharmacyId}.csv");
    }
}
