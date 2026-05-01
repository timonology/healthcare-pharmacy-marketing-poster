using System.Reflection;
using Acme.Domain.Users;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Hydrates a <see cref="User"/> from persisted state without going through
/// <c>Register</c> (which would create a new id and timestamps).
/// </summary>
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
        DateTime createdAtUtc,
        DateTime updatedAtUtc)
    {
        var user = (User)PrivateCtor.Invoke(null);

        SetProp(user, nameof(User.Id), id);
        SetProp(user, nameof(User.Email), Email.Create(email));
        SetProp(user, nameof(User.DisplayName), displayName);
        SetProp(user, nameof(User.PasswordHash), PasswordHash.FromHash(passwordHash));
        SetProp(user, nameof(User.CreatedAtUtc), createdAtUtc);
        SetProp(user, nameof(User.UpdatedAtUtc), updatedAtUtc);

        return user;
    }

    private static void SetProp(object target, string name, object value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
