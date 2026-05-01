namespace Acme.Application.Auth;

public interface IRefreshTokenGenerator
{
    /// <summary>Random opaque token to send to the client.</summary>
    string Generate();

    /// <summary>SHA-256 hex digest used for storage and lookup.</summary>
    string Hash(string token);
}
