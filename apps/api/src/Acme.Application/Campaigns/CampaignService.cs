using System.Text.RegularExpressions;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Export;
using Acme.Application.Messaging;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Acme.Domain.Campaigns;
using Acme.Domain.Common;
using Acme.Domain.Patients;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Campaigns;

public sealed class CampaignService(
    ICampaignRepository campaigns,
    IUserRepository users,
    IPosterRepository posters,
    IPatientRepository patients,
    IPatientGroupRepository patientGroups,
    IEmailSender email,
    ISmsSender sms,
    IPosterPdfExporter pdf,
    ILogger<CampaignService> logger)
{
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<CampaignDto>> ListAsync(string ownerId, CancellationToken ct)
    {
        var items = await campaigns.ListByOwnerAsync(ownerId, ct);
        logger.LogInformation("Campaigns list for owner={OwnerId} returned {Count}", ownerId, items.Count);
        return items.Select(ToDto).ToList();
    }

    public async Task<Result<CampaignDto>> CreateAndSendAsync(
        string ownerId,
        CreateCampaignRequest request,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<CampaignDto>.NotFound("User not found.");

        var poster = await posters.FindByIdAsync(request.PosterId, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Poster not found.");

        // Expand patient/group sources, then validate recipients per channel.
        var manual = request.Recipients ?? Array.Empty<string>();
        var resolved = await ResolveAudienceContactsAsync(
            ownerId,
            request.Channel,
            request.PatientIds,
            request.GroupIds,
            ct);

        var recipients = NormalizeRecipients(
            manual.Concat(resolved).ToList(),
            request.Channel,
            out var rejectedReason,
            dropInvalid: resolved.Count > 0);
        if (rejectedReason is not null)
            return Result<CampaignDto>.Invalid(rejectedReason);
        if (recipients.Count == 0)
            return Result<CampaignDto>.Invalid("No valid recipients.");

        // Tier limit
        var plan = Plans.For(user.Tier);
        if (plan.MaxCampaignRecipientsPerMonth != Plans.Unlimited)
        {
            if (plan.MaxCampaignRecipientsPerMonth == 0)
                return Result<CampaignDto>.Forbidden("Campaigns are not available on the Free plan. Upgrade to send.");

            var alreadySent = await campaigns.CountRecipientsThisMonthAsync(ownerId, ct);
            if (alreadySent + recipients.Count > plan.MaxCampaignRecipientsPerMonth)
                return Result<CampaignDto>.Forbidden(
                    $"This send would exceed your {plan.DisplayName} plan limit of {plan.MaxCampaignRecipientsPerMonth} recipients/month. Used: {alreadySent}.");
        }

        Campaign campaign;
        try
        {
            campaign = Campaign.Create(ownerId, poster.Id, request.Name, request.Channel, recipients);
        }
        catch (DomainException ex) { return Result<CampaignDto>.Invalid(ex.Message); }

        campaign.MarkSending();
        await campaigns.AddAsync(campaign, ct);
        logger.LogInformation(
            "Campaign created id={CampaignId} owner={OwnerId} name='{Name}' recipients={Count}",
            campaign.Id, campaign.OwnerId, campaign.Name, recipients.Count);

        await DispatchAndPersistAsync(campaign, user, poster, recipients, request.Channel, ct);
        return Result<CampaignDto>.Success(ToDto(campaign));
    }

    /// <summary>Retry a previously failed (or partially failed) campaign.</summary>
    public async Task<Result<CampaignDto>> RetryAsync(string id, string ownerId, CancellationToken ct)
    {
        var campaign = await campaigns.FindByIdAsync(id, ct);
        if (campaign is null || campaign.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Campaign not found.");
        if (campaign.Status != CampaignStatus.Failed)
            return Result<CampaignDto>.Invalid("Only failed campaigns can be retried.");

        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<CampaignDto>.NotFound("User not found.");

        var poster = await posters.FindByIdAsync(campaign.PosterId, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Poster no longer available.");

        // Re-check the monthly quota — a retry is a fresh send from the plan's POV.
        var plan = Plans.For(user.Tier);
        if (plan.MaxCampaignRecipientsPerMonth != Plans.Unlimited)
        {
            var alreadySent = await campaigns.CountRecipientsThisMonthAsync(ownerId, ct);
            if (alreadySent + campaign.Recipients.Count > plan.MaxCampaignRecipientsPerMonth)
                return Result<CampaignDto>.Forbidden(
                    $"Retrying would exceed your {plan.DisplayName} plan limit of {plan.MaxCampaignRecipientsPerMonth} recipients/month.");
        }

        campaign.MarkSending();
        await campaigns.UpdateAsync(campaign, ct);

        await DispatchAndPersistAsync(campaign, user, poster, campaign.Recipients, campaign.Channel, ct);
        return Result<CampaignDto>.Success(ToDto(campaign));
    }

    private async Task DispatchAndPersistAsync(
        Campaign campaign,
        Domain.Users.User user,
        Domain.Posters.Poster poster,
        IReadOnlyList<string> recipients,
        CampaignChannel channel,
        CancellationToken ct)
    {
        try
        {
            EmailDispatchResult? emailResult = null;
            SmsDispatchResult? smsResult = null;

            if (channel == CampaignChannel.Email)
            {
                var watermark = Plans.For(user.Tier).Watermark;
                var pdfBytes = await pdf.RenderAsync(
                    new PdfExportRequest(poster.CanvasJson, poster.Name, watermark), ct);

                var pharmacyName = user.Profile.PharmacyName ?? user.DisplayName;
                emailResult = await email.SendAsync(new EmailMessage(
                    // Let the sender infrastructure decide (usually the Mailjet
                    // verified sender). Passing an unverified address here just
                    // makes Mailjet silently drop the message.
                    FromAddress: string.Empty,
                    FromName: user.Profile.PharmacyName ?? "Pharmacy",
                    To: recipients,
                    // Campaign name is the primary identifier the user chose; keep
                    // it in the subject line so it's visible in the inbox preview.
                    Subject: $"{campaign.Name} · {pharmacyName}",
                    HtmlBody: BuildEmailHtml(campaign.Name, poster.Name, pharmacyName),
                    TextBody: BuildEmailText(campaign.Name, poster.Name, pharmacyName),
                    Attachments: new[] { new EmailAttachment($"{Slug(poster.Name)}.pdf", "application/pdf", pdfBytes) }),
                    ct);
            }
            else
            {
                smsResult = await sms.SendAsync(new SmsMessage(
                    FromSenderId: (user.Profile.PharmacyName ?? "PHARMACY").Substring(0, Math.Min(11, (user.Profile.PharmacyName ?? "PHARMACY").Length)),
                    To: recipients,
                    Body: $"{campaign.Name} — {user.Profile.PharmacyName ?? user.DisplayName}. {poster.Name}. Visit us for details."),
                    ct);
            }

            var sent = emailResult?.Sent ?? smsResult?.Sent ?? 0;
            var failed = emailResult?.Failed ?? smsResult?.Failed ?? 0;
            var note = emailResult?.Note ?? smsResult?.Note;

            // Nothing landed → real failure. Some landed → mark Sent but keep the note
            // so the UI can surface partial-failure info.
            if (sent == 0 && failed > 0)
            {
                campaign.MarkFailed(note ?? $"All {failed} recipient(s) failed to send.");
                logger.LogWarning(
                    "Campaign {CampaignId} failed: sent=0 failed={Failed} note={Note}",
                    campaign.Id, failed, note);
            }
            else
            {
                var combinedNote = failed > 0
                    ? $"{failed} recipient(s) failed. {note}".Trim()
                    : note;
                campaign.MarkSent(sent, combinedNote);
                logger.LogInformation(
                    "Campaign {CampaignId} dispatched: sent={Sent} failed={Failed}",
                    campaign.Id, sent, failed);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Campaign {CampaignId} threw during dispatch", campaign.Id);
            campaign.MarkFailed(ex.Message);
        }

        await campaigns.UpdateAsync(campaign, ct);
    }

    public async Task<Result<CampaignDto>> StopAsync(string id, string ownerId, CancellationToken ct)
    {
        var campaign = await campaigns.FindByIdAsync(id, ct);
        if (campaign is null || campaign.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Campaign not found.");

        try { campaign.Stop(); }
        catch (DomainException ex) { return Result<CampaignDto>.Invalid(ex.Message); }

        await campaigns.UpdateAsync(campaign, ct);
        return Result<CampaignDto>.Success(ToDto(campaign));
    }

    private static IReadOnlyList<string> NormalizeRecipients(
        IReadOnlyList<string> input,
        CampaignChannel channel,
        out string? rejectedReason,
        bool dropInvalid = false)
    {
        var trimmed = input
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (channel == CampaignChannel.Email)
        {
            if (dropInvalid)
            {
                trimmed = trimmed.Where(r => EmailRegex.IsMatch(r)).ToList();
            }
            else
            {
                var bad = trimmed.Where(r => !EmailRegex.IsMatch(r)).Take(3).ToList();
                if (bad.Count > 0)
                {
                    rejectedReason = $"Invalid email(s): {string.Join(", ", bad)}";
                    return Array.Empty<string>();
                }
            }
        }
        else
        {
            // Very light phone normalisation — strip whitespace, keep +/digits.
            trimmed = trimmed
                .Select(r => new string(r.Where(c => c == '+' || char.IsDigit(c)).ToArray()))
                .Where(r => r.Length >= 7)
                .ToList();
        }

        rejectedReason = null;
        return trimmed;
    }

    private async Task<List<string>> ResolveAudienceContactsAsync(
        string ownerId,
        CampaignChannel channel,
        IReadOnlyList<string>? patientIds,
        IReadOnlyList<string>? groupIds,
        CancellationToken ct)
    {
        var contacts = new List<string>();

        if (patientIds is { Count: > 0 })
        {
            var picked = await patients.FindManyByIdsAsync(ownerId, patientIds, ct);
            contacts.AddRange(picked.Select(p => SelectContact(p, channel)).Where(c => c is not null)!);
        }

        if (groupIds is { Count: > 0 })
        {
            foreach (var gid in groupIds.Distinct())
            {
                var group = await patientGroups.FindByIdAsync(gid, ct);
                if (group is null || group.OwnerId != ownerId) continue;
                var members = await patients.ListByGroupAsync(ownerId, gid, ct);
                contacts.AddRange(members.Select(p => SelectContact(p, channel)).Where(c => c is not null)!);
            }
        }

        return contacts;
    }

    private static string? SelectContact(Patient p, CampaignChannel channel) =>
        channel == CampaignChannel.Email ? p.Email : p.Phone;

    public async Task<Result<CampaignAudiencePreview>> PreviewAudienceAsync(
        string ownerId,
        CampaignAudienceRequest request,
        CancellationToken ct)
    {
        var manual = request.Recipients ?? Array.Empty<string>();
        var resolved = await ResolveAudienceContactsAsync(
            ownerId, request.Channel, request.PatientIds, request.GroupIds, ct);

        var manualNormalized = NormalizeRecipients(manual, request.Channel, out _, dropInvalid: false);
        var resolvedNormalized = NormalizeRecipients(resolved, request.Channel, out _, dropInvalid: true);

        var combined = manualNormalized
            .Concat(resolvedNormalized)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var invalidFromResolved = resolved.Count - resolvedNormalized.Count;
        var sample = combined.Take(5).ToList();

        return Result<CampaignAudiencePreview>.Success(new CampaignAudiencePreview(
            TotalUnique: combined.Count,
            FromManual: manualNormalized.Count,
            FromPatients: request.PatientIds?.Count ?? 0,
            FromGroups: request.GroupIds?.Count ?? 0,
            InvalidCount: invalidFromResolved,
            SampleRecipients: sample));
    }

    private static string BuildEmailHtml(string campaignName, string posterName, string pharmacy)
    {
        var encCampaign = System.Net.WebUtility.HtmlEncode(campaignName);
        var encPoster = System.Net.WebUtility.HtmlEncode(posterName);
        var encPharmacy = System.Net.WebUtility.HtmlEncode(pharmacy);

        return $"""
            <!doctype html>
            <html>
              <body style="font-family:Inter,system-ui,sans-serif;background:#f8fafc;padding:24px;margin:0;">
                <div style="max-width:560px;margin:0 auto;background:#fff;border-radius:12px;padding:32px;border:1px solid #e2e8f0;">
                  <p style="margin:0 0 6px;color:#0e7490;font-size:12px;font-weight:600;letter-spacing:0.08em;text-transform:uppercase;">
                    Campaign
                  </p>
                  <h1 style="margin:0 0 24px;color:#0f172a;font-size:26px;line-height:1.2;">
                    {encCampaign}
                  </h1>
                  <p style="margin:0 0 12px;color:#475569;font-size:15px;">
                    A new poster from <strong>{encPharmacy}</strong> is attached as a PDF.
                  </p>
                  <p style="margin:0 0 24px;color:#475569;font-size:15px;">
                    Poster: <strong>{encPoster}</strong>
                  </p>
                  <hr style="border:none;border-top:1px solid #e2e8f0;margin:24px 0;">
                  <p style="margin:0;color:#94a3b8;font-size:12px;">
                    Sent via Sonar Marketing25.
                  </p>
                </div>
              </body>
            </html>
            """;
    }

    private static string BuildEmailText(string campaignName, string posterName, string pharmacy) =>
        $"""
        {campaignName}

        A new poster from {pharmacy} is attached as a PDF.
        Poster: {posterName}

        Sent via Sonar Marketing25.
        """;

    private static string Slug(string s)
    {
        var lower = s.ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (var c in lower)
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }

    private static CampaignDto ToDto(Campaign c) => new(
        c.Id,
        c.OwnerId,
        c.PosterId,
        c.Name,
        c.Channel,
        c.Status,
        c.Recipients.Count,
        c.SentCount,
        c.SentAtUtc,
        c.Note,
        c.CreatedAtUtc);
}
