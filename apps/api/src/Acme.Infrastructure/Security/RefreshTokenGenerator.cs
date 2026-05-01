using System.Security.Cryptography;
using System.Text;
using Acme.Application.Auth;

namespace Acme.Infrastructure.Security;

public sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    private const int RawByteLength = 32; // 256 bits

    public string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(RawByteLength);
        return Base64UrlEncode(bytes);
    }

    public string Hash(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var digest = SHA256.HashData(bytes);
        var sb = new StringBuilder(digest.Length * 2);
        foreach (var b in digest) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
