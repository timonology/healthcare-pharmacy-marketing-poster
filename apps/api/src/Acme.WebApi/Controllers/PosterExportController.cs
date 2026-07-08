using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Acme.Application.Auth;
using Acme.Application.Export;
using Acme.Application.Messaging;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Acme.WebApi.Controllers;

[ApiController]
[Route("api/posters/{id}")]
[Authorize]
public sealed class PosterExportController(
    IPosterRepository posters,
    IUserRepository users,
    IPosterPdfExporter pdfExporter,
    IEmailSender email) : ControllerBase
{
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

        var pdf = await pdfExporter.RenderAsync(
            new PdfExportRequest(poster.CanvasJson, poster.Name, watermark), ct);

        return File(pdf, "application/pdf", FileName(poster.Name) + ".pdf");
    }

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

        if (recipients.Count == 0)
            return BadRequest(new { error = "At least one valid email is required." });

        var user = await users.FindByIdAsync(userId, ct);
        var watermark = user is null ? true : Plans.For(user.Tier).Watermark;
        var pdf = await pdfExporter.RenderAsync(
            new PdfExportRequest(poster.CanvasJson, poster.Name, watermark), ct);

        var pharmacy = user?.Profile.PharmacyName ?? user?.DisplayName ?? "Pharmacy";
        var result = await email.SendAsync(new EmailMessage(
            FromAddress: "no-reply@pharmacyposter.app",
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
