using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Hierarchy;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Gives every account that predates memberships the memberships it implies.
/// </summary>
/// <remarks>
/// <para>
/// Two rows per relationship that already exists in the data:
/// </para>
/// <list type="bullet">
///   <item>
///     every user gets a <b>self-membership</b>, so "the pharmacies I can work
///     as" is a query rather than a query plus a special case;
///   </item>
///   <item>
///     every pharmacy with children gets <b>HeadOfficeAdmin</b> over each of
///     them, so the manage-as relationships that work today keep working
///     through the new model instead of beside it.
///   </item>
/// </list>
/// <para>
/// <b>Idempotent by unique index.</b> The pair (userId, pharmacyId) is unique,
/// inserts are unordered, and duplicates are expected to bounce — so this can
/// run on every boot without checking first, and two instances starting
/// together cannot produce a double grant.
/// </para>
/// <para>
/// It runs on every environment including production, because an account with
/// no self-membership cannot switch to its own pharmacy — which is to say it
/// cannot use the product. This is not optional data.
/// </para>
/// </remarks>
public sealed class MembershipBackfill(
    IUserRepository users,
    IPharmacyMembershipRepository memberships,
    ILogger<MembershipBackfill> log) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var all = await users.SearchAsync(
            new UserQuery(Skip: 0, Take: int.MaxValue), ct);

        if (all.Items.Count == 0) return;

        var existing = await memberships.CountAsync(ct);

        // Every pair that already exists, read once.
        //
        // The obvious shape — FindAsync per user — is two round trips per
        // account, so a few thousand accounts would be several thousand queries
        // before the API accepts its first request. One pass over each user's
        // memberships is enough, and the set is small: a row per pharmacy a
        // person can reach.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in all.Items)
            foreach (var m in await memberships.ListForUserAsync(user.Id, ct))
                seen.Add(Pair(m.UserId, m.PharmacyId));

        var toGrant = new List<PharmacyMembership>();

        // Pharmacies that already run branches, and so must keep being able to.
        //
        // Head office became a granted status, and a grant nobody has yet is a
        // grant everybody lacks: without this, every existing group would lose
        // the ability to add a branch the moment the gate shipped, and would
        // have to ring support to get back a thing they already had.
        var runsBranches = all.Items
            .Where(u => !string.IsNullOrWhiteSpace(u.ParentPharmacyId))
            .Select(u => u.ParentPharmacyId!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var user in all.Items)
        {
            if (runsBranches.Contains(user.Id) && !user.IsHeadOffice)
            {
                user.GrantHeadOffice();
                await users.UpdateAsync(user, ct);
                log.LogInformation(
                    "Granted head office to {Pharmacy} — it already has sub-pharmacies.",
                    user.Id);
            }

            // Self-membership for everyone, staff included. A platform admin is
            // still an account that can own a poster.
            if (seen.Add(Pair(user.Id, user.Id)))
                toGrant.Add(PharmacyMembership.Self(user.Id));

            // A child's parent becomes a head office admin of it. Reading the
            // relationship off the child avoids a second query per parent.
            if (!string.IsNullOrWhiteSpace(user.ParentPharmacyId)
                && seen.Add(Pair(user.ParentPharmacyId!, user.Id)))
            {
                toGrant.Add(PharmacyMembership.Grant(
                    userId: user.ParentPharmacyId!,
                    pharmacyId: user.Id,
                    role: PharmacyMembershipRole.HeadOfficeAdmin,
                    grantedBy: PharmacyMembership.System));
            }
        }

        if (toGrant.Count == 0)
        {
            log.LogInformation(
                "Memberships are up to date ({Existing} row(s), {Users} account(s)).",
                existing, all.Items.Count);
            return;
        }

        try
        {
            await memberships.AddManyAsync(toGrant, ct);
        }
        catch (Exception ex)
        {
            // A duplicate-key error here is the unique index doing its job when
            // two instances boot together. Logged rather than thrown: the rows
            // that mattered are in, and failing startup over a race would take
            // the API down for a problem that has already resolved itself.
            log.LogWarning(ex,
                "Some memberships were not inserted — most likely already present.");
        }

        log.LogInformation(
            "Backfilled {Count} membership(s) across {Users} account(s).",
            toGrant.Count, all.Items.Count);
    }

    /// <summary>A comparable key for one (person, pharmacy) pair.</summary>
    private static string Pair(string userId, string pharmacyId) => $"{userId}|{pharmacyId}";
}
