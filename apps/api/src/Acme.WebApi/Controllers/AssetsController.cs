using Acme.Application.Templates;
using Acme.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Serves images embedded in poster templates.
/// </summary>
/// <remarks>
/// Anonymous by design, and consistent with the rest of the template surface —
/// GET /api/templates is already [AllowAnonymous] so the gallery and landing
/// page can render without a session. These images are decoration on
/// deliberately public marketing designs; there is nothing tenant-specific in
/// them.
///
/// The URLs are unguessable (a 128-bit GUID) rather than access-controlled,
/// which is the right trade for content that has to be fetchable by an
/// anonymous gallery visitor and by the PDF exporter alike.
/// </remarks>
[ApiController]
[Route("api/assets/templates")]
[AllowAnonymous]
public sealed class AssetsController(ITemplateAssetService assets) : ControllerBase
{
    [HttpGet("{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string fileName, CancellationToken ct)
    {
        var stream = await assets.OpenAsync(fileName, ct);
        if (stream is null) return NotFound();

        // Content is immutable — the file name is a GUID minted at upload and a
        // replacement gets a new name — so it can be cached hard. This matters
        // more than it looks: the gallery renders every template's canvas, so a
        // browsing admin may pull dozens of these in one page load.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return File(stream, TemplateAssetUrls.ContentTypeFor(fileName));
    }
}
