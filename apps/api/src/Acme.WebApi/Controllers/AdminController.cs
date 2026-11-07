using Acme.Application.Admin;
using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Export;
using Acme.Application.Posters;
using Acme.Application.Templates;
using Acme.Domain.Admin;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Admin API.
///
/// Two layers of protection, and both are necessary:
///
///   1. The class-level policy keeps non-staff out entirely.
///   2. AdminService re-derives what this specific caller may see, because
///      "Admin" is not a uniform permission — each one has their own portfolio.
///
/// The <see cref="AdminContext"/> passed to the service is built here from
/// validated JWT claims only. Nothing about the caller's identity or role is
/// ever read from the request body or query string.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = AuthPolicies.AdminArea)]
public sealed class AdminController(
    AdminService admin,
    ITemplateAssetService assets) : AdminApiController
{
    /* ---------------- Dashboard ---------------- */

    [ActivityLog("admin.console.dashboard")]
    [HttpGet("stats")]
    [ProducesResponseType(typeof(AdminStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.GetStatsAsync(actor, ct));
    }

    /// <summary>Platform-wide activity over a rolling window of days.</summary>
    [HttpGet("analytics")]
    [ProducesResponseType(typeof(AdminAnalyticsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Analytics(
        [FromQuery] int days = 30, CancellationToken ct = default)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.GetAnalyticsAsync(actor, days, ct));
    }

    [HttpGet("audit")]
    [ProducesResponseType(typeof(AuditListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Audit(
        [FromQuery] AdminAction? action,
        [FromQuery] string? search,
        [FromQuery] int? days,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (Context() is not { } actor) return Unauthorized();

        var query = new AuditQuery(
            Action: action,
            Search: search,
            FromUtc: days is { } d and > 0 ? DateTime.UtcNow.AddDays(-d) : null,
            Skip: skip,
            Take: take);

        return Respond(await admin.ListAuditAsync(actor, query, ct));
    }

    /* ---------------- Users ---------------- */

    [ActivityLog("admin.console.users")]
    [HttpGet("users")]
    [ProducesResponseType(typeof(AdminListDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Users(
        [FromQuery] string? search,
        [FromQuery] UserRole? role,
        [FromQuery] bool? isActive,
        [FromQuery] SubscriptionTier? tier,
        [FromQuery] string? managedBy,
        [FromQuery] bool? onboarded,
        [FromQuery] string? sort,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] bool unassignedOnly = false,
        [FromQuery] bool pharmacyAccounts = false,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 25,
        CancellationToken ct = default)
    {
        if (Context() is not { } actor) return Unauthorized();

        // Account created date. The shared words (newest|oldest) and the older
        // enum names (NewestFirst, NameAsc, …) are both accepted, so a saved
        // link from before the shared toolbar still sorts the way it did.
        var window = ListWindow.From(sort, from, to);

        var query = new UserQuery(
            Search: search,
            Role: role,
            IsActive: isActive,
            Tier: tier,
            ManagedByAdminId: managedBy,
            UnassignedOnly: unassignedOnly,
            Onboarded: onboarded,
            Sort: UserSortFrom(sort),
            Skip: skip,
            Take: take,
            PharmacyAccountsOnly: pharmacyAccounts,
            CreatedFromUtc: window.FromUtc,
            CreatedToUtc: window.ToUtc);

        return Respond(await admin.ListUsersAsync(actor, query, ct));
    }

    // Named GetUser, not User: an action called `User` shadows
    // ControllerBase.User (the ClaimsPrincipal), which Context() reads the
    // caller's role from. The route is unchanged.
    [ActivityLog("admin.user.view", "user", PharmacyRoute = "id")]
    [HttpGet("users/{id}")]
    [ProducesResponseType(typeof(AdminUserDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUser(string id, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.GetUserAsync(actor, id, ct));
    }

    [Audit("admin.user.deactivate", "user", PharmacyRoute = "id")]
    [HttpPost("users/{id}/deactivate")]
    public async Task<IActionResult> Deactivate(string id, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.SetUserActiveAsync(actor, id, false, ct));
    }

    [Audit("admin.user.reactivate", "user", PharmacyRoute = "id")]
    [HttpPost("users/{id}/reactivate")]
    public async Task<IActionResult> Reactivate(string id, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.SetUserActiveAsync(actor, id, true, ct));
    }

    [Audit("admin.user.assign", "user", PharmacyRoute = "id")]
    [HttpPost("users/{id}/assign")]
    public async Task<IActionResult> Assign(
        string id, [FromBody] AssignUserRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.AssignUserAsync(actor, id, req.AdminId, ct));
    }

    [Audit("admin.user.unassign", "user", PharmacyRoute = "id")]
    [HttpPost("users/{id}/unassign")]
    public async Task<IActionResult> Unassign(string id, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.UnassignUserAsync(actor, id, ct));
    }

    [Audit("admin.subscription.override", "user", PharmacyRoute = "id")]
    [HttpPost("users/{id}/tier")]
    public async Task<IActionResult> OverrideTier(
        string id, [FromBody] OverrideTierRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.OverrideTierAsync(actor, id, req.Tier, req.Reason, ct));
    }

    [Audit("admin.credits.grant", "user", PharmacyRoute = "id")]
    [HttpPost("users/{id}/credits")]
    public async Task<IActionResult> GrantCredits(
        string id, [FromBody] GrantCreditsRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.GrantCreditsAsync(actor, id, req.Credits, req.Reason, ct));
    }

    /// <summary>
    /// Grants or withdraws the ability to run branches.
    /// </summary>
    /// <remarks>
    /// Audited, like every other capability change here. Withdrawing does not
    /// detach existing branches; it only stops new ones being created.
    /// </remarks>
    [Audit("admin.head_office.set", "user", PharmacyRoute = "id")]
    [HttpPost("users/{id}/head-office")]
    public async Task<IActionResult> SetHeadOffice(
        string id, [FromBody] SetHeadOfficeRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.SetHeadOfficeAsync(actor, id, req.Granted, req.Reason, ct));
    }

    /* ---------------- Administrators — SuperAdmin only ---------------- */

    [ActivityLog("admin.console.admins")]
    [HttpGet("admins")]
    [Authorize(Policy = AuthPolicies.SuperAdminOnly)]
    [ProducesResponseType(typeof(IReadOnlyList<AdminSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Admins(CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.ListAdminsAsync(actor, ct));
    }

    [Audit("admin.admin.create", "user")]
    [HttpPost("admins")]
    [Authorize(Policy = AuthPolicies.SuperAdminOnly)]
    [ProducesResponseType(typeof(AdminSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAdmin(
        [FromBody] CreateAdminRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.CreateAdminAsync(actor, req, ct));
    }

    [Audit("admin.admin.update", "user")]
    [HttpPatch("admins/{id}")]
    [Authorize(Policy = AuthPolicies.SuperAdminOnly)]
    public async Task<IActionResult> UpdateAdmin(
        string id, [FromBody] UpdateAdminRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.UpdateAdminAsync(actor, id, req, ct));
    }

    [Audit("admin.user.role", "user")]
    [HttpPost("users/{id}/role")]
    [Authorize(Policy = AuthPolicies.SuperAdminOnly)]
    public async Task<IActionResult> ChangeRole(
        string id, [FromBody] ChangeRoleRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        return Respond(await admin.ChangeRoleAsync(actor, id, req.Role, ct));
    }

    /* ---------------- Template authoring ---------------- */

    /// <summary>
    /// Generate a poster template from a natural-language brief.
    /// </summary>
    /// <remarks>
    /// Returns a candidate design; it is not added to the gallery. Publishing
    /// is a separate, explicit call to POST /api/templates.
    /// </remarks>
    [Audit("admin.template.generate", "template")]
    [HttpPost("templates/generate")]
    [ProducesResponseType(typeof(GeneratedTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateTemplate(
        [FromBody] GenerateTemplateRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (BearerToken() is not { } token) return Unauthorized();

        return Respond(await admin.GenerateTemplateAsync(actor, req, token, ct));
    }

    /// <summary>
    /// Upload an image to use in a template. Multipart form-data, field "file".
    /// </summary>
    /// <remarks>
    /// Returns a stable application URL to drop straight into a canvas
    /// `blobKey`. Not a SAS URL — see ITemplateAssetService for why a template
    /// image must never expire.
    /// </remarks>
    [Audit("admin.template.asset_upload", "template_asset")]
    [HttpPost("templates/assets")]
    [RequestSizeLimit(MaxAssetBytes)]
    [ProducesResponseType(typeof(StoredTemplateAsset), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadTemplateAsset(IFormFile file, CancellationToken ct)
    {
        if (Context() is null) return Unauthorized();

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose an image to upload." });

        if (file.Length > MaxAssetBytes)
            return BadRequest(new { error = $"Image exceeds the {MaxAssetBytes / (1024 * 1024)}MB limit." });

        try
        {
            // The bytes decide what it is, not the browser's content type: a
            // renamed PDF must be refused here, not a minute into a design.
            using var buffer = new MemoryStream();
            await using (var upload = file.OpenReadStream())
                await upload.CopyToAsync(buffer, ct);
            if (SniffImage(buffer) is not { } contentType)
                return BadRequest(new { error = "That file isn't a JPEG, PNG or WebP image." });

            buffer.Position = 0;
            var stored = await assets.UploadAsync(buffer, contentType, string.Empty, ct);
            return Ok(stored);
        }
        catch (InvalidOperationException ex)
        {
            // Thrown for a rejected content type — a caller error, not a fault.
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Produce a photograph for a poster (AI if configured, otherwise stock).
    /// </summary>
    [Audit("admin.template.image_create", "template_asset")]
    [HttpPost("templates/images")]
    [ProducesResponseType(typeof(PosterImage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreatePosterImage(
        [FromBody] CreatePosterImageRequest req, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (BearerToken() is not { } token) return Unauthorized();

        return Respond(await admin.CreatePosterImageAsync(
            actor, req.Description, req.Aspect, req.Offset, token, ct, req.Provider));
    }

    /// <summary>
    /// Reports whether every image in a template can actually be loaded for
    /// print.
    /// </summary>
    /// <remarks>
    /// The PDF renderer skips images it cannot fetch so a poster still gets
    /// produced, which means a missing photograph is invisible until someone
    /// opens the email. This runs the renderer's own fetch and says what
    /// happened to each one.
    /// </remarks>
    [HttpGet("templates/{id}/image-check")]
    [ProducesResponseType(typeof(IReadOnlyList<ImageProbe>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckTemplateImages(
        string id, [FromServices] IPosterPdfExporter exporter,
        [FromServices] TemplateService templates, CancellationToken ct)
    {
        if (Context() is null) return Unauthorized();

        var template = await templates.GetAsync(id, ct);
        if (template.Kind == ResultKind.NotFound)
            return NotFound(new { error = template.Error });
        if (!template.IsSuccess)
            return BadRequest(new { error = template.Error });

        var probes = await exporter.ProbeImagesAsync(
            template.Value!.Canvas.GetRawText(), ct);

        return Ok(new
        {
            template = template.Value!.Name,
            images = probes.Count,
            failing = probes.Count(p => !p.Ok),
            probes,
        });
    }

    /// <summary>
    /// The same check, for a poster somebody actually sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The template version above has covered only the library. When a
    /// pharmacy reports that "the poster in the email is missing things", the
    /// poster is theirs, not a template, and there was no way to ask the
    /// renderer what happened to it short of reading API logs.
    /// </para>
    /// <para>
    /// <b>The brand logo is resolved first, exactly as the campaign does.</b>
    /// A canvas stores the logo as <c>/api/brand-kit/logo</c>, a path with no
    /// owner in it; <see cref="BrandCanvasAssets.ResolveForRender"/> swaps it
    /// for that pharmacy's blob key just before rendering. Probing the raw
    /// canvas would therefore report on a reference the renderer never fetches
    /// — failing on a path that is *supposed* to be unresolvable, while saying
    /// nothing about the blob that actually went missing. A diagnostic that
    /// answers a different question than the one asked is worse than none.
    /// </para>
    /// </remarks>
    [HttpGet("posters/{id}/image-check")]
    [ProducesResponseType(typeof(IReadOnlyList<ImageProbe>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CheckPosterImages(
        string id,
        [FromServices] IPosterPdfExporter exporter,
        [FromServices] IPosterRepository posters,
        [FromServices] IBrandKitRepository brandKits,
        [FromServices] IUserRepository users,
        [FromServices] IQrCodeGenerator qrCodes,
        CancellationToken ct)
    {
        if (Context() is null) return Unauthorized();

        var poster = await posters.FindByIdAsync(id, ct);
        if (poster is null) return NotFound(new { error = "Poster not found." });

        var kit = await brandKits.FindByOwnerAsync(poster.OwnerId, ct);

        // Kept as its own name because the report below reads better for it, and
        // because this is the value the probe list is matched against.
        var logoKey = kit?.LogoBlobKey;

        // The owner's profile and a real encoder, for the reason the logo is
        // resolved above: this has to probe what the renderer actually fetches.
        //
        // Resolving without them makes a booking QR vanish into an empty source
        // and drop out of the probe entirely — so a pharmacy reporting "the code
        // on my poster is missing" would get a clean report saying nothing about
        // it, which is the precise failure this endpoint exists to avoid.
        var owner = await users.FindByIdAsync(poster.OwnerId, ct);
        var canvasJson = BrandCanvasAssets.ResolveForRender(
            poster.CanvasJson, kit, owner?.Profile, qrCodes);

        var probes = await exporter.ProbeImagesAsync(canvasJson, ct);

        return Ok(new
        {
            poster = poster.Name,
            owner = poster.OwnerId,
            status = poster.Status.ToString(),
            // Named so the answer to "did the logo make it" is readable
            // without cross-referencing blob keys by eye.
            brandLogo = logoKey is null
                ? "the pharmacy has no logo uploaded"
                : probes.FirstOrDefault(p => p.BlobKey == logoKey) is { } logoProbe
                    ? logoProbe.Ok ? "loaded" : $"FAILED: {logoProbe.Error}"
                    : "the canvas does not reference the brand logo at all",

            // The booking code, reported separately because it cannot appear in
            // `probes` at all.
            //
            // The remarks above say this endpoint exists so that a pharmacy
            // reporting "the code on my poster is missing" does not get a clean
            // report that never mentions it. That was only half true: the QR
            // resolves to an inline data URI rather than a blob, so there is
            // nothing for the image probe to fetch and nothing about it in the
            // list — exactly the silence the endpoint is supposed to prevent,
            // for exactly the complaint it was written for.
            //
            // Derived from the inputs rather than re-encoding, so this line
            // cannot disagree with the render by doing its own work. The one
            // soft edge is the last branch: a poster carrying its own inline PNG
            // would satisfy the base64 test too. That only matters when a code
            // was asked for and a link exists, where encoding all but certainly
            // worked, and a wrong "encoded" there is a far cheaper mistake than
            // saying nothing.
            bookingCode =
                !poster.CanvasJson.Contains(
                    BrandCanvasAssets.BookingQrSrcPath, StringComparison.OrdinalIgnoreCase)
                    ? "this poster does not carry a booking code"
                    : string.IsNullOrWhiteSpace(kit?.Pharmacy.BookingUrl)
                        ? "no booking link is set on the brand kit, so the code is left out"
                        : canvasJson.Contains("data:image/png;base64,", StringComparison.Ordinal)
                            ? "encoded into the poster"
                            : "FAILED: a booking link is set but would not encode",

            images = probes.Count,
            failing = probes.Count(p => !p.Ok),
            probes,
        });
    }

    /// <summary>Search stock photography for a poster.</summary>
    [HttpGet("templates/stock-photos")]
    [ProducesResponseType(typeof(IReadOnlyList<StockPhoto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchStockPhotos(
        [FromQuery] string query,
        [FromQuery] string? aspect,
        CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (BearerToken() is not { } token) return Unauthorized();

        return Respond(await admin.SearchStockPhotosAsync(actor, query, aspect, token, ct));
    }

    /// <summary>
    /// Copy a chosen stock photo into our storage, ready to design around.
    /// </summary>
    [Audit("admin.template.stock_photo", "stock_photo", IdRoute = "photoId")]
    [HttpPost("templates/stock-photos/{photoId}")]
    [ProducesResponseType(typeof(TemplateImage), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UseStockPhoto(string photoId, CancellationToken ct)
    {
        if (Context() is not { } actor) return Unauthorized();
        if (BearerToken() is not { } token) return Unauthorized();

        return Respond(await admin.UseStockPhotoAsync(actor, photoId, token, ct));
    }

    /* ---------------- Plumbing ---------------- */

    /// <summary>
    /// <c>newest</c>/<c>oldest</c> from the shared toolbar, or one of the
    /// <see cref="UserSort"/> names this endpoint always accepted. Anything else
    /// is newest first rather than a 400: the value comes from a shareable URL.
    /// </summary>
    [NonAction]
    public static UserSort UserSortFrom(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return UserSort.NewestFirst;
        if (v.Equals("newest", StringComparison.OrdinalIgnoreCase)) return UserSort.NewestFirst;
        if (v.Equals("oldest", StringComparison.OrdinalIgnoreCase)) return UserSort.OldestFirst;
        return Enum.TryParse<UserSort>(v, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : UserSort.NewestFirst;
    }

    private static string? SniffImage(MemoryStream buffer) =>
        ImageSniffer.Detect(buffer.GetBuffer().AsSpan(
            0, (int)Math.Min(buffer.Length, ImageSniffer.HeaderLength)));

    /// <summary>
    /// 12MB. Generous enough for a phone photo, small enough that a careless
    /// upload can't stall the request pipeline. Images are downscaled in the
    /// browser before they get here, so this is a backstop rather than the
    /// normal case.
    /// </summary>
    private const long MaxAssetBytes = 12L * 1024 * 1024;

    /// <summary>
    /// The caller's raw access token, for forwarding to ai-service so it can
    /// independently verify the caller is staff.
    /// </summary>
    /// <remarks>
    /// Read from the Authorization header rather than reconstructed, because a
    /// re-minted token would detach the downstream call from this specific
    /// administrator. Returns null if the scheme isn't Bearer.
    /// </remarks>
    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(header)) return null;

        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }
}
