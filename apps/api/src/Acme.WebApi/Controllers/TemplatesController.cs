using Acme.Application.Common;
using Acme.Application.Templates;
using Acme.Domain.Templates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/templates")]
[Authorize]
public sealed class TemplatesController(TemplateService service) : ControllerBase
{
    /// <summary>
    /// Request ceiling for a canvas save. Above the service's own limit
    /// (CanvasLimitsOptions), so a pharmacist normally gets its plain message;
    /// this only stops a body large enough to be abuse from being read at all.
    /// </summary>
    private const long CanvasBodyLimit = 8 * 1024 * 1024;

    /// <summary>List published templates with optional category / search filtering.</summary>
    /// <remarks>Anonymous — the landing page and guest browse use this endpoint.</remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResponse<TemplateSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] TemplateCategory? category,
        [FromQuery] string? search,
        [FromQuery] string? sort,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        // Anonymous input: a negative skip or take must be a short page, not a 500.
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);
        // Created date. The house set still leads; sort and range apply within
        // it and within everything after it.
        var query = new TemplateQuery(category, search, IsPublished: true, skip, take,
            Window: ListWindow.From(sort, from, to));
        var items = await service.ListAsync(query, ct);
        var total = await service.CountAsync(query, ct);
        return Ok(new PagedResponse<TemplateSummaryDto>(items, total, skip, take));
    }

    /// <summary>Anonymous — used by the public template detail page.</summary>
    [ActivityLog("template.view", "template")]
    [HttpGet("{id}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var result = await service.GetAsync(id, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Create a template. Staff only.</summary>
    /// <remarks>
    /// Templates are shared platform-wide: every pharmacy sees the same gallery.
    /// Before this policy existed the class-level [Authorize] was the only gate,
    /// so any signed-in pharmacy user could create, rewrite or delete the
    /// designs every other customer depends on.
    /// </remarks>
    [Audit("admin.template.create", "template")]
    [HttpPost]
    [RequestSizeLimit(CanvasBodyLimit)] // R2-12: a clear refusal comes from the service below this
    [Authorize(Policy = AuthPolicies.AdminArea)]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(
        [FromBody] UpsertTemplateRequest request,
        CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.Kind switch
        {
            ResultKind.Success => CreatedAtAction(nameof(Get), new { id = result.Value!.Id }, result.Value),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Update a template. Staff only — see <see cref="Create"/>.</summary>
    [Audit("admin.template.update", "template")]
    [HttpPut("{id}")]
    [RequestSizeLimit(CanvasBodyLimit)] // R2-12: a clear refusal comes from the service below this
    [Authorize(Policy = AuthPolicies.AdminArea)]
    [ProducesResponseType(typeof(TemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] UpsertTemplateRequest request,
        CancellationToken ct)
    {
        var result = await service.UpdateAsync(id, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Delete a template. Staff only — see <see cref="Create"/>.</summary>
    [Audit("admin.template.delete", "template")]
    [HttpDelete("{id}")]
    [Authorize(Policy = AuthPolicies.AdminArea)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var result = await service.DeleteAsync(id, ct);
        return result.Kind switch
        {
            ResultKind.Success => NoContent(),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }
}

public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Skip,
    int Take);
