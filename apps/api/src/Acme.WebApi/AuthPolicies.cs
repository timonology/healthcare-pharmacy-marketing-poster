namespace Acme.WebApi;

/// <summary>Policy names, so controllers and Program.cs can't drift apart.</summary>
public static class AuthPolicies
{
    /// <summary>SuperAdmin only — creating, editing, or removing admins.</summary>
    public const string SuperAdminOnly = "SuperAdminOnly";

    /// <summary>
    /// Admin or SuperAdmin. Grants entry to the admin area; it does NOT grant
    /// access to any particular user's record. Row-level scope is enforced in
    /// AdminService, because an Admin may only touch their own pharmacies and
    /// that isn't expressible as a policy.
    /// </summary>
    public const string AdminArea = "AdminArea";
}
