using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Acme.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Creates the dedicated account <c>pnpm smoke</c> signs in as.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate account.</b> The smoke test ran as the bootstrap
/// SuperAdmin, which meant every run left posters, campaigns and videos on the
/// one account an operator has to be able to trust, and exercised the product
/// as staff. Staff sit on the Free plan, so the video and AI-design doors — the
/// expensive ones this test exists to check — reported "no allowance" and
/// skipped. A green run that skipped the paid half is exactly the kind of
/// reassuring nonsense the smoke was written to stop.
/// </para>
/// <para>
/// <b>Least privilege.</b> It is an ordinary pharmacy user, not an admin. The
/// credential lives in a `.env` and is typed into CI eventually; what a leak
/// costs should be one pharmacy account holding test data.
/// </para>
/// <para>
/// <b>It never updates an existing account.</b> Same rule as
/// <see cref="SuperAdminSeeder"/>: if the email is taken, leave it entirely
/// alone. A seeder that reset the password on boot would turn "leave the config
/// in place" into a standing backdoor that silently reverts a rotated
/// credential on every deploy.
/// </para>
/// <para>
/// <b>Environments.</b> Development and Staging seed when enabled. Production
/// is possible but deliberately awkward: it demands an explicit opt-in <i>and</i>
/// a password that is neither short nor the one shipped in this repository —
/// because a known-address account with a published password on a real
/// deployment is worse than having no smoke account at all.
/// </para>
/// </remarks>
public sealed class SmokeAccountSeeder(
    IUserRepository users,
    IPasswordHasher hasher,
    IOptions<SmokeAccountOptions> options,
    IHostEnvironment env,
    IAuditLogger audit,
    ILogger<SmokeAccountSeeder> log) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;

        if (string.IsNullOrWhiteSpace(opts.Email))
        {
            log.LogError(
                "SmokeAccount:Enabled is true but Email is empty — no account was created. " +
                "`pnpm smoke` will fail at sign-in.");
            return;
        }

        // The rule lives in SmokeAccountPolicy so it can be tested without a
        // host or a repository fake. Refusing is loud: a smoke account that
        // silently failed to exist looks, from the runner, exactly like a
        // deployment with broken authentication.
        if (SmokeAccountPolicy.WhyPasswordRefused(opts.Password, env.IsDevelopment()) is { } refusal)
        {
            log.LogCritical(
                "Refusing to seed the smoke test account: {Reason} No account was created.",
                refusal);
            return;
        }

        Email email;
        try { email = Email.Create(opts.Email); }
        catch (Exception ex)
        {
            log.LogError(ex, "SmokeAccount:Email is not a valid address — no account was created.");
            return;
        }

        // Taken is taken. Not an error: on every boot after the first, this is
        // the expected outcome.
        if (await users.FindByEmailAsync(email, ct) is not null)
        {
            log.LogDebug("Smoke account {Email} already exists — leaving it alone.", opts.Email);
            return;
        }

        var user = User.Register(
            email,
            string.IsNullOrWhiteSpace(opts.ContactName) ? "Smoke Test" : opts.ContactName,
            PasswordHash.FromHash(hasher.Hash(opts.Password)));

        // Onboarding completed, deliberately.
        //
        // An account mid-onboarding is refused by the parts of the product the
        // smoke needs, so it would fail at the first door with a message about a
        // form rather than anything about the deployment. The profile is also
        // what makes this account legible as test data in the admin list.
        user.SetProfile(PharmacyProfile.Create(
            pharmacyName: string.IsNullOrWhiteSpace(opts.PharmacyName)
                ? "Smoke Test Pharmacy"
                : opts.PharmacyName,
            address: "1 Test Street, Manchester",
            postCode: "M1 1AA",
            description:
                "Automated pre-release smoke test account. Records here are created and "
                + "deleted by `pnpm smoke`. Not a real pharmacy.",
            contactName: string.IsNullOrWhiteSpace(opts.ContactName) ? "Smoke Test" : opts.ContactName,
            contactPhone: "0161 000 0000",
            sonarFCode: null));

        if (opts.Tier != SubscriptionTier.Free) user.ChangeTier(opts.Tier);
        if (opts.VideoCredits > 0) user.AddVideoCredits(opts.VideoCredits);

        await users.AddAsync(user, ct);
        await audit.RecordAsync(
            AuditEvent.Create(AuditActor.System("smoke-account-seeder"), "system.seed.user", "user", user.Id,
                AuditResults.Ok, new Dictionary<string, string> { ["role"] = user.Role.ToString() }),
            ct);

        log.LogInformation(
            "Seeded the smoke test account {Email} on the {Tier} plan. It is a pharmacy user, "
            + "not an admin. `pnpm smoke` signs in as this account so nothing it creates lands "
            + "on a real one.",
            opts.Email, opts.Tier);
    }
}
