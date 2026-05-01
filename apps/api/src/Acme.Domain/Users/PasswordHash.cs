using Acme.Domain.Common;

namespace Acme.Domain.Users;

/// <summary>
/// Wraps an already-hashed password. Hashing is performed by an Application
/// service (<c>IPasswordHasher</c>) so the domain stays free of crypto details.
/// </summary>
public readonly record struct PasswordHash
{
    public string Value { get; }

    private PasswordHash(string value) => Value = value;

    public static PasswordHash FromHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            throw new DomainException("Password hash is required.");
        return new PasswordHash(hash);
    }

    public override string ToString() => Value;
}
