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
}
