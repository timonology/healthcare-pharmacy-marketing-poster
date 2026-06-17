using Acme.Domain.Subscriptions;

namespace Acme.Application.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserProfile User);

public sealed record UserProfile(
    string Id,
    string Email,
    string DisplayName,
    SubscriptionTier Tier,
    DateTime CreatedAtUtc);
