using Acme.Domain.Auth;

namespace Acme.Application.Auth;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken ct);
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct);
    Task UpdateAsync(RefreshToken token, CancellationToken ct);

    /// <summary>Revoke every refresh token belonging to a user (logout-everywhere).</summary>
    Task RevokeAllForUserAsync(string userId, CancellationToken ct);
}
