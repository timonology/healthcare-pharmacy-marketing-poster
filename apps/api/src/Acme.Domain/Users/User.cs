using Acme.Domain.Common;
using Acme.Domain.Subscriptions;

namespace Acme.Domain.Users;

public sealed class User : Entity
{
    public Email Email { get; private set; } = default!;
    public string DisplayName { get; private set; } = default!;
    public PasswordHash PasswordHash { get; private set; } = default!;
    public SubscriptionTier Tier { get; private set; } = SubscriptionTier.Free;
    public PharmacyProfile Profile { get; private set; } = PharmacyProfile.Empty();

    private User() { }

    private User(string id, Email email, string displayName, PasswordHash passwordHash)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        Tier = SubscriptionTier.Free;
        Profile = PharmacyProfile.Empty();
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

    public void ChangeTier(SubscriptionTier tier)
    {
        Tier = tier;
        Touch();
    }

    public void SetProfile(PharmacyProfile profile)
    {
        Profile = profile;
        Touch();
    }
}
