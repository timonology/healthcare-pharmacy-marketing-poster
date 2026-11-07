using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Users;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Creates the bootstrap SuperAdmin on first startup.
///
/// Idempotent in two directions: it does nothing if any SuperAdmin already
/// exists, and nothing if the configured email is already taken. It never
/// updates an existing account — in particular it will not reset a password,
/// because that would turn "leave the seed config in place" into a standing
/// backdoor that silently reverts a rotated credential on every deploy.
/// </summary>
public sealed class SuperAdminSeeder(
    IUserRepository users,
    IPasswordHasher hasher,
    IOptions<SuperAdminOptions> options,
    IHostEnvironment env,
    IAuditLogger audit,
    ILogger<SuperAdminSeeder> log) : IStartupTask
{
    private static readonly string[] ObviouslyWeak =
    {
        "changeme", "password", "admin", "superadmin", "letmein", "123456",
    };

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            // Only worth mentioning when there's genuinely no way in.
            if (!await users.AnyWithRoleAsync(UserRole.SuperAdmin, ct))
                log.LogWarning(
                    "No SuperAdmin exists and SuperAdmin:Enabled is false — the admin area " +
                    "is unreachable. Set SuperAdmin__Enabled=true with an email and password to bootstrap one.");
            return;
        }

        if (string.IsNullOrWhiteSpace(opts.Email) || string.IsNullOrWhiteSpace(opts.Password))
        {
            log.LogError("SuperAdmin:Enabled is true but Email or Password is empty — skipping seed.");
            return;
        }

        if (await users.AnyWithRoleAsync(UserRole.SuperAdmin, ct))
        {
            log.LogInformation("SuperAdmin already exists — skipping seed.");
            return;
        }

        // Outside development, refuse anything that looks like a placeholder.
        // A seeded super-admin with a guessable password is worse than none:
        // it's a known-address, known-credential account with total access.
        if (!env.IsDevelopment())
        {
            var lowered = opts.Password.ToLowerInvariant();
            if (opts.Password.Length < 16 || ObviouslyWeak.Any(w => lowered.Contains(w)))
            {
                log.LogCritical(
                    "Refusing to seed a SuperAdmin outside Development with a weak password. " +
                    "Use at least 16 characters and avoid common words. No account was created.");
                return;
            }
        }

        Email email;
        try { email = Email.Create(opts.Email); }
        catch (Exception ex)
        {
            log.LogError(ex, "SuperAdmin:Email is not a valid address — skipping seed.");
            return;
        }

        if (await users.FindByEmailAsync(email, ct) is not null)
        {
            log.LogWarning(
                "SuperAdmin seed skipped: {Email} already exists as a non-SuperAdmin account. " +
                "Promote it manually rather than seeding over it.", opts.Email);
            return;
        }

        var admin = User.CreateStaff(
            email,
            string.IsNullOrWhiteSpace(opts.DisplayName) ? "Super Admin" : opts.DisplayName,
            PasswordHash.FromHash(hasher.Hash(opts.Password)),
            UserRole.SuperAdmin);

        await users.AddAsync(admin, ct);
        // A SuperAdmin appearing from configuration is exactly the kind of
        // thing a reviewer must be able to find.
        await audit.RecordAsync(
            AuditEvent.Create(AuditActor.System("superadmin-seeder"), "system.seed.user", "user", admin.Id,
                AuditResults.Ok, new Dictionary<string, string> { ["role"] = admin.Role.ToString() }),
            ct);

        log.LogWarning(
            "Seeded initial SuperAdmin {Email}. Sign in and change this password now, " +
            "then set SuperAdmin__Enabled=false.", opts.Email);
    }
}
