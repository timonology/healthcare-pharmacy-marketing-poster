using System.Reflection;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;

namespace Acme.Infrastructure.Persistence;

internal static class UserMapper
{
    private static readonly ConstructorInfo PrivateCtor = typeof(User)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("User parameterless ctor missing.");

    public static User Hydrate(
        string id,
        string email,
        string displayName,
        string passwordHash,
        SubscriptionTier tier,
        PharmacyProfile profile,
        BillingProfile billing,
        UserRole role,
        string? managedByAdminId,
        bool isActive,
        AccountStatus status,
        string? parentPharmacyId,
        DateTime? lastLoginAtUtc,
        DateTime? emailVerifiedAtUtc,
        DateTime? headOfficeGrantedAtUtc,
        DateTime createdAtUtc,
        DateTime updatedAtUtc,
        DateTime? sessionsValidAfterUtc = null)
    {
        var user = (User)PrivateCtor.Invoke(null);

        SetProp(user, nameof(User.Id), id);
        SetProp(user, nameof(User.Email), Email.Create(email));
        SetProp(user, nameof(User.DisplayName), displayName);
        SetProp(user, nameof(User.PasswordHash), PasswordHash.FromHash(passwordHash));
        SetProp(user, nameof(User.Tier), tier);
        SetProp(user, nameof(User.Profile), profile);
        SetProp(user, nameof(User.Billing), billing);
        SetProp(user, nameof(User.Role), role);
        SetProp(user, nameof(User.ManagedByAdminId), managedByAdminId);
        SetProp(user, nameof(User.IsActive), isActive);
        SetProp(user, nameof(User.Status), status);
        SetProp(user, nameof(User.ParentPharmacyId), parentPharmacyId);
        SetProp(user, nameof(User.EmailVerifiedAtUtc), emailVerifiedAtUtc);
        SetProp(user, nameof(User.HeadOfficeGrantedAtUtc), headOfficeGrantedAtUtc);
        SetProp(user, nameof(User.LastLoginAtUtc), lastLoginAtUtc);
        SetProp(user, nameof(User.SessionsValidAfterUtc), sessionsValidAfterUtc);
        SetProp(user, nameof(User.CreatedAtUtc), createdAtUtc);
        SetProp(user, nameof(User.UpdatedAtUtc), updatedAtUtc);

        return user;
    }

    private static void SetProp(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
