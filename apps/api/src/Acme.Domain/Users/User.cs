using Acme.Domain.Common;

namespace Acme.Domain.Users;

public sealed class User : Entity
{
    public Email Email { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public PasswordHash PasswordHash { get; private set; } = default!;

    // Required by Mongo driver / EF.
    private User() { }

    private User(string id, Email email, string displayName, PasswordHash passwordHash)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
    }

    public static User Register(Email email, string displayName, PasswordHash passwordHash)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("Display name is required.");
        if (displayName.Length > 80)
            throw new DomainException("Display name must be 80 chars or fewer.");

        return new User(Guid.NewGuid().ToString("N"), email, displayName.Trim(), passwordHash);
    }

    public void Rename(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new DomainException("Display name is required.");
        DisplayName = displayName.Trim();
        Touch();
    }

    public void ChangePassword(PasswordHash newHash)
    {
        PasswordHash = newHash;
        Touch();
    }
}
