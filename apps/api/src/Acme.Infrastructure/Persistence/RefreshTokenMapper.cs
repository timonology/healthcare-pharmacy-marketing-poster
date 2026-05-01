using System.Reflection;
using Acme.Domain.Auth;

namespace Acme.Infrastructure.Persistence;

internal static class RefreshTokenMapper
{
    private static readonly ConstructorInfo Ctor = typeof(RefreshToken)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("RefreshToken parameterless ctor missing.");

    public static RefreshToken Hydrate(RefreshTokenDocument d)
    {
        var token = (RefreshToken)Ctor.Invoke(null);
        Set(token, nameof(RefreshToken.Id), d.Id);
        Set(token, nameof(RefreshToken.UserId), d.UserId);
        Set(token, nameof(RefreshToken.TokenHash), d.TokenHash);
        Set(token, nameof(RefreshToken.ExpiresAtUtc), d.ExpiresAtUtc);
        Set(token, nameof(RefreshToken.RevokedAtUtc), d.RevokedAtUtc);
        Set(token, nameof(RefreshToken.ReplacedByTokenHash), d.ReplacedByTokenHash);
        Set(token, nameof(RefreshToken.CreatedAtUtc), d.CreatedAtUtc);
        Set(token, nameof(RefreshToken.UpdatedAtUtc), d.UpdatedAtUtc);
        return token;
    }

    public static RefreshTokenDocument ToDocument(RefreshToken t) => new()
    {
        Id = t.Id,
        UserId = t.UserId,
        TokenHash = t.TokenHash,
        ExpiresAtUtc = t.ExpiresAtUtc,
        RevokedAtUtc = t.RevokedAtUtc,
        ReplacedByTokenHash = t.ReplacedByTokenHash,
        CreatedAtUtc = t.CreatedAtUtc,
        UpdatedAtUtc = t.UpdatedAtUtc,
    };

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
