using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Export;
using Acme.Application.Messaging;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Acme.WebApi.Auditing;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/posters/{id}")]
[Authorize]
public sealed class PosterExportController(
    IPosterRepository posters,
    IUserRepository users,
    IPosterPdfExporter pdfExporter,
    IBrandKitRepository brandKits,
    // Encodes the booking QR. Both exports below resolve the canvas themselves
    // rather than going through PosterImageService, so each has to be handed
    // this — a resolve that forgets it prints a gap where the code belongs.
    IQrCodeGenerator qrCodes,
    IEmailSender email,
    Acme.Application.Audit.IAuditScope audit) : ControllerBase
{
    public const int MaxShareRecipients = 10;
    public const int MaxShareSubject = 150;
    public const int MaxShareMessage = 1000;

    /// <summary>A web address in free text — the thing a phishing message needs.</summary>
    private static readonly System.Text.RegularExpressions.Regex LinkPattern = new(
        @"https?://|www\.|\b[a-z0-9-]+\.(com|net|org|co|uk|io|info|biz|xyz|ru|top|link|click)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    [HttpGet("export.pdf")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportPdf(string id, CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var poster = await posters.FindByIdAsync(id, ct);
        if (poster is null || poster.OwnerId != userId) return NotFound();

        var user = await users.FindByIdAsync(userId, ct);
        var watermark = user is null ? true : Plans.For(user.Tier).Watermark;

        // The logo is stored as a stable path, not as bytes and not as a signed
        // URL. Swapping it for the owner's blob key here lets the exporter read
        // storage directly instead of making an HTTP call back to this API over
        // a URL that came out of a saved document.
        var kit = await brandKits.FindByOwnerAsync(userId, ct);
        var canvasJson = BrandCanvasAssets.ResolveForRender(
            poster.CanvasJson, kit, user?.Profile, qrCodes);

        var pdf = await pdfExporter.RenderAsync(
            new PdfExportRequest(canvasJson, poster.Name, watermark), ct);

        return File(pdf, "application/pdf", FileName(poster.Name) + ".pdf");
    }

    /// <summary>
    /// The poster as a PNG.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Goes through <see cref="PosterImageService"/> rather than calling the
    /// rasteriser directly, so a download and a social publish share one cache
    /// and one watermark decision. Downloading a poster then publishing it
    /// should not render it twice, and must never produce two different images.
    /// </para>
    /// <para>
    /// Cached by content hash on the way in, so this is cheap on repeat calls
    /// and correct after an edit.
    /// </para>
    /// </remarks>
    [HttpGet("export.png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportPng(
        string id,
        [FromServices] PosterImageService images,
        [FromServices] IPosterImageStore store,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var rendered = await images.EnsureRenderedAsync(id, userId, ct);

        if (rendered.Kind != ResultKind.Success)
            return rendered.Kind == ResultKind.NotFound
                ? NotFound(new { error = rendered.Error })
                : BadRequest(new { error = rendered.Error });

        // Streamed from blob rather than re-rendered — EnsureRenderedAsync has
        // already put the bytes there, and reading them back is far cheaper
        // than a second rasterisation.
        var poster = await posters.FindByIdAsync(id, ct);
        await using var stream = await OpenAsync(store, rendered.Value!.BlobKey, ct);

        if (stream is null)
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "The rendered image could not be read back from storage." });

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        return File(buffer.ToArray(), "image/png",
            FileName(poster?.Name ?? rendered.Value.Name) + ".png");
    }

    /// <summary>
    /// The poster's card on My Posters: the same renderer as the PDF and PNG,
    /// at card size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Served through the app rather than as a signed storage URL, so the
    /// browser never needs to reach storage (in Docker it cannot) and a list
    /// left open does not turn into broken images when a signature expires.
    /// </para>
    /// <para>
    /// <c>no-cache</c> with a strong ETag: the browser keeps the bytes but asks
    /// every time, and the answer is a 304 computed from the canvas without
    /// reading storage. A new logo or a changed phone number therefore shows
    /// on the next visit, which a max-age could not promise.
    /// </para>
    /// <para>
    /// A render failure is a 422, not a 500. The page draws its own preview
    /// for any card that does not load, so a bad poster costs its own card and
    /// nothing else.
    /// </para>
    /// </remarks>
    [HttpGet("card")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Card(
        string id,
        [FromServices] PosterImageService images,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var result = await images.EnsureCardAsync(id, userId, Request.Headers.IfNoneMatch.ToString(), ct);
        if (result.Kind == ResultKind.NotFound) return NotFound(new { error = result.Error });
        if (result.Kind != ResultKind.Success)
            return UnprocessableEntity(new { error = result.Error });

        var card = result.Value!;
        Response.Headers.ETag = card.ETag;
        Response.Headers.CacheControl = "private, no-cache";

        return card.NotModified || card.Bytes is null
            ? StatusCode(StatusCodes.Status304NotModified)
            : File(card.Bytes, card.ContentType);
    }

    /// <summary>Opens a stored render, or null when storage refuses.</summary>
    private static async Task<Stream?> OpenAsync(
        IPosterImageStore store, string blobKey, CancellationToken ct)
    {
        try { return await store.OpenReadAsync(blobKey, ct); }
        catch (Exception) { return null; }
    }

    [Audit("poster.share_email", "poster")]
    [EnableRateLimiting(RateLimits.ShareEmail)]
    [HttpPost("share-email")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ShareByEmail(
        string id,
        [FromBody] ShareEmailRequest request,
        CancellationToken ct)
    {
        if (CurrentUserId() is not { } userId) return Unauthorized();

        var poster = await posters.FindByIdAsync(id, ct);
        if (poster is null || poster.OwnerId != userId) return NotFound();

        var recipients = request.Recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Where(r => EmailRegex.IsMatch(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // How many, never who: the addresses stay out of the audit log.
        audit.AddMeta("recipientCount", recipients.Count);

        if (recipients.Count == 0)
            return BadRequest(new { error = "At least one valid email is required." });

        // Sharing is for sending a poster to a handful of colleagues or
        // partners, not a mailing list — campaigns exist for that, with their
        // plan limits and unsubscribe handling. Without these caps any new
        // account could send unlimited mail with its own subject and message
        // from our domain (security review SEC-06). The endpoint is also
        // rate-limited per person (RateLimits.ShareEmail).
        if (recipients.Count > MaxShareRecipients)
            return BadRequest(new { error = $"Share with up to {MaxShareRecipients} people at a time. To reach patients, send a campaign." });
        if ((request.Subject?.Length ?? 0) > MaxShareSubject)
            return BadRequest(new { error = $"Keep the subject under {MaxShareSubject} characters." });
        if ((request.Message?.Length ?? 0) > MaxShareMessage)
            return BadRequest(new { error = $"Keep the message under {MaxShareMessage} characters." });
        if (LinkPattern.IsMatch(request.Subject ?? "") || LinkPattern.IsMatch(request.Message ?? ""))
            return BadRequest(new { error = "Links can't be added to a shared poster's message. The poster's booking code links to your booking page." });

        var user = await users.FindByIdAsync(userId, ct);
        var watermark = user is null ? true : Plans.For(user.Tier).Watermark;

        // Resolved, like every other render. This path was passing the raw
        // canvas straight to the exporter, so a poster shared by email went out
        // with the brand logo unresolved — the one export nobody compared
        // against the download, because it arrives in somebody else's inbox.
        // Templates now carry {{address}} tokens as well, which would have
        // printed literally here.
        var kit = await brandKits.FindByOwnerAsync(userId, ct);
        var shareCanvas = BrandCanvasAssets.ResolveForRender(
            poster.CanvasJson, kit, user?.Profile, qrCodes);

        var pdf = await pdfExporter.RenderAsync(
            new PdfExportRequest(shareCanvas, poster.Name, watermark), ct);

        // See User.PharmacyDisplayName: the ?? chain this replaces produced an
        // empty sender name for any pharmacy that had not finished onboarding.
        var pharmacy = user?.PharmacyDisplayName ?? "Pharmacy";
        var result = await email.SendAsync(new EmailMessage(
            // Blank, so the configured — and provider-verified — sender is used.
            // A literal address here overrides it, and Mailjet rejects anything
            // sent from a domain not verified on the account.
            //
            // The pharmacy's name still travels as the display name, so the
            // recipient sees who it came from; only the envelope address has to
            // belong to us. Reply-to is the right place for the pharmacy's own
            // address if that is wanted later.
            FromAddress: string.Empty,
            FromName: pharmacy,
            To: recipients,
            Subject: string.IsNullOrWhiteSpace(request.Subject)
                ? $"{poster.Name} from {pharmacy}"
                : request.Subject!,
            HtmlBody: $"""
                <p>Hi,</p>
                <p>{System.Net.WebUtility.HtmlEncode(request.Message ?? "Please find our latest poster attached.")}</p>
                <p>— {System.Net.WebUtility.HtmlEncode(pharmacy)}</p>
                """,
            TextBody: null,
            Attachments: new[] { new EmailAttachment(FileName(poster.Name) + ".pdf", "application/pdf", pdf) }),
            ct);

        return Ok(new { sent = result.Sent, failed = result.Failed, note = result.Note });
    }

    private string? CurrentUserId() =>
        User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    private static string FileName(string name) =>
        new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
}

public sealed record ShareEmailRequest(
    IReadOnlyList<string> Recipients,
    string? Subject,
    string? Message);
