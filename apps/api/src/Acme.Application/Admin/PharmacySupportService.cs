using Acme.Application.Auth;
using Acme.Application.BrandKit;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Application.Videos;
using Acme.Domain.Admin;
using Acme.Domain.Campaigns;
using Acme.Domain.Patients;
using Acme.Domain.Posters;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Admin;

/// <summary>
/// The support view: an administrator looking at one pharmacy's workspace in
/// order to answer a question about it.
/// </summary>
/// <remarks>
/// <para>
/// This is separate from <see cref="AdminService"/> because it does a
/// different kind of thing. AdminService manages accounts — plans, roles,
/// assignment — and its subject is the user record. This service reads the
/// data a pharmacy has <i>created</i>: their patients, their posters, the
/// campaigns they sent. Those are the pharmacy's own working materials, which
/// we hold on their behalf, and the rules for touching them are not the same
/// as the rules for changing someone's plan.
/// </para>
/// <para>
/// Two rules follow from that, and both are enforced here rather than left to
/// the UI:
/// </para>
/// <list type="number">
///   <item>
///     <b>Reads do not mutate.</b> Every method below is a projection. The one
///     exception is <see cref="ResendCampaignAsync"/>, which is separated out,
///     restricted to campaigns that already failed, and audited.
///   </item>
///   <item>
///     <b>Patient contact details leave here masked.</b> Not hidden in the
///     browser — masked in the DTO, so the real value is never transmitted.
///     <see cref="RevealPatientContactAsync"/> is the only path to the real
///     value, it requires a stated reason, and it writes to the audit trail
///     before returning anything.
///   </item>
/// </list>
/// <para>
/// Every entry point re-derives the caller's authority from the
/// <see cref="AdminContext"/> the controller built from the signed token, and
/// re-checks that the account being inspected is one they may inspect.
/// </para>
/// </remarks>
public sealed class PharmacySupportService(
    IUserRepository users,
    IPatientRepository patients,
    IPatientGroupRepository groups,
    IPosterRepository posters,
    ICampaignRepository campaigns,
    IVideoRepository videos,
    IBrandAssetService assets,
    IAdminAuditRepository audit,
    CampaignService campaignService,
    ILogger<PharmacySupportService> log)
{
    /// <summary>
    /// Poster thumbnails are shown in a list an administrator scrolls; fifteen
    /// minutes outlives a support call without leaving a long-lived readable
    /// URL to a customer's artwork lying in a browser history.
    /// </summary>
    private static readonly TimeSpan ThumbnailUrlValidity = TimeSpan.FromMinutes(15);

    /// <summary>Upper bound on a single page of patients. Guards against `take=100000`.</summary>
    private const int MaxPageSize = 100;

    /// <summary>
    /// Ceiling for the un-paged lists — posters, campaigns, videos.
    /// </summary>
    /// <remarks>
    /// These are shown in full because a support view that paginates a
    /// pharmacy's twelve posters is worse than one that doesn't. A real
    /// account will not approach this; the number is here so that a pharmacy
    /// which somehow has fifty thousand posters degrades to a truncated list
    /// rather than to a request that never returns.
    /// </remarks>
    private const int ListCap = 1000;

    /* ---------------------------------------------------------------- *
     * Overview
     * ---------------------------------------------------------------- */

    /// <summary>
    /// The same overview, read by a head office about one of its own branches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shares the staff implementation deliberately. Two code paths producing
    /// "the numbers for a pharmacy" drift, and the one used less often drifts
    /// unnoticed — a head office would start seeing a different patient count
    /// from the administrator on the phone to them.
    /// </para>
    /// <para>
    /// What differs is the two things that must: who is allowed to ask, and
    /// what gets written to the audit log. Authorisation here is parenthood,
    /// checked against the branch's own record rather than anything the caller
    /// sent. And the read is recorded — a head office can see across its
    /// branches without switching into them, and switching is what used to
    /// leave the trail.
    /// </para>
    /// </remarks>
    public async Task<Result<PharmacySupportOverviewDto>> GetBranchOverviewAsync(
        string headOfficeId, string branchId, CancellationToken ct)
    {
        var headOffice = await users.FindByIdAsync(headOfficeId, ct);
        if (headOffice is null)
            return Result<PharmacySupportOverviewDto>.NotFound("Account not found.");

        var branch = await users.FindByIdAsync(branchId, ct);

        // Not-yours and not-found give the same answer on purpose. A
        // distinguishable "that exists but is not your branch" lets somebody
        // probe for other pharmacies' account ids.
        if (branch is null
            || !string.Equals(branch.ParentPharmacyId, headOfficeId, StringComparison.Ordinal))
        {
            log.LogWarning(
                "{HeadOffice} asked for branch {Branch}, which is not theirs", headOfficeId, branchId);
            return Result<PharmacySupportOverviewDto>.NotFound("That branch was not found.");
        }

        // Recorded before the data is returned, so a read that happened is
        // logged even if serialising the response then fails.
        await audit.AddAsync(
            AdminAuditEntry.Record(
                headOffice.Id,
                headOffice.Email.Value,
                AdminAction.ViewBranchDetail,
                branch.Id,
                branch.Email.Value,
                $"head office viewed branch records ({branch.Profile.PharmacyName})"),
            ct);

        return await BuildOverviewAsync(branch, ct);
    }

    public async Task<Result<PharmacySupportOverviewDto>> GetOverviewAsync(
        AdminContext actor, string pharmacyId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<PharmacySupportOverviewDto>(failure);
        return await BuildOverviewAsync(resolved.User!, ct);
    }

    /// <summary>
    /// The counts themselves, for a pharmacy the caller is already entitled to.
    /// </summary>
    /// <remarks>
    /// Takes a resolved user rather than an id: authorisation is the caller's
    /// job, and a method that re-derives permission from an id is a method
    /// somebody eventually calls with the wrong one.
    /// </remarks>
    private async Task<Result<PharmacySupportOverviewDto>> BuildOverviewAsync(
        Domain.Users.User user, CancellationToken ct)
    {

        // Six independent reads. Sequential rather than Task.WhenAll: these
        // share a scoped Mongo context and the repositories are not documented
        // as safe to use concurrently from one scope. The page loads in one
        // round trip either way and correctness is worth more than the
        // milliseconds.
        var patientCount = await patients.CountByOwnerAsync(user.Id, ct);
        var groupList = await groups.ListByOwnerAsync(user.Id, ct);
        var posterList = await posters.ListAsync(new PosterQuery(user.Id, Take: ListCap), ct);
        var videoList = await videos.ListByOwnerAsync(user.Id, ct);
        var campaignList = await campaigns.ListByOwnerAsync(user.Id, ct);

        var managerName = user.ManagedByAdminId is { } adminId
            ? (await users.FindByIdAsync(adminId, ct))?.DisplayName
            : null;

        var sent = campaignList.Where(c => c.SentAtUtc is not null).ToList();

        // Group structure. A pharmacy is either a parent or a branch, never
        // both, so at most one of these two reads returns anything — but which
        // one is not known until the account is loaded, and a missing branch
        // list here is the difference between support answering a question and
        // guessing at it.
        var parent = user.ParentPharmacyId is { } parentId
            ? await users.FindByIdAsync(parentId, ct)
            : null;

        var branches = user.IsChildPharmacy
            ? Array.Empty<PharmacyBranchDto>()
            : (await users.ListChildPharmaciesAsync(user.Id, ct))
                .Select(c => new PharmacyBranchDto(
                    UserId: c.Id,
                    PharmacyName: string.IsNullOrWhiteSpace(c.Profile.PharmacyName)
                        ? c.DisplayName
                        : c.Profile.PharmacyName,
                    ContactName: c.DisplayName,
                    Email: c.Email.Value,
                    IsActive: c.IsActive,
                    AwaitingActivation: c.Status == AccountStatus.Invited))
                .ToArray();

        var hierarchy = new PharmacyHierarchyDto(
            ParentId: parent?.Id ?? user.ParentPharmacyId,
            // A parent id with no name means the parent account is gone. Say
            // so rather than rendering a blank, so the orphan is visible.
            ParentName: parent is null
                ? (user.ParentPharmacyId is null ? null : "Unknown pharmacy")
                : (string.IsNullOrWhiteSpace(parent.Profile.PharmacyName)
                    ? parent.DisplayName
                    : parent.Profile.PharmacyName),
            Branches: branches);

        return Result<PharmacySupportOverviewDto>.Success(new PharmacySupportOverviewDto(
            UserId: user.Id,
            PharmacyName: user.Profile.PharmacyName,
            ContactName: user.DisplayName,
            Email: user.Email.Value,
            PostCode: user.Profile.PostCode,
            Tier: user.Tier,
            IsActive: user.IsActive,
            OnboardingCompleted: user.Profile.OnboardingCompleted,
            ManagedByAdminName: managerName,
            BillingStatus: user.Billing.Status.ToString(),
            VideoCredits: user.Billing.VideoCredits,
            LastLoginAtUtc: user.LastLoginAtUtc,
            CreatedAtUtc: user.CreatedAtUtc,
            PatientCount: patientCount,
            GroupCount: groupList.Count,
            PosterCount: posterList.Count,
            PublishedPosterCount: posterList.Count(p => p.Status == PosterStatus.Published),
            VideoCount: videoList.Count,
            CampaignCount: campaignList.Count,
            CampaignsSentCount: sent.Count,
            CampaignsFailedCount: campaignList.Count(c => c.Status == CampaignStatus.Failed),
            RecipientsReached: sent.Sum(c => c.SentCount),
            LastCampaignAtUtc: sent.Count == 0 ? null : sent.Max(c => c.SentAtUtc),
            Hierarchy: hierarchy));
    }

    /* ---------------------------------------------------------------- *
     * Patients
     * ---------------------------------------------------------------- */

    public async Task<Result<SupportPatientListDto>> ListPatientsAsync(
        AdminContext actor,
        string pharmacyId,
        string? search,
        string? groupId,
        int skip,
        int take,
        CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<SupportPatientListDto>(failure);
        var user = resolved.User!;

        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPageSize);

        var query = new PatientQuery(user.Id, search, groupId, skip, take);
        var page = await patients.ListAsync(query, ct);
        var total = await patients.CountAsync(query, ct);

        // Group names, not ids: an administrator reading "Diabetes review" can
        // act on it; a 32-character hex string tells them nothing and sends
        // them looking for a second screen to translate it.
        var groupNames = (await groups.ListByOwnerAsync(user.Id, ct))
            .ToDictionary(g => g.Id, g => g.Name);

        var items = page.Select(p => ToDto(p, groupNames)).ToList();

        return Result<SupportPatientListDto>.Success(
            new SupportPatientListDto(items, total, skip, take));
    }

    /// <summary>
    /// Returns one patient's real email and phone, and records that it happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reason is required, and is stored verbatim. Requiring it is not
    /// bureaucracy for its own sake: an administrator who has to type why they
    /// are looking will, on the occasion when there is no good answer, notice
    /// that. The typed reason also makes the log readable months later, when
    /// "who looked at this patient" needs to become "and why".
    /// </para>
    /// <para>
    /// The audit entry is written <b>before</b> the details are returned. If
    /// the write fails the reveal fails, because an unlogged reveal is exactly
    /// the thing this endpoint exists to prevent.
    /// </para>
    /// </remarks>
    public async Task<Result<RevealedPatientContactDto>> RevealPatientContactAsync(
        AdminContext actor,
        string pharmacyId,
        string patientId,
        string? reason,
        CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<RevealedPatientContactDto>(failure);
        var user = resolved.User!;

        var trimmedReason = (reason ?? string.Empty).Trim();
        if (trimmedReason.Length < 5)
            return Result<RevealedPatientContactDto>.Invalid(
                "Give a reason for viewing this patient's contact details — it is recorded in the audit log.");
        if (trimmedReason.Length > 200)
            trimmedReason = trimmedReason[..200];

        var patient = await patients.FindByIdAsync(patientId, ct);

        // The ownership check is the point, not a formality. Without it this
        // endpoint reads any patient in the database given only their id, and
        // the pharmacy in the URL becomes decoration.
        if (patient is null || patient.OwnerId != user.Id)
            return Result<RevealedPatientContactDto>.NotFound("Patient not found for this pharmacy.");

        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId,
                actor.Email,
                AdminAction.ViewPatientContact,
                user.Id,
                user.Email.Value,
                $"Patient \"{patient.FullName}\" — {trimmedReason}"),
            ct);

        log.LogInformation(
            "Admin {Actor} revealed contact details for a patient of pharmacy {Pharmacy}. Reason: {Reason}",
            PersonalData.Mask(actor.Email), PersonalData.Mask(user.Email.Value), trimmedReason);

        return Result<RevealedPatientContactDto>.Success(new RevealedPatientContactDto(
            patient.Id, patient.FullName, patient.Email, patient.Phone, trimmedReason));
    }

    public async Task<Result<IReadOnlyList<SupportGroupDto>>> ListGroupsAsync(
        AdminContext actor, string pharmacyId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<IReadOnlyList<SupportGroupDto>>(failure);
        var user = resolved.User!;

        var list = await groups.ListByOwnerAsync(user.Id, ct);

        // PatientCount on the group is a denormalised counter maintained by
        // PatientService. It drifts if a write ever half-completes, and a
        // support view showing a stale number is worse than useless — it is
        // the number the administrator will quote back to the customer. So it
        // is recomputed here from the patients themselves.
        var recounted = new List<SupportGroupDto>(list.Count);
        foreach (var g in list)
        {
            var members = await patients.ListByGroupAsync(user.Id, g.Id, ct);
            recounted.Add(new SupportGroupDto(
                g.Id, g.Name, g.Description, members.Count, g.CreatedAtUtc));
        }

        return Result<IReadOnlyList<SupportGroupDto>>.Success(recounted);
    }

    /* ---------------------------------------------------------------- *
     * Posters and videos
     * ---------------------------------------------------------------- */

    public async Task<Result<IReadOnlyList<SupportPosterDto>>> ListPostersAsync(
        AdminContext actor, string pharmacyId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<IReadOnlyList<SupportPosterDto>>(failure);
        var user = resolved.User!;

        var list = await posters.ListAsync(new PosterQuery(user.Id, Take: ListCap), ct);

        // One pass over the campaigns rather than a count query per poster.
        var campaignList = await campaigns.ListByOwnerAsync(user.Id, ct);
        var usage = campaignList
            .GroupBy(c => c.PosterId)
            .ToDictionary(g => g.Key, g => g.Count());

        var items = list
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Select(p => new SupportPosterDto(
                p.Id,
                p.Name,
                p.Status,
                p.SourceTemplateId,
                ThumbnailUrl(p.ThumbnailBlobKey),
                usage.TryGetValue(p.Id, out var used) ? used : 0,
                p.CreatedAtUtc,
                p.UpdatedAtUtc))
            .ToList();

        return Result<IReadOnlyList<SupportPosterDto>>.Success(items);
    }

    public async Task<Result<IReadOnlyList<SupportVideoDto>>> ListVideosAsync(
        AdminContext actor, string pharmacyId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<IReadOnlyList<SupportVideoDto>>(failure);
        var user = resolved.User!;

        var list = await videos.ListByOwnerAsync(user.Id, ct);

        var items = list
            .OrderByDescending(v => v.CreatedAtUtc)
            .Select(v => new SupportVideoDto(
                v.Id, v.Name, v.Topic, v.Status, v.DurationSeconds, v.Note, v.CreatedAtUtc))
            .ToList();

        return Result<IReadOnlyList<SupportVideoDto>>.Success(items);
    }

    /* ---------------------------------------------------------------- *
     * Campaigns
     * ---------------------------------------------------------------- */

    public async Task<Result<IReadOnlyList<SupportCampaignDto>>> ListCampaignsAsync(
        AdminContext actor, string pharmacyId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<IReadOnlyList<SupportCampaignDto>>(failure);
        var user = resolved.User!;

        var list = await campaigns.ListByOwnerAsync(user.Id, ct);
        var posterNames = await PosterNameLookupAsync(user.Id, ct);

        var items = list
            .OrderByDescending(c => c.SentAtUtc ?? c.CreatedAtUtc)
            .Select(c => ToDto(c, posterNames))
            .ToList();

        return Result<IReadOnlyList<SupportCampaignDto>>.Success(items);
    }

    public async Task<Result<SupportCampaignDetailDto>> GetCampaignAsync(
        AdminContext actor, string pharmacyId, string campaignId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<SupportCampaignDetailDto>(failure);
        var user = resolved.User!;

        var campaign = await campaigns.FindByIdAsync(campaignId, ct);
        if (campaign is null || campaign.OwnerId != user.Id)
            return Result<SupportCampaignDetailDto>.NotFound("Campaign not found for this pharmacy.");

        var posterNames = await PosterNameLookupAsync(user.Id, ct);
        var summary = ToDto(campaign, posterNames);

        var report = campaign.DeliveryReport
            .Select(r => new SupportRecipientResultDto(
                ContactMask.Recipient(r.Recipient), r.Status, r.Reason, r.AtUtc))
            .ToList();

        return Result<SupportCampaignDetailDto>.Success(new SupportCampaignDetailDto(
            summary.Id,
            summary.Name,
            summary.Channel,
            summary.Status,
            summary.PosterId,
            summary.PosterName,
            summary.RecipientCount,
            summary.SentCount,
            summary.FailedCount,
            summary.SentAtUtc,
            summary.Note,
            summary.CreatedAtUtc,
            campaign.VideoIds,
            report,
            CanResend: campaign.Status == CampaignStatus.Failed));
    }

    /// <summary>
    /// Re-sends a campaign that failed, on the pharmacy's behalf.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only action in this service that leaves the building. The
    /// restriction to <see cref="CampaignStatus.Failed"/> comes from
    /// <c>CampaignService.RetryAsync</c> and is worth stating plainly:
    /// an administrator cannot use this to send a pharmacy's patients anything
    /// new, only to re-attempt a message the pharmacy already composed and
    /// already tried to send.
    /// </para>
    /// <para>
    /// The plan's monthly recipient cap is re-checked by RetryAsync, so a
    /// retry cannot be used to push an account past a limit it would hit if
    /// the pharmacy pressed the button themselves. Delegating rather than
    /// reimplementing the send is the point — a second copy of that logic here
    /// would be a second place for the quota check to be forgotten.
    /// </para>
    /// </remarks>
    public async Task<Result<SupportCampaignDetailDto>> ResendCampaignAsync(
        AdminContext actor, string pharmacyId, string campaignId, CancellationToken ct)
    {
        var resolved = await ResolveAsync(actor, pharmacyId, ct);
        if (resolved.Failure is { } failure) return Fail<SupportCampaignDetailDto>(failure);
        var user = resolved.User!;

        var existing = await campaigns.FindByIdAsync(campaignId, ct);
        if (existing is null || existing.OwnerId != user.Id)
            return Result<SupportCampaignDetailDto>.NotFound("Campaign not found for this pharmacy.");

        var retry = await campaignService.RetryAsync(campaignId, user.Id, ct);
        if (retry.Kind != ResultKind.Success)
            return Fail<SupportCampaignDetailDto>((retry.Kind, retry.Error ?? "Retry failed."));

        // Recorded after the send, and only on success: a failed retry sent
        // nothing, and logging the attempt as though it had reached patients
        // would make the trail actively misleading.
        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId,
                actor.Email,
                AdminAction.ResendCampaign,
                user.Id,
                user.Email.Value,
                $"\"{existing.Name}\" — {existing.Recipients.Count} recipient(s)"),
            ct);

        log.LogInformation(
            "Admin {Actor} re-sent campaign {Campaign} for pharmacy {Pharmacy}",
            PersonalData.Mask(actor.Email), campaignId, PersonalData.Mask(user.Email.Value));

        return await GetCampaignAsync(actor, pharmacyId, campaignId, ct);
    }

    /* ---------------------------------------------------------------- *
     * Helpers
     * ---------------------------------------------------------------- */

    /// <summary>
    /// Loads the pharmacy and decides whether this caller may look at it.
    /// </summary>
    /// <remarks>
    /// Every public method starts here, so the check exists once and cannot be
    /// omitted from a method added later without that omission being visible.
    ///
    /// Two rules, and the second one is subtler than it first looks.
    ///
    /// A staff account is off limits to an Admin, mirroring
    /// AdminService.CanActOn — an Admin who could browse a SuperAdmin's
    /// workspace has escalated sideways.
    ///
    /// The target must also be a pharmacy, but "is a pharmacy" is a question
    /// about the profile, not about the role. Promote a pharmacy owner to
    /// Admin and their role changes while their pharmacy, patients and
    /// campaigns carry on existing — testing the role here would refuse to
    /// open the very account a colleague was asking about. So the test is
    /// whether there is a pharmacy name on the profile.
    /// </remarks>
    private async Task<(User? User, (ResultKind Kind, string Error)? Failure)> ResolveAsync(
        AdminContext actor, string pharmacyId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(pharmacyId))
            return (null, (ResultKind.Invalid, "A pharmacy id is required."));

        var user = await users.FindByIdAsync(pharmacyId, ct);
        if (user is null)
            return (null, (ResultKind.NotFound, "Pharmacy not found."));

        if (user.Role.IsStaff() && !actor.IsSuperAdmin)
            return (null, (ResultKind.Forbidden,
                "Only a SuperAdmin can view an administrator's account."));

        var ownsPharmacy =
            user.Role == UserRole.PharmacyUser
            || !string.IsNullOrWhiteSpace(user.Profile.PharmacyName);

        if (!ownsPharmacy)
            return (null, (ResultKind.Invalid,
                "This is a staff account with no pharmacy — there is no patient or campaign data to show."));

        return (user, null);
    }

    private static Result<T> Fail<T>((ResultKind Kind, string Error) failure) => failure.Kind switch
    {
        ResultKind.NotFound => Result<T>.NotFound(failure.Error),
        ResultKind.Forbidden => Result<T>.Forbidden(failure.Error),
        ResultKind.Conflict => Result<T>.Conflict(failure.Error),
        ResultKind.Unauthorized => Result<T>.Unauthorized(failure.Error),
        _ => Result<T>.Invalid(failure.Error),
    };

    private async Task<IReadOnlyDictionary<string, string>> PosterNameLookupAsync(
        string ownerId, CancellationToken ct)
    {
        var list = await posters.ListAsync(new PosterQuery(ownerId, Take: ListCap), ct);
        return list.ToDictionary(p => p.Id, p => p.Name);
    }

    private string? ThumbnailUrl(string? blobKey) =>
        blobKey is null ? null : assets.GetReadOnlyUrl(blobKey, ThumbnailUrlValidity).ToString();

    private static SupportPatientDto ToDto(
        Patient p, IReadOnlyDictionary<string, string> groupNames) => new(
        p.Id,
        p.FullName,
        ContactMask.Email(p.Email),
        ContactMask.Phone(p.Phone),
        HasEmail: !string.IsNullOrWhiteSpace(p.Email),
        HasPhone: !string.IsNullOrWhiteSpace(p.Phone),
        // A group the patient belongs to that no longer exists is dropped
        // rather than shown as a raw id — it is a data artefact, not something
        // an administrator can act on.
        GroupNames: p.GroupIds
            .Select(id => groupNames.TryGetValue(id, out var name) ? name : null)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList(),
        Notes: p.Notes,
        CreatedAtUtc: p.CreatedAtUtc);

    private static SupportCampaignDto ToDto(
        Campaign c, IReadOnlyDictionary<string, string> posterNames) => new(
        c.Id,
        c.Name,
        c.Channel,
        c.Status,
        c.PosterId,
        // "(deleted)" rather than blank: a campaign whose poster is gone is a
        // real state with a real explanation, and the blank cell version of it
        // gets reported as a rendering fault.
        posterNames.TryGetValue(c.PosterId, out var name) ? name : "(poster deleted)",
        c.Recipients.Count,
        c.SentCount,
        c.DeliveryReport.Count(r => r.Status == RecipientStatus.Failed),
        c.SentAtUtc,
        c.Note,
        c.CreatedAtUtc);
}
