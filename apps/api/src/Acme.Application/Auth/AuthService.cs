using Acme.Application.Common;
using Acme.Domain.Auth;
using Acme.Domain.Common;
using Acme.Domain.Users;
using Microsoft.Extensions.Options;

namespace Acme.Application.Auth;

public sealed class AuthService(
    IUserRepository users,
    IPasswordHasher hasher,
    IJwtTokenService jwt,
    IRefreshTokenRepository refreshTokens,
    IRefreshTokenGenerator refreshGenerator,
    IOptions<AuthOptions> options)
{
    private readonly TimeSpan _refreshLifetime = TimeSpan.FromDays(options.Value.RefreshTokenDays);

    public async Task<Result<AuthResponse>> RegisterAsync(RegisterRequest req, CancellationToken ct)
    {
        Email email;
        try { email = Email.Create(req.Email); }
        catch (DomainException ex) { return Result<AuthResponse>.Invalid(ex.Message); }

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return Result<AuthResponse>.Invalid("Password must be at least 8 characters.");

        if (await users.FindByEmailAsync(email, ct) is not null)
            return Result<AuthResponse>.Conflict("Email is already registered.");

        var hash = PasswordHash.FromHash(hasher.Hash(req.Password));

        User user;
        try { user = User.Register(email, req.DisplayName, hash); }
        catch (DomainException ex) { return Result<AuthResponse>.Invalid(ex.Message); }

        await users.AddAsync(user, ct);

        return Result<AuthResponse>.Success(await IssueAsync(user, ct));
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        Email email;
        try { email = Email.Create(req.Email); }
        catch (DomainException) { return Result<AuthResponse>.Unauthorized("Invalid email or password."); }

        var user = await users.FindByEmailAsync(email, ct);
        if (user is null || !hasher.Verify(req.Password, user.PasswordHash.Value))
            return Result<AuthResponse>.Unauthorized("Invalid email or password.");

        return Result<AuthResponse>.Success(await IssueAsync(user, ct));
    }

    public async Task<Result<AuthResponse>> RefreshAsync(string rawToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return Result<AuthResponse>.Unauthorized("Refresh token is required.");

        var hash = refreshGenerator.Hash(rawToken);
        var existing = await refreshTokens.FindByHashAsync(hash, ct);
        if (existing is null)
            return Result<AuthResponse>.Unauthorized("Refresh token not recognized.");

        if (!existing.IsActive)
        {
            await refreshTokens.RevokeAllForUserAsync(existing.UserId, ct);
            return Result<AuthResponse>.Unauthorized("Refresh token is no longer valid.");
        }

        var user = await users.FindByIdAsync(existing.UserId, ct);
        if (user is null) return Result<AuthResponse>.Unauthorized("Account not found.");

        var (accessToken, accessExp) = jwt.Issue(user);
        var newRaw = refreshGenerator.Generate();
        var newHash = refreshGenerator.Hash(newRaw);
        var newToken = RefreshToken.Issue(user.Id, newHash, _refreshLifetime);
        await refreshTokens.AddAsync(newToken, ct);

        existing.Rotate(newHash);
        await refreshTokens.UpdateAsync(existing, ct);

        return Result<AuthResponse>.Success(new AuthResponse(
            accessToken,
            accessExp,
            newRaw,
            newToken.ExpiresAtUtc,
            new UserProfile(user.Id, user.Email.Value, user.DisplayName, user.CreatedAtUtc)));
    }

    public async Task LogoutAsync(string userId, CancellationToken ct) =>
        await refreshTokens.RevokeAllForUserAsync(userId, ct);

    private async Task<AuthResponse> IssueAsync(User user, CancellationToken ct)
    {
        var (accessToken, accessExp) = jwt.Issue(user);

        var raw = refreshGenerator.Generate();
        var token = RefreshToken.Issue(user.Id, refreshGenerator.Hash(raw), _refreshLifetime);
        await refreshTokens.AddAsync(token, ct);

        return new AuthResponse(
            accessToken,
            accessExp,
            raw,
            token.ExpiresAtUtc,
            new UserProfile(user.Id, user.Email.Value, user.DisplayName, user.CreatedAtUtc));
    }
}
