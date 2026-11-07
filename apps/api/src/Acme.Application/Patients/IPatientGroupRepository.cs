using Acme.Domain.Patients;

namespace Acme.Application.Patients;

public interface IPatientGroupRepository
{
    /// <summary>
    /// A pharmacy's own groups. Private only — global groups are not included.
    /// </summary>
    /// <remarks>
    /// Left as-is on purpose. Several callers use this to mean "groups this
    /// pharmacy owns", and quietly widening it to include global groups would
    /// have changed what every one of them counts — including the admin
    /// support view's group tab and the campaign audience preview.
    /// </remarks>
    Task<IReadOnlyList<PatientGroup>> ListByOwnerAsync(string ownerId, CancellationToken ct);

    /// <summary>Every group defined by a SuperAdmin, visible to all pharmacies.</summary>
    Task<IReadOnlyList<PatientGroup>> ListGlobalAsync(CancellationToken ct);

    /// <summary>
    /// Everything a pharmacy may use: their own plus every global group.
    /// </summary>
    /// <remarks>
    /// The list a pharmacy actually sees, and the set any group id from that
    /// pharmacy must be validated against.
    /// </remarks>
    Task<IReadOnlyList<PatientGroup>> ListVisibleToAsync(string pharmacyId, CancellationToken ct);
    Task<PatientGroup?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(PatientGroup group, CancellationToken ct);
    Task UpdateAsync(PatientGroup group, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}
