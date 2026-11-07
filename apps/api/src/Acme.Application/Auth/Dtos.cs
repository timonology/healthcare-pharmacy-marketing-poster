using Acme.Domain.Subscriptions;
using Acme.Domain.Users;

namespace Acme.Application.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

/// <summary>Starts a password reset. Answered identically whether or not the address exists.</summary>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>Completes a password reset with the token from the emailed link.</summary>
public sealed record ResetPasswordRequest(string Token, string Password);

/// <summary>Redeems an email-verification link.</summary>
public sealed record VerifyEmailRequest(string Token);

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
    bool OnboardingCompleted,
    DateTime CreatedAtUtc,
    UserRole Role);

/// <summary>
/// The last account email of one kind sent to the signed-in account.
/// </summary>
/// <param name="Purpose">PasswordReset or EmailVerification.</param>
/// <param name="RequestedAtUtc">When the link was issued.</param>
/// <param name="Outcome">accepted, rejected, error, or unknown for links issued before this was recorded.</param>
/// <param name="LinkHost">The host the emailed link points at — the public web address, or localhost when misconfigured.</param>
public sealed record AccountEmailStatusDto(
    string Purpose,
    DateTime RequestedAtUtc,
    string Outcome,
    string? LinkHost);
