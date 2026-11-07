using Acme.Domain.Subscriptions;
using Acme.Domain.Users;

namespace Acme.Application.Auth;

/// <summary>
/// Server-side filter for the admin user list. Every field is optional; the
/// repository composes only the ones that are set.
/// </summary>
public sealed record UserQuery(
    string? Search = null,
    UserRole? Role = null,
    bool? IsActive = null,
    SubscriptionTier? Tier = null,
    // Restricts results to users managed by this admin. Set by the service for
    // Admin callers from their own identity — never read from the request.
    string? ManagedByAdminId = null,
    bool UnassignedOnly = false,
    /// <summary>Filter by onboarding completion. Null means either.</summary>
    bool? Onboarded = null,
    UserSort Sort = UserSort.NewestFirst,
    int Skip = 0,
    int Take = 25,
    /// <summary>
    /// Restrict to accounts that <i>are</i> a pharmacy, whatever platform role
    /// they hold.
    /// </summary>
    /// <remarks>
    /// Not the same as <c>Role == PharmacyUser</c>, and the difference is a bug
    /// we shipped. Role answers "what may this person do on the platform";
    /// having a pharmacy answers "what is this account". Promote a pharmacy
    /// owner to Admin so they can help run things and the two diverge — their
    /// role becomes Admin while their pharmacy, its patients and its campaigns
    /// carry on existing. Filtering the Pharmacies page by role made those
    /// accounts vanish from it, which reads as data loss rather than as a
    /// filter doing its job.
    ///
    /// So this matches on the pharmacy itself: role PharmacyUser, or a
    /// non-empty pharmacy name on the profile.
    /// </remarks>
    bool PharmacyAccountsOnly = false,
    /// <summary>Account created at or after this instant (inclusive).</summary>
    DateTime? CreatedFromUtc = null,
    /// <summary>Account created before this instant (exclusive).</summary>
    DateTime? CreatedToUtc = null);

/// <summary>
/// Sort options for the admin user table.
/// </summary>
/// <remarks>
/// A closed enum rather than a free-text field name from the request. A caller
/// -supplied sort string would have to be mapped to a document field anyway,
/// and an unmapped value either throws or silently sorts by nothing — this
/// makes the valid set explicit and keeps the Mongo projection total.
/// </remarks>
public enum UserSort
{
    NewestFirst = 0,
    OldestFirst = 1,
    NameAsc = 2,
    NameDesc = 3,
    EmailAsc = 4,
    EmailDesc = 5,
    /// <summary>Most recently signed in first. Never-signed-in sort last.</summary>
    LastLoginDesc = 6,
    TierDesc = 7,
}

public sealed record UserPage(IReadOnlyList<User> Items, long Total);

/// <summary>Counts for the admin dashboard, computed database-side.</summary>
public sealed record UserStats(
    long Total,
    long Active,
    long Inactive,
    long SuperAdmins,
    long Admins,
    long PharmacyUsers,
    long FreeTier,
    long StarterTier,
    long ProTier,
    long NewThisMonth,
    long ActiveLast30Days,
    /// <summary>
    /// Accounts that own a pharmacy, whatever role they hold.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same number as <see cref="PharmacyUsers"/>, and
    /// kept alongside it rather than replacing it. PharmacyUsers is a role
    /// count and is what makes the role breakdown add up to Total; this is a
    /// business count and is what the Pharmacies page lists. They differ by
    /// exactly the number of pharmacy owners who have been given a staff role,
    /// which is a real thing worth being able to see rather than a discrepancy
    /// to paper over.
    /// </remarks>
    long PharmacyAccounts);

public interface IUserRepository
{
    Task<User?> FindByEmailAsync(Email email, CancellationToken ct);
    Task<User?> FindByIdAsync(string id, CancellationToken ct);

    /// <summary>
    /// Resolves the user behind a Stripe webhook. Stripe events carry the
    /// customer id, not ours, so this is the only join back to our own
    /// identity — subscription.updated and subscription.deleted have no
    /// client_reference_id to fall back on.
    /// </summary>
    Task<User?> FindByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken ct);

    /* ---------------- Admin queries ---------------- */

    Task<UserPage> SearchAsync(UserQuery query, CancellationToken ct);

    /// <summary>Everyone assigned to a given admin. Used when demoting or deleting them.</summary>
    Task<IReadOnlyList<User>> ListManagedByAsync(string adminId, CancellationToken ct);

    Task<IReadOnlyList<User>> ListByRoleAsync(UserRole role, CancellationToken ct);

    /// <summary>Every pharmacy sitting directly under this one.</summary>
    /// <remarks>
    /// One level, matching the domain rule — there is no recursive variant
    /// because a child cannot have children.
    /// </remarks>
    Task<IReadOnlyList<User>> ListChildPharmaciesAsync(string parentId, CancellationToken ct);

    Task<UserStats> GetStatsAsync(CancellationToken ct);

    /// <summary>
    /// How many pharmacy users each admin manages, keyed by admin id. One
    /// aggregation rather than a count query per admin — the admins list would
    /// otherwise be N+1.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> CountManagedPerAdminAsync(CancellationToken ct);

    Task<bool> AnyWithRoleAsync(UserRole role, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    Task UpdateAsync(User user, CancellationToken ct);
}
