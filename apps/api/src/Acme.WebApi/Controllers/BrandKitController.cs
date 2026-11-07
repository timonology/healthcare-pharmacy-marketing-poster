using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/brand-kit")]
[Authorize]
public sealed class BrandKitController(BrandKitService service) : ControllerBase
{
    private const long MaxLogoBytes = 5 * 1024 * 1024; // 5 MB

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/svg+xml",
        "image/webp",
    };

    /// <summary>
    /// The pharmacy's logo, as bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the path a canvas stores. It never expires and carries no
    /// signature, which is what lets a poster saved today still show its logo in
    /// a year — the signed URL on the DTO above lasts fifteen minutes and is for
    /// display on a page that is open right now.
    /// </para>
    /// <para>
    /// Authorised, and scoped to the caller: the logo is resolved from the
    /// caller's own brand kit rather than an id in the path, so there is no id
    /// to tamper with and one pharmacy cannot request another's mark.
    /// </para>
    /// </remarks>
    [HttpGet("logo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Logo(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var logo = await service.OpenLogoAsync(userId, ct);
        if (logo is null) return NotFound();

        // Private, and briefly. The bytes belong to one pharmacy, so a shared
        // cache must not hold them; but the editor redraws constantly, and
        // re-fetching the same logo on every frame is a lot of requests for an
        // image that changes when somebody uploads a new one.
        Response.Headers.CacheControl = "private, max-age=300";

        return File(logo.Value.Content, logo.Value.ContentType);
    }

    /// <summary>
    /// A QR code for the pharmacy's booking link, as a PNG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The editor's counterpart to the render-time encoding. A canvas stores the
    /// path, and each renderer resolves it: the exporter encodes the link and
    /// inlines it, and the editor fetches this so the designer sees the real
    /// code while they are placing it.
    /// </para>
    /// <para>
    /// Authorised and scoped to the caller for the same reason the logo route is
    /// — the link is resolved from the caller's own profile rather than an id in
    /// the path, so there is nothing to tamper with. It is not sensitive, but a
    /// route that took an id would be one more thing to get right.
    /// </para>
    /// <para>
    /// <b>404 when no booking link is set</b>, rather than a code that scans to
    /// nothing. A patient cannot tell a dead QR from a working one until they
    /// have already pointed a phone at it, so the honest failure is no image.
    /// </para>
    /// </remarks>
    [HttpGet("booking-qr")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BookingQr(
        [FromServices] IBrandKitRepository brandKits,
        [FromServices] IQrCodeGenerator qrCodes,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        // The brand kit, not the profile. This is where the link lives now, and
        // reading it from anywhere else would be a second resolution path for a
        // value that ends up printed on a wall.
        var kit = await brandKits.FindByOwnerAsync(userId, ct);
        var bookingUrl = kit?.Pharmacy.BookingUrl;
        if (string.IsNullOrWhiteSpace(bookingUrl))
            return NotFound(new { error = "No booking link is set on your brand kit yet." });

        var png = qrCodes.ToPng(bookingUrl);
        if (png is null) return NotFound(new { error = "That booking link could not be encoded." });

        // Private and brief, like the logo: the editor redraws constantly, and
        // the code changes only when somebody edits their booking link.
        Response.Headers.CacheControl = "private, max-age=300";

        return File(png, "image/png");
    }

    /// <summary>Get the current user's brand kit.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(BrandKitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.GetForOwnerAsync(userId, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Create or update the current user's brand kit.</summary>
    [Audit("brand.update", "brand_kit")]
    [HttpPut]
    [ProducesResponseType(typeof(BrandKitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upsert(
        [FromBody] UpsertBrandKitRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await service.UpsertAsync(userId, request, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.Invalid => BadRequest(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    /// <summary>Upload a new logo image. Multipart form-data, field name "file".</summary>
    [Audit("brand.logo.upload", "brand_kit")]
    [HttpPost("logo")]
    [RequestSizeLimit(MaxLogoBytes)]
    [ProducesResponseType(typeof(BrandKitDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UploadLogo(IFormFile file, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "File is required." });

        if (file.Length > MaxLogoBytes)
            return BadRequest(new { error = $"File exceeds {MaxLogoBytes / (1024 * 1024)}MB limit." });

        if (!AllowedContentTypes.Contains(file.ContentType))
            return BadRequest(new { error = $"Unsupported content type: {file.ContentType}" });

        var ext = Path.GetExtension(file.FileName);
        await using var stream = file.OpenReadStream();

        var result = await service.UploadLogoAsync(userId, stream, file.ContentType, ext, ct);
        return result.Kind switch
        {
            ResultKind.Success => Ok(result.Value),
            ResultKind.NotFound => NotFound(new { error = result.Error }),
            _ => BadRequest(new { error = result.Error }),
        };
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
}
