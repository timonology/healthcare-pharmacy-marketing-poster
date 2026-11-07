using Acme.Domain.Admin;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;

namespace Acme.Application.Admin;

/// <summary>A user row in the admin table.</summary>
public sealed record AdminUserDto(
    string Id,
    string Email,
    string DisplayName,
    UserRole Role,
    SubscriptionTier Tier,
    bool IsActive,
    string? ManagedByAdminId,
    string? ManagedByAdminName,
    string PharmacyName,
    string PostCode,
    bool OnboardingCompleted,
    bool IsHeadOffice,
    bool IsChildPharmacy,
    int VideoCredits,
    string BillingStatus,
    DateTime? CurrentPeriodEndUtc,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc);

public sealed record AdminListDto(
    IReadOnlyList<AdminUserDto> Items,
    long Total,
    int Skip,
    int Take);

/// <summary>An admin plus how many pharmacies they carry.</summary>
public sealed record AdminSummaryDto(
    string Id,
    string Email,
    string DisplayName,
    bool IsActive,
    int ManagedUserCount,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc);

public sealed record CreateAdminRequest(
    string Email,
    string Password,
    string DisplayName);

public sealed record UpdateAdminRequest(
    string? DisplayName,
    bool? IsActive);

public sealed record AssignUserRequest(string UserId, string AdminId);

public sealed record OverrideTierRequest(SubscriptionTier Tier, string? Reason);

public sealed record GrantCreditsRequest(int Credits, string? Reason);

/// <summary>
/// Grants or withdraws the ability to run branches.
/// </summary>
/// <param name="Granted">True to grant, false to withdraw.</param>
/// <param name="Reason">
/// Recorded in the audit log. Optional in the contract, but it is the field
/// that makes the entry answerable a year later.
/// </param>
public sealed record SetHeadOfficeRequest(bool Granted, string? Reason = null);

public sealed record ChangeRoleRequest(UserRole Role);

/// <param name="Series">
/// One entry per day across the whole window, including days with no activity.
/// </param>
public sealed record AdminAnalyticsDto(
    int Days,
    IReadOnlyList<AnalyticsPoint> Series,
    IReadOnlyList<TopPharmacyDto> TopPharmacies,
    long PostersInPeriod,
    long VideosInPeriod,
    long CampaignsInPeriod);

/// <param name="Date">UTC day, ISO-8601.</param>
public sealed record AnalyticsPoint(
    string Date,
    long Posters,
    long Videos,
    long Campaigns);

public sealed record TopPharmacyDto(
    string UserId,
    string PharmacyName,
    string Email,
    long Posters,
    long Videos,
    long Campaigns,
    long Total);

/// <param name="Aspect">"banner" | "hero" | "portrait" | "full".</param>
/// <param name="Offset">
/// Which candidate to take when the provider is stock search — lets the UI
/// offer "try another" without re-typing the description.
/// </param>
/// <param name="Provider">"ai" for AI only (an error, never stock, when it cannot), "stock", or "auto".</param>
public sealed record CreatePosterImageRequest(
    string Description,
    string? Aspect = null,
    int Offset = 0,
    string? Provider = null);

/// <summary>Dashboard figures.</summary>
public sealed record AdminStatsDto(
    long TotalUsers,
    long ActiveUsers,
    long InactiveUsers,
    long SuperAdmins,
    long Admins,
    long PharmacyUsers,
    /// <summary>
    /// Accounts that own a pharmacy, including any held by staff. This is what
    /// the Pharmacies page lists; <see cref="PharmacyUsers"/> is the role count
    /// that makes the breakdown sum to <see cref="TotalUsers"/>.
    /// </summary>
    long PharmacyAccounts,
    long FreeTier,
    long StarterTier,
    long ProTier,
    long NewUsersThisMonth,
    long ActiveLast30Days,
    long VideosThisMonth,
    long TotalVideos,
    long PostersThisMonth,
    long TotalPosters,
    long CampaignsSentThisMonth,
    long TotalCampaignsSent,
    long RecipientsReached,
    decimal EstimatedMonthlyRevenueGbp,
    decimal EstimatedAnnualRunRateGbp);

public sealed record AuditEntryDto(
    string Id,
    string ActorEmail,
    AdminAction Action,
    string? TargetEmail,
    string? Detail,
    DateTime CreatedAtUtc);

public sealed record AuditListDto(
    IReadOnlyList<AuditEntryDto> Items,
    long Total,
    int Skip,
    int Take);
