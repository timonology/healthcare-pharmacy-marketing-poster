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
    bool OnboardingCompleted);

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
    DateTime CreatedAtUtc);
