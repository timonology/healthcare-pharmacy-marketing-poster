using Acme.Domain.Users;

namespace Acme.Application.Hierarchy;

/// <summary>Who may act as which pharmacy.</summary>
public interface IPharmacyMembershipRepository
{
    /// <summary>Every pharmacy this person may act as, including their own.</summary>
    Task<IReadOnlyList<PharmacyMembership>> ListForUserAsync(string userId, CancellationToken ct);

    /// <summary>Everyone who may act as this pharmacy.</summary>
    Task<IReadOnlyList<PharmacyMembership>> ListForPharmacyAsync(string pharmacyId, CancellationToken ct);

    /// <summary>
    /// One membership, or null.
    /// </summary>
    /// <remarks>
    /// The authorisation primitive. Every switch and every refresh resolves
    /// through this, so it is a point read on an indexed pair rather than a
    /// scan — it runs on requests an unauthenticated caller can trigger.
    /// </remarks>
    Task<PharmacyMembership?> FindAsync(string userId, string pharmacyId, CancellationToken ct);

    Task AddAsync(PharmacyMembership membership, CancellationToken ct);

    Task UpdateAsync(PharmacyMembership membership, CancellationToken ct);

    Task RemoveAsync(string id, CancellationToken ct);

    /// <summary>How many memberships exist at all. Used by the backfill to decide whether to run.</summary>
    Task<long> CountAsync(CancellationToken ct);

    /// <summary>Inserts many at once. Used only by the backfill.</summary>
    Task AddManyAsync(IEnumerable<PharmacyMembership> memberships, CancellationToken ct);
}
