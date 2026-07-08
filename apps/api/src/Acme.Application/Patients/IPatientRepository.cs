using Acme.Domain.Patients;

namespace Acme.Application.Patients;

public sealed record PatientQuery(
    string OwnerId,
    string? Search = null,
    string? GroupId = null,
    int Skip = 0,
    int Take = 50);

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
}
