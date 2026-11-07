using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Acme.Application.Auth;
using Acme.Domain.Users;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Acme.Infrastructure.Security;

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options) => _options = options.Value;

    public (string Token, DateTime ExpiresAtUtc) Issue(User user)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(_options.ExpiryMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email.Value),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),

            // ClaimTypes.Role is what [Authorize(Roles = ...)] and
            // RequireRole() read by default, so emitting it here makes
            // role-based policies work without any custom claim mapping.
            new(ClaimTypes.Role, user.Role.ToString()),

            // Duplicated under a short name for the browser: the frontend
            // decodes the JWT for cosmetic gating (showing the Admin nav) and
            // the long schema URI is awkward to read there. The backend never
            // trusts this copy.
            new("role", user.Role.ToString()),
        };

        // Lets an Admin's own id reach authorization handlers without a
        // database round trip. Absent for pharmacy users and super admins.
        if (user.Role == UserRole.Admin)
            claims.Add(new Claim("admin_id", user.Id));

        return Sign(claims, now, expiresAt);
    }

    /// <summary>
    /// A token that operates on <paramref name="effective"/> while recording
    /// <paramref name="actor"/> as the one doing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The role emitted is the <b>effective</b> account's, not the actor's. A
    /// parent pharmacy managing a child must have exactly the child's
    /// permissions — no more. Emitting the actor's role would be an escalation
    /// path the moment a parent is ever promoted to staff.
    /// </para>
    /// <para>
    /// No <c>admin_id</c> claim is added for the same reason: a delegated token
    /// is never an administrator's token.
    /// </para>
    /// </remarks>
    public (string Token, DateTime ExpiresAtUtc) IssueDelegated(
        User effective, User actor, TimeSpan validFor)
    {
        var now = DateTime.UtcNow;

        // Capped regardless of what the caller asks for. This token grants
        // access to someone else's account, so its lifetime is not a parameter
        // worth trusting.
        var expiresAt = now.Add(validFor > TimeSpan.FromHours(1)
            ? TimeSpan.FromHours(1)
            : validFor);

        var claims = new List<Claim>
        {
            // Subject is the pharmacy being managed — every endpoint reads this.
            new(JwtRegisteredClaimNames.Sub, effective.Id),
            new(JwtRegisteredClaimNames.Email, effective.Email.Value),
            new(JwtRegisteredClaimNames.Name, effective.DisplayName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),

            new(ClaimTypes.Role, effective.Role.ToString()),
            new("role", effective.Role.ToString()),

            // Who is really acting. Presence of this claim is what marks the
            // token as delegated.
            new(DelegationClaims.Actor, actor.Id),
            new(DelegationClaims.ActorEmail, actor.Email.Value),
            // Attribution only — see DelegationClaims.ActorRole.
            new(DelegationClaims.ActorRole, actor.Role.ToString()),
        };

        return Sign(claims, now, expiresAt);
    }

    private (string Token, DateTime ExpiresAtUtc) Sign(
        List<Claim> claims, DateTime now, DateTime expiresAt)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
