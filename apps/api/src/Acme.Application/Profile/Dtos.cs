using Acme.Domain.Subscriptions;

namespace Acme.Application.Profile;

public sealed record PharmacyProfileDto(
    string PharmacyName,
    string Address,
    string PostCode,
    string Description,
    string ContactName,
    string ContactPhone,
    string? SonarFCode,
    bool OnboardingCompleted,

    /// <summary>
    /// Where a patient books, or null. Always absolute http/https.
    /// </summary>
    /// <remarks>
    /// <b>Read here, written on the brand kit.</b> The profile page shows this
    /// so a pharmacist looking at their details can see it, and sends them to
    /// the brand kit to change it. It is not accepted by
    /// <see cref="UpsertProfileRequest"/> — there is one copy of this URL, it
    /// lives on the kit, and a second write path is how the two would drift.
    /// </remarks>
    string? BookingUrl);

public sealed record UpsertProfileRequest(
    string PharmacyName,
    string Address,
    string PostCode,
    string Description,
    string ContactName,
    string ContactPhone,
    string? SonarFCode);

public sealed record OnboardRequest(
    string PharmacyName,
    string Address,
    string PostCode,
    string Description,
    string ContactName,
    string ContactPhone,
    string? SonarFCode,
    SubscriptionTier? Tier);

public sealed record MeDto(
    string Id,
    string Email,
    string DisplayName,
    SubscriptionTier Tier,
    PharmacyProfileDto Profile,
    DateTime CreatedAtUtc,
    Acme.Domain.Users.UserRole Role);
