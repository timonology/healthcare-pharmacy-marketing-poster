using Acme.Application.Common;
using Acme.Domain.Patients;

namespace Acme.Application.Patients;

public sealed record PatientQuery(
    string OwnerId,
    string? Search = null,
    string? GroupId = null,
    int Skip = 0,
    int Take = 50,
    /// <summary>Sort and range on <c>CreatedAtUtc</c>. Null: alphabetical by name, all time.</summary>
    ListWindow? Window = null);

public interface IPatientRepository
{
    Task<IReadOnlyList<Patient>> ListAsync(PatientQuery query, CancellationToken ct);
    Task<int> CountAsync(PatientQuery query, CancellationToken ct);
    Task<Patient?> FindByIdAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<Patient>> FindManyByIdsAsync(string ownerId, IReadOnlyList<string> ids, CancellationToken ct);
    Task<IReadOnlyList<Patient>> ListByGroupAsync(string ownerId, string groupId, CancellationToken ct);
    Task AddAsync(Patient patient, CancellationToken ct);
    Task AddManyAsync(IEnumerable<Patient> patients, CancellationToken ct);
    Task UpdateAsync(Patient patient, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task DeleteManyAsync(string ownerId, IReadOnlyList<string> ids, CancellationToken ct);
    Task<int> CountByOwnerAsync(string ownerId, CancellationToken ct);

    /// <summary>
    /// How many of this pharmacy's patients are in each group, keyed by group id.
    /// </summary>
    /// <remarks>
    /// One aggregation rather than a count per group. It exists because a
    /// global group's size differs for every pharmacy — each adds their own
    /// patients — so the stored counter on the group cannot be right for
    /// anyone, and the alternative is a query per group on a page that lists
    /// all of them.
    ///
    /// Groups with no members are absent from the result rather than zero;
    /// callers should default.
    /// </remarks>
    Task<IReadOnlyDictionary<string, int>> CountsByGroupAsync(string ownerId, CancellationToken ct);

    /// <summary>
    /// Removes a group from every patient on the platform, across all pharmacies.
    /// </summary>
    /// <remarks>
    /// Only for deleting a global group, and deliberately the only cross-owner
    /// write in this interface. A global group can be referenced by patients
    /// belonging to hundreds of pharmacies, so the owner-scoped detach the
    /// private path uses would leave every other pharmacy's patients pointing
    /// at a group that no longer exists.
    /// </remarks>
    Task<long> RemoveGroupFromAllPatientsAsync(string groupId, CancellationToken ct);

    /// <summary>
    /// How many patients are in a group across the whole platform.
    /// </summary>
    /// <remarks>
    /// For the admin console only. This number must never reach a pharmacy —
    /// it would tell them how many patients their competitors hold.
    /// </remarks>
    Task<long> CountInGroupAcrossPlatformAsync(string groupId, CancellationToken ct);
}
