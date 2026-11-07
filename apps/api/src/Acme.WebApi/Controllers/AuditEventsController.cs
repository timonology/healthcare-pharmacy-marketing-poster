using System.Text;
using Acme.Application.Audit;
using Acme.Application.Common;
using Acme.WebApi.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

/// <summary>
/// The audit console: security audit, staff activity, pharmacy activity.
/// </summary>
/// <remarks>
/// Who may see which rows is decided by <see cref="AuditEventService"/> from
/// the caller's token — a SuperAdmin everything, an Admin their linked
/// pharmacies and their own actions. Nothing in the query string widens it.
/// </remarks>
[ApiController]
[Route("api/admin/audit-events")]
[Authorize(Policy = AuthPolicies.AdminArea)]
public sealed class AuditEventsController(
    AuditEventService events,
    IAuditScope audit) : AdminApiController
{
    [ActivityLog("admin.console.audit")]
    [HttpGet]
    [ProducesResponseType(typeof(AuditEventListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Search([FromQuery] AuditEventQueryParams q, CancellationToken ct)
    {
        if (Context() is not { } caller) return Unauthorized();
        return Respond(await events.SearchAsync(caller, q.ToSearch(), ct));
    }

    /// <summary>The current filters as CSV, at most 10,000 rows, newest first.</summary>
    /// <remarks>
    /// Audited: a bulk copy of who-did-what leaving the system is itself
    /// something a reviewer should be able to find.
    /// </remarks>
    [Audit("admin.audit.export")]
    [HttpGet("export.csv")]
    // No [Produces("text/csv")]: it would turn the JSON 403 body into a 406.
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Export([FromQuery] AuditEventQueryParams q, CancellationToken ct)
    {
        if (Context() is not { } caller) return Unauthorized();

        var search = q.ToSearch();
        audit.AddMeta("tab", search.Tab.ToString().ToLowerInvariant());

        var result = await events.ExportCsvAsync(caller, search, ct);
        if (result.Value is not { } csv) return Respond(result);

        audit.AddMeta("rowCount", csv.Rows);
        if (csv.Truncated) audit.AddMeta("truncated", "true");

        Response.Headers["X-Export-Truncated"] = csv.Truncated ? "true" : "false";
        // BOM so Excel opens UTF-8 names correctly.
        var preamble = Encoding.UTF8.GetPreamble();
        var bytes = new byte[preamble.Length + Encoding.UTF8.GetByteCount(csv.Content)];
        preamble.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(csv.Content, 0, csv.Content.Length, bytes, preamble.Length);
        return File(bytes, "text/csv; charset=utf-8", csv.FileName);
    }
}

/// <summary>Query string for the audit console.</summary>
public sealed class AuditEventQueryParams
{
    public AuditTab Tab { get; set; } = AuditTab.Security;
    public AuditStreamView Stream { get; set; } = AuditStreamView.Changes;
    public SecurityView View { get; set; } = SecurityView.Default;
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Actor { get; set; }
    public string? PharmacyId { get; set; }
    public string? Action { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 50;

    /// <summary><c>newest</c> (default) or <c>oldest</c>, by event time.</summary>
    public string? Sort { get; set; }

    public AuditEventSearch ToSearch() => new(
        Tab, Stream, View,
        ListWindow.Utc(From), ListWindow.Utc(To),
        Actor, PharmacyId, Action, EntityType, EntityId,
        Skip, Take,
        ListWindow.ParseSort(Sort));
}
