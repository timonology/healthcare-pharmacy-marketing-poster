using Acme.Domain.Patients;

namespace Acme.Application.Patients;

public interface IPatientGroupRepository
{
    Task<IReadOnlyList<PatientGroup>> ListByOwnerAsync(string ownerId, CancellationToken ct);
    Task<PatientGroup?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(PatientGroup group, CancellationToken ct);
    Task UpdateAsync(PatientGroup group, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
