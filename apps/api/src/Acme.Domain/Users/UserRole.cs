namespace Acme.Domain.Users;

/// <summary>
/// Access level. Ordered so that a higher value means broader privilege —
/// several checks rely on that ordering, so do not reorder these.
/// </summary>
public enum UserRole
{
    /// <summary>A pharmacy customer. The default for everyone who self-registers.</summary>
    PharmacyUser = 0,

    /// <summary>Manages the pharmacy users assigned to them. Cannot see other admins' users.</summary>
    Admin = 1,

    /// <summary>Full access, including creating and removing admins.</summary>
    SuperAdmin = 2,
}

public static class UserRoleExtensions
{
    /// <summary>True for Admin and SuperAdmin — i.e. may enter the /admin area at all.</summary>
    public static bool IsStaff(this UserRole role) => role >= UserRole.Admin;

    /// <summary>
    /// True when this role may act on a user holding <paramref name="target"/>.
    /// Strictly greater, never equal: admins must not be able to edit or
    /// deactivate each other, and a SuperAdmin editing another SuperAdmin is
    /// gated separately so the last one can't be removed by a peer.
    /// </summary>
    public static bool Outranks(this UserRole role, UserRole target) => role > target;
}
