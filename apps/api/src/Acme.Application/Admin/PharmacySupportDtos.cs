using Acme.Domain.Campaigns;
using Acme.Domain.Posters;
using Acme.Domain.Subscriptions;
using Acme.Domain.Videos;

namespace Acme.Application.Admin;

/* ------------------------------------------------------------------ *
 * Overview
 * ------------------------------------------------------------------ */

/// <summary>
/// Everything an administrator needs to orient themselves on a support call,
/// before drilling into any one tab.
/// </summary>
public sealed record PharmacySupportOverviewDto(
    string UserId,
    string PharmacyName,
    string ContactName,
    string Email,
    string PostCode,
    SubscriptionTier Tier,
    bool IsActive,
    bool OnboardingCompleted,
    string? ManagedByAdminName,
    string BillingStatus,
    int VideoCredits,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    int PatientCount,
    int GroupCount,
    int PosterCount,
    int PublishedPosterCount,
    int VideoCount,
    int CampaignCount,
    int CampaignsSentCount,
    int CampaignsFailedCount,
    int RecipientsReached,
    DateTime? LastCampaignAtUtc,
    PharmacyHierarchyDto Hierarchy);

/// <summary>
/// Where this pharmacy sits in a group, from the support view.
/// </summary>
/// <remarks>
/// Support needs this before it can answer most questions about a branch:
/// "who set this up", "why can someone else see our patients", "we were
/// charged twice". Without it a branch looks like an unrelated account and the
/// answer is a guess.
///
/// Both directions are present because a pharmacy can only be one or the
/// other — a parent has branches, a branch has a parent, and neither has both.
/// </remarks>
public sealed record PharmacyHierarchyDto(
    string? ParentId,
    string? ParentName,
    IReadOnlyList<PharmacyBranchDto> Branches);

public sealed record PharmacyBranchDto(
    string UserId,
    string PharmacyName,
    string ContactName,
    string Email,
    bool IsActive,
    bool AwaitingActivation);

/* ------------------------------------------------------------------ *
 * Patients
 * ------------------------------------------------------------------ */

/// <summary>
/// A patient row for the support view, with contact details already masked.
/// </summary>
/// <remarks>
/// The masking happens on this side of the wire on purpose. Sending the real
/// address and hiding it in the UI would mean every administrator's browser
/// held a full copy of the pharmacy's patient list, retrievable from devtools
/// or a network log by anyone who thought to look. Masking in the DTO means
/// the value genuinely is not there.
/// </remarks>
public sealed record SupportPatientDto(
    string Id,
    string FullName,
    string? MaskedEmail,
    string? MaskedPhone,
    bool HasEmail,
    bool HasPhone,
    IReadOnlyList<string> GroupNames,
    string Notes,
    DateTime CreatedAtUtc);

public sealed record SupportPatientListDto(
    IReadOnlyList<SupportPatientDto> Items,
    long Total,
    int Skip,
    int Take);

/// <summary>The unmasked contact details, returned only from the audited reveal.</summary>
public sealed record RevealedPatientContactDto(
    string PatientId,
    string FullName,
    string? Email,
    string? Phone,
    string Reason);

public sealed record RevealPatientRequest(string Reason);

public sealed record SupportGroupDto(
    string Id,
    string Name,
    string Description,
    int PatientCount,
    DateTime CreatedAtUtc);

/* ------------------------------------------------------------------ *
 * Posters and videos
 * ------------------------------------------------------------------ */

public sealed record SupportPosterDto(
    string Id,
    string Name,
    PosterStatus Status,
    string? SourceTemplateId,
    string? ThumbnailUrl,
    int UsedInCampaigns,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SupportVideoDto(
    string Id,
    string Name,
    string Topic,
    VideoStatus Status,
    int DurationSeconds,
    string? Note,
    DateTime CreatedAtUtc);

/* ------------------------------------------------------------------ *
 * Campaigns
 * ------------------------------------------------------------------ */

public sealed record SupportCampaignDto(
    string Id,
    string Name,
    CampaignChannel Channel,
    CampaignStatus Status,
    string PosterId,
    string PosterName,
    int RecipientCount,
    int SentCount,
    int FailedCount,
    DateTime? SentAtUtc,
    string? Note,
    DateTime CreatedAtUtc);

/// <param name="MaskedRecipient">
/// Masked for the same reason patient rows are: a delivery report is a list of
/// patient email addresses wearing a different hat.
/// </param>
public sealed record SupportRecipientResultDto(
    string MaskedRecipient,
    RecipientStatus Status,
    string? Reason,
    DateTime AtUtc);

public sealed record SupportCampaignDetailDto(
    string Id,
    string Name,
    CampaignChannel Channel,
    CampaignStatus Status,
    string PosterId,
    string PosterName,
    int RecipientCount,
    int SentCount,
    int FailedCount,
    DateTime? SentAtUtc,
    string? Note,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> VideoIds,
    IReadOnlyList<SupportRecipientResultDto> DeliveryReport,
    bool CanResend);
