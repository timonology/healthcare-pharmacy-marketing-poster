using Acme.Application.Auth;

namespace Acme.Infrastructure.Security;

public sealed class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    public string Hash(string plainText) =>
        BCrypt.Net.BCrypt.HashPassword(plainText, WorkFactor);

    public bool Verify(string plainText, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(plainText, hash); }
        catch { return false; }
    }
}
