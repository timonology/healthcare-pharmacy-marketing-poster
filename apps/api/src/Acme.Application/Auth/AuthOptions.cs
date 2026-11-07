namespace Acme.Application.Auth;

/// <summary>
/// Settings consumed by <see cref="AuthService"/>. Bound from configuration
/// in the WebApi composition root.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Refresh token lifetime. Default 30 days.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// How long a password-reset link works. Default 60 minutes.
    /// </summary>
    /// <remarks>
    /// Short on purpose. Unlike an invitation — which is expected to sit in an
    /// inbox over a weekend — a reset is requested by somebody who is at the
    /// keyboard now. The window is exactly how long a forwarded or intercepted
    /// email remains a live credential for an existing account.
    /// </remarks>
    public int PasswordResetMinutes { get; set; } = 60;

    /// <summary>How long an email-verification link works. Default 72 hours.</summary>
    public int EmailVerificationHours { get; set; } = 72;

    /// <summary>
    /// Send a welcome email when somebody registers. Default on.
    /// </summary>
    /// <remarks>
    /// A switch rather than a constant so a bulk import or a load test can be
    /// run without mailing everybody it touches.
    /// </remarks>
    public bool SendWelcomeEmail { get; set; } = true;

    /// <summary>
    /// Require a confirmed address before an account can be used. Default off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification is recorded from the moment this ships — every new account
    /// gets a verification email and <c>EmailVerifiedAtUtc</c> is set when the
    /// link is clicked — but nothing is <i>enforced</i> until this is true.
    /// </para>
    /// <para>
    /// Separating the two matters because every account that already exists is
    /// unverified. Turning enforcement on the same day the feature ships would
    /// lock out the entire customer base. Let it run, watch the proportion of
    /// verified accounts climb, send a reminder to the stragglers, and only
    /// then flip this.
    /// </para>
    /// </remarks>
    public bool RequireEmailVerification { get; set; }
}
