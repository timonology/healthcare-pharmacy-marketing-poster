using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Seeds a spread of demonstration accounts so the admin console has something
/// to exercise on a fresh database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never runs in production.</b> Development seeds by default; Staging seeds
/// only when <c>DevData:SeedUsers</c> is explicitly true, which is how the QA
/// environment gets demo data without being run as Development. Anywhere else
/// the flag is inert, so a seeder that creates known-credential accounts is not
/// one config mistake away from running against real pharmacies.
/// </para>
/// <para>
/// Outside Development it also inspects the database first and refuses if it
/// finds pharmacy accounts it did not create — the signature of an environment
/// wired to the wrong connection string.
/// </para>
/// <para>
/// The accounts are deliberately varied rather than uniform — different tiers,
/// some deactivated, some mid-onboarding, some with no account manager, some
/// that have never signed in. Twelve identical rows would make every filter,
/// sort and bulk action look like it works.
/// </para>
/// <para>
/// Idempotent by email: an account that already exists is left completely
/// alone, so restarting never overwrites changes made while testing.
/// </para>
/// </remarks>
public sealed class DevUserSeeder(
    IUserRepository users,
    IPasswordHasher hasher,
    IConfiguration config,
    IHostEnvironment env,
    IAuditLogger audit,
    ILogger<DevUserSeeder> log) : IStartupTask
{
    /// <summary>Shared password for every seeded account. Test data only.</summary>
    /// <remarks>
    /// The local default, and the one the browser and e2e suites sign in with —
    /// changing it breaks them, so it stays.
    /// </remarks>
    private const string Password = "TestPass123!";

    /// <summary>
    /// The password the demo accounts are actually created with.
    /// </summary>
    /// <remarks>
    /// Overridable through <c>DevData:Password</c> so a shared QA environment
    /// on a public URL is not protected by a credential published in this
    /// repository. Local development keeps the constant and needs no config.
    /// </remarks>
    private string SeedPassword =>
        config.GetValue<string?>("DevData:Password") is { Length: > 0 } custom
            ? custom
            : Password;

    private const string Domain = "example.test";

    /// <summary>
    /// Whether demo accounts may be created in this environment at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Development seeds by default, as it always has. <b>Staging seeds only on
    /// an explicit opt-in</b>, which is what makes a shared QA environment
    /// possible without running it as Development — Development also turns on
    /// the diagnostics endpoints and developer exception pages, and this
    /// environment is reachable from the internet.
    /// </para>
    /// <para>
    /// Production is not a case here, deliberately. <c>DevData:SeedUsers</c>
    /// has <i>no power</i> outside Development and Staging, so setting it by
    /// accident — or copying a whole app-settings block between environments —
    /// cannot create known-password accounts in production.
    /// </para>
    /// </remarks>
    private bool SeedingAllowed()
    {
        if (env.IsDevelopment())
            return config.GetValue("DevData:SeedUsers", true);

        if (env.IsStaging())
            return config.GetValue("DevData:SeedUsers", false);

        return false;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!SeedingAllowed())
        {
            if (env.IsStaging())
                log.LogInformation(
                    "DevData:SeedUsers is not true — skipping demo accounts in Staging.");
            return;
        }

        // The guardrail that matters when two environments share a template.
        //
        // The realistic accident is not someone enabling this in production; it
        // is a Staging App Service left pointing at the production connection
        // string. That looks like a working deployment — until this seeder
        // scatters accounts whose password is written down in a QA document
        // across a database of real pharmacies.
        //
        // So outside Development, look at what is already there. A database
        // holding pharmacies we did not create is not ours to seed.
        if (!env.IsDevelopment())
        {
            var existing = await users.ListByRoleAsync(UserRole.PharmacyUser, ct);
            var strangers = existing
                .Where(u => !u.Email.Value.EndsWith($"@{Domain}", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (strangers.Count > 0)
            {
                log.LogCritical(
                    "Refusing to seed demo accounts: this database already holds {Count} " +
                    "pharmacy account(s) that are not demo data (for example {Sample}). " +
                    "That almost certainly means this environment is pointed at the wrong " +
                    "database — check Mongo__ConnectionString. No accounts were created.",
                    strangers.Count,
                    strangers[0].Email.Value);
                return;
            }
        }

        var hash = PasswordHash.FromHash(hasher.Hash(SeedPassword));

        // Admins first: the pharmacy accounts below reference them by index,
        // and an account manager has to exist before anyone is assigned to it.
        var admins = new List<User>();
        foreach (var (email, name) in DemoAdmins)
        {
            var created = await EnsureAsync(email, () =>
                User.CreateStaff(Email.Create(email), name, hash, UserRole.Admin), ct);
            if (created is not null) admins.Add(created);
        }

        var seeded = 0;
        foreach (var spec in DemoPharmacies)
        {
            var user = await EnsureAsync(spec.Email, () =>
            {
                var u = User.Register(Email.Create(spec.Email), spec.ContactName, hash);

                // Onboarding is a property of the profile, and Create() always
                // marks it complete — so the "still onboarding" accounts are
                // built through the record constructor instead.
                u.SetProfile(spec.Onboarded
                    ? PharmacyProfile.Create(
                        spec.PharmacyName, spec.Address, spec.PostCode,
                        spec.Description, spec.ContactName, spec.Phone, null)
                    : new PharmacyProfile(
                        spec.PharmacyName, spec.Address, spec.PostCode,
                        spec.Description, spec.ContactName, spec.Phone,
                        SonarFCode: null, OnboardingCompleted: false));

                if (spec.Tier != SubscriptionTier.Free) u.ChangeTier(spec.Tier);
                if (spec.Credits > 0) u.AddVideoCredits(spec.Credits);

                // Only some accounts have ever signed in, so "last sign-in"
                // sorting and the active-in-30-days figure have both cases.
                if (spec.HasSignedIn) u.RecordLogin();

                if (spec.AdminIndex is { } idx && idx < admins.Count)
                    u.AssignToAdmin(admins[idx].Id);

                if (!spec.IsActive) u.Deactivate();

                return u;
            }, ct);

            if (user is not null) seeded++;
        }

        seeded += await SeedBranchesAsync(hash, admins, ct);

        if (seeded > 0 || admins.Count > 0)
        {
            // The password is printed in Development only.
            //
            // It used to be logged unconditionally, and it logged the constant
            // rather than the password actually used — so a Staging environment
            // with DevData:Password set would print a password that does not
            // work, into a log stream several people can read, while leaking
            // the fact that the real one exists. Outside Development the line
            // says where to look instead.
            if (env.IsDevelopment())
            {
                log.LogInformation(
                    "Seeded {Pharmacies} demo pharmacy account(s) and {Admins} admin(s). "
                    + "Password for all of them: {Password}",
                    seeded, admins.Count, SeedPassword);
            }
            else
            {
                log.LogInformation(
                    "Seeded {Pharmacies} demo pharmacy account(s) and {Admins} admin(s). "
                    + "The shared password is whatever DevData:Password is set to "
                    + "on this app service.",
                    seeded, admins.Count);
            }
        }
    }

    /// <summary>
    /// Makes Riverside a head office with three branches under it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The twelve demo pharmacies above are all peers, which left the whole of
    /// head office — the grant, the branch list, the branch detail page, the
    /// audited read, switching between branches — with no data to run on. The
    /// browser suite skipped every one of those tests rather than failing, so
    /// the feature looked covered and was not exercised at all.
    /// </para>
    /// <para>
    /// Branches are seeded as ordinary accounts and then attached, rather than
    /// through <c>CreateChildAsync</c>. That path exists to invite a real
    /// person: it generates a credential and emails it. A seeder that sent
    /// three emails on every boot of every developer's machine would be a
    /// surprising thing for a seeder to do.
    /// </para>
    /// <para>
    /// Memberships are not created here. <see cref="MembershipBackfill"/> runs
    /// after this task on the same boot and derives head-office membership from
    /// <c>ParentPharmacyId</c>, so the branches are switchable immediately and
    /// there is only one piece of code that knows how that rule works.
    /// </para>
    /// </remarks>
    private async Task<int> SeedBranchesAsync(
        PasswordHash hash, IReadOnlyList<User> admins, CancellationToken ct)
    {
        var parent = await users.FindByEmailAsync(Email.Create(HeadOfficeEmail), ct);

        // Riverside is seeded immediately above, so this is only null if
        // somebody removed it from DemoPharmacies. Say so rather than seeding
        // three orphans that look like ordinary pharmacies.
        if (parent is null)
        {
            log.LogWarning(
                "No {Email} account, so no demo branches were created. The head office "
                + "screens will have nothing to show.", HeadOfficeEmail);
            return 0;
        }

        if (!parent.IsHeadOffice)
        {
            parent.GrantHeadOffice();
            await users.UpdateAsync(parent, ct);
        }

        // Read out before the loop. Nullable analysis does not carry the
        // not-null state of a captured local into a lambda, so dereferencing
        // `parent` inside one is a CS8602 — and warnings are errors here.
        var parentId = parent.Id;

        var seeded = 0;
        foreach (var spec in DemoBranches)
        {
            var child = await EnsureAsync(spec.Email, () =>
            {
                var u = User.Register(Email.Create(spec.Email), spec.ContactName, hash);

                u.SetProfile(spec.Onboarded
                    ? PharmacyProfile.Create(
                        spec.PharmacyName, spec.Address, spec.PostCode,
                        spec.Description, spec.ContactName, spec.Phone, null)
                    : new PharmacyProfile(
                        spec.PharmacyName, spec.Address, spec.PostCode,
                        spec.Description, spec.ContactName, spec.Phone,
                        SonarFCode: null, OnboardingCompleted: false));

                if (spec.Tier != SubscriptionTier.Free) u.ChangeTier(spec.Tier);
                if (spec.Credits > 0) u.AddVideoCredits(spec.Credits);
                if (spec.HasSignedIn) u.RecordLogin();

                if (spec.AdminIndex is { } idx && idx < admins.Count)
                    u.AssignToAdmin(admins[idx].Id);

                if (!spec.IsActive) u.Deactivate();

                // What makes it a branch rather than a thirteenth pharmacy.
                u.SetParent(parentId);

                return u;
            }, ct);

            if (child is not null) seeded++;
        }

        return seeded;
    }

    /// <summary>The demo pharmacy that runs branches.</summary>
    private const string HeadOfficeEmail = $"riverside@{Domain}";

    /// <summary>
    /// Riverside's branches — deliberately not identical to each other.
    /// </summary>
    /// <remarks>
    /// One is on a different plan and one has never signed in, because a head
    /// office looking across its branches is looking for exactly that kind of
    /// difference. Three identical rows would let the branch detail page render
    /// the same number in every card and still look right.
    /// </remarks>
    private static readonly PharmacySpec[] DemoBranches =
    {
        new($"riverside-didsbury@{Domain}", "Riverside Pharmacy — Didsbury", "Leah Byrne",
            "0161 496 0131", "204 Wilmslow Road, Didsbury, Manchester", "M20 2RN",
            "Branch pharmacy beside the Didsbury medical centre.",
            SubscriptionTier.Pro, true, true, true, 10, 0),

        new($"riverside-salford@{Domain}", "Riverside Pharmacy — Salford Quays", "Ade Okonkwo",
            "0161 496 0132", "5 The Quays, Salford", "M50 3AZ",
            "Branch pharmacy serving the Quays development.",
            SubscriptionTier.Starter, true, true, true, 0, 0),

        // Opened on paper, never used. The head office's list should show this
        // plainly — it is the most useful thing a branch list can tell anybody.
        new($"riverside-stockport@{Domain}", "Riverside Pharmacy — Stockport", "Grace Whitfield",
            "0161 496 0133", "17 Great Underbank, Stockport", "SK1 1NE",
            "Newest branch, not yet trading.",
            SubscriptionTier.Free, true, true, false, 0, 0),
    };

    /// <summary>
    /// Creates the account unless the email is taken. Returns the persisted
    /// user, or null when one already existed.
    /// </summary>
    private async Task<User?> EnsureAsync(string email, Func<User> build, CancellationToken ct)
    {
        if (await users.FindByEmailAsync(Email.Create(email), ct) is not null) return null;

        var user = build();
        await users.AddAsync(user, ct);
        await audit.RecordAsync(
            AuditEvent.Create(AuditActor.System("dev-user-seeder"), "system.seed.user", "user", user.Id,
                AuditResults.Ok, new Dictionary<string, string> { ["role"] = user.Role.ToString() }),
            ct);
        return user;
    }

    private static readonly (string Email, string Name)[] DemoAdmins =
    {
        ($"nora.admin@{Domain}", "Nora Whitfield"),
        ($"sam.admin@{Domain}", "Sam Okonkwo"),
    };

    private sealed record PharmacySpec(
        string Email,
        string PharmacyName,
        string ContactName,
        string Phone,
        string Address,
        string PostCode,
        string Description,
        SubscriptionTier Tier,
        bool IsActive,
        bool Onboarded,
        bool HasSignedIn,
        int Credits,
        int? AdminIndex);

    /// <summary>
    /// Twelve independent pharmacies covering every combination the admin pages
    /// filter on. Riverside's branches are seeded separately, below.
    /// </summary>
    private static readonly PharmacySpec[] DemoPharmacies =
    {
        new($"riverside@{Domain}", "Riverside Pharmacy", "Aisha Rahman", "0161 496 0111",
            "12 Riverside Walk, Manchester", "M3 4AB",
            "Independent community pharmacy with a busy travel clinic.",
            SubscriptionTier.Pro, true, true, true, 25, 0),

        new($"oakfield@{Domain}", "Oakfield Chemist", "Daniel Achebe", "0121 496 0112",
            "8 Oakfield Road, Birmingham", "B15 2TT",
            "Family-run chemist serving three GP surgeries.",
            SubscriptionTier.Starter, true, true, true, 5, 0),

        new($"harbour@{Domain}", "Harbour Health Pharmacy", "Megan Lloyd", "029 2018 0113",
            "3 Harbour Parade, Cardiff", "CF10 4PA",
            "Coastal pharmacy with a strong vaccination programme.",
            SubscriptionTier.Pro, true, true, true, 40, 1),

        new($"kingsgate@{Domain}", "Kingsgate Pharmacy", "Tom Fletcher", "0131 496 0114",
            "77 Kingsgate Street, Edinburgh", "EH1 1QS",
            "City-centre pharmacy focused on repeat prescriptions.",
            SubscriptionTier.Starter, true, true, true, 0, 1),

        new($"meadowbrook@{Domain}", "Meadowbrook Pharmacy", "Priya Nair", "0113 496 0115",
            "2 Meadowbrook Lane, Leeds", "LS6 3AA",
            "Suburban pharmacy with a large elderly patient base.",
            SubscriptionTier.Free, true, true, true, 0, null),

        new($"stjames@{Domain}", "St James Dispensary", "Clara Bennett", "0117 496 0116",
            "45 St James Row, Bristol", "BS1 3LU",
            "Dispensary attached to a private clinic.",
            SubscriptionTier.Free, true, true, false, 0, null),

        // Signed up but never finished onboarding — should appear under the
        // "Not onboarded" filter.
        new($"newbridge@{Domain}", "Newbridge Pharmacy", "Owen Griffiths", "01633 496117",
            "", "NP20 1AA", "",
            SubscriptionTier.Free, true, false, true, 0, null),

        new($"lakeside@{Domain}", "Lakeside Chemist", "Hannah Osei", "015394 96118",
            "", "LA22 0AA", "",
            SubscriptionTier.Free, true, false, false, 0, null),

        // Deactivated — exercises the status filter and the reactivate action.
        new($"elmtree@{Domain}", "Elm Tree Pharmacy", "Victor Silva", "0151 496 0119",
            "19 Elm Tree Road, Liverpool", "L1 9BG",
            "Closed for refurbishment.",
            SubscriptionTier.Starter, false, true, true, 3, 0),

        new($"northgate@{Domain}", "Northgate Pharmacy", "Ruth Adeyemi", "0191 496 0120",
            "5 Northgate, Newcastle", "NE1 4XP",
            "Account suspended pending payment.",
            SubscriptionTier.Free, false, true, true, 0, null),

        new($"willowpark@{Domain}", "Willow Park Pharmacy", "Jacob Turner", "0114 496 0121",
            "31 Willow Park Avenue, Sheffield", "S10 2QP",
            "Growing pharmacy trialling video campaigns.",
            SubscriptionTier.Pro, true, true, true, 15, 1),

        new($"castlehill@{Domain}", "Castle Hill Pharmacy", "Fiona Marsh", "01522 496122",
            "9 Castle Hill, Lincoln", "LN1 3AA",
            "Historic pharmacy near the cathedral quarter.",
            SubscriptionTier.Starter, true, true, false, 0, null),
    };
}
