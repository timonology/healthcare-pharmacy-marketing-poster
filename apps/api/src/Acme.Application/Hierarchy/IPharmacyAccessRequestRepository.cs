using Acme.Domain.Users;

namespace Acme.Application.Hierarchy;

/// <summary>Requests to be given access to a pharmacy.</summary>
public interface IPharmacyAccessRequestRepository
{
    Task AddAsync(PharmacyAccessRequest request, CancellationToken ct);

    Task UpdateAsync(PharmacyAccessRequest request, CancellationToken ct);

    Task<PharmacyAccessRequest?> FindByIdAsync(string id, CancellationToken ct);

    /// <summary>Everything this person has asked for, newest first.</summary>
    Task<IReadOnlyList<PharmacyAccessRequest>> ListForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// One person's outstanding request for one pharmacy, if any.
    /// </summary>
    /// <remarks>
    /// Used to refuse a second request while the first is undecided — otherwise
    /// somebody clicking twice fills the review queue with duplicates of the
    /// same question.
    /// </remarks>
    Task<PharmacyAccessRequest?> FindPendingAsync(
        string userId, string pharmacyId, CancellationToken ct);

    /// <summary>The review queue. Pending first, then recently decided.</summary>
    Task<IReadOnlyList<PharmacyAccessRequest>> ListForReviewAsync(
        bool pendingOnly, int take, CancellationToken ct);
}
