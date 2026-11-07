namespace Acme.Domain.Patients;

/// <summary>
/// Who a patient group belongs to.
/// </summary>
/// <remarks>
/// Persisted; do not renumber. Zero is Private so that every group written
/// before global groups existed keeps behaving as one pharmacy's own.
/// </remarks>
public enum GroupScope
{
    /// <summary>
    /// One pharmacy's own group. Invisible to everyone else, and the only kind
    /// a pharmacy can create.
    /// </summary>
    Private = 0,

    /// <summary>
    /// Defined by a SuperAdmin and visible to every pharmacy.
    /// </summary>
    /// <remarks>
    /// Shared in definition only. Membership is not shared: each pharmacy adds
    /// their own patients, sees only their own, and cannot tell whether another
    /// pharmacy uses the group at all. Pharmacies in the same town are
    /// competitors, and a shared member list would leak their patient volumes
    /// to each other.
    /// </remarks>
    Global = 1,
}
