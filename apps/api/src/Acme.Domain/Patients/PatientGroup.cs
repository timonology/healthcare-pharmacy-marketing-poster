using Acme.Domain.Common;

namespace Acme.Domain.Patients;

public sealed class PatientGroup : Entity
{
    /// <summary>
    /// The pharmacy that owns this group. Empty for a global group.
    /// </summary>
    /// <remarks>
    /// A global group belongs to the platform, not to a pharmacy, so there is
    /// nobody to put here. Empty rather than null keeps every existing
    /// ownership comparison working unchanged — <c>g.OwnerId != ownerId</c>
    /// stays false for a real pharmacy id, which is exactly the behaviour the
    /// private-group paths already rely on.
    /// </remarks>
    public string OwnerId { get; private set; } = default!;

    public GroupScope Scope { get; private set; } = GroupScope.Private;

    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Denormalised member count. <b>Meaningful for private groups only.</b>
    /// </summary>
    /// <remarks>
    /// A global group has a different number of members for every pharmacy —
    /// each adds their own patients — so a single stored figure cannot be
    /// right for anyone. Callers must compute a per-pharmacy count from the
    /// patients themselves; <see cref="PatientCountFor"/> exists to make that
    /// impossible to forget.
    /// </remarks>
    public int PatientCount { get; private set; }

    /// <summary>True when every pharmacy can see and use this group.</summary>
    public bool IsGlobal => Scope == GroupScope.Global;

    private PatientGroup() { }

    public static PatientGroup Create(string ownerId, string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        Validate(name, description);

        return new PatientGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Scope = GroupScope.Private,
            Name = name.Trim(),
            Description = (description ?? string.Empty).Trim(),
            PatientCount = 0,
        };
    }

    /// <summary>
    /// Creates a group every pharmacy can see. SuperAdmin only — enforced in
    /// the service, since authorisation is not the domain's job.
    /// </summary>
    public static PatientGroup CreateGlobal(string name, string? description)
    {
        Validate(name, description);

        return new PatientGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = string.Empty,
            Scope = GroupScope.Global,
            Name = name.Trim(),
            Description = (description ?? string.Empty).Trim(),
            PatientCount = 0,
        };
    }

    /// <summary>
    /// Whether <paramref name="pharmacyId"/> may add and remove members.
    /// </summary>
    /// <remarks>
    /// Every pharmacy may curate a global group's membership with their own
    /// patients — that is the point of it. Editing the group itself is a
    /// separate question, answered by <see cref="CanBeEditedBy"/>.
    /// </remarks>
    public bool AllowsMembershipFrom(string pharmacyId) =>
        IsGlobal || OwnerId == pharmacyId;

    /// <summary>
    /// Whether the name and description may be changed.
    /// </summary>
    /// <remarks>
    /// A global group's definition is shared, so one pharmacy renaming it
    /// would rename it for everyone. Only a SuperAdmin can, and the caller
    /// passes that in rather than the domain reaching for a role it cannot see.
    /// </remarks>
    public bool CanBeEditedBy(string pharmacyId, bool isSuperAdmin) =>
        IsGlobal ? isSuperAdmin : OwnerId == pharmacyId;

    public void Update(string name, string? description)
    {
        Validate(name, description);
        Name = name.Trim();
        Description = (description ?? string.Empty).Trim();
        Touch();
    }

    public void SetPatientCount(int count)
    {
        if (count < 0) count = 0;
        PatientCount = count;
        Touch();
    }

    private static void Validate(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Group name is required.");
        if (name.Length > 120)
            throw new DomainException("Group name must be 120 chars or fewer.");
        if ((description ?? string.Empty).Length > 1000)
            throw new DomainException("Description must be 1000 chars or fewer.");
    }
}
