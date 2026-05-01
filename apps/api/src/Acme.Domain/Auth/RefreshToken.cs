using Acme.Domain.Common;

namespace Acme.Domain.Auth;

public sealed class RefreshToken : Entity
{
    public string UserId { get; private set; } = default!;
    public string TokenHash { get; private set; } = default!;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    private RefreshToken() { }

    public static RefreshToken Issue(
        string userId,
        string tokenHash,
        TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new DomainException("UserId is required.");
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainException("Token hash is required.");
        if (lifetime <= TimeSpan.Zero)
            throw new DomainException("Lifetime must be positive.");

        return new RefreshToken
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAtUtc = DateTime.UtcNow.Add(lifetime),
        };
    }

    public bool IsActive =>
        RevokedAtUtc is null && DateTime.UtcNow < ExpiresAtUtc;

    public void Rotate(string replacementHash)
    {
        if (!IsActive)
            throw new DomainException("Refresh token is not active.");
        RevokedAtUtc = DateTime.UtcNow;
        ReplacedByTokenHash = replacementHash;
        Touch();
    }

    public void Revoke()
    {
        if (RevokedAtUtc is not null) return;
        RevokedAtUtc = DateTime.UtcNow;
        Touch();
    }
}
