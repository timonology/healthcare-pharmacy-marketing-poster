namespace Acme.Infrastructure.Options;

/// <summary>
/// Bootstrap account, created on first startup so there is a way into the
/// admin area on a fresh database.
/// </summary>
public sealed class SuperAdminOptions
{
    public const string SectionName = "SuperAdmin";

    /// <summary>
    /// Off by default. Seeding a privileged account is not something that
    /// should happen because someone forgot to configure it.
    /// </summary>
    public bool Enabled { get; set; }

    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "Super Admin";
}
