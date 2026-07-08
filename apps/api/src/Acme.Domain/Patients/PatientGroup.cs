using Acme.Domain.Common;

namespace Acme.Domain.Patients;

public sealed class PatientGroup : Entity
{
    public string OwnerId { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public string Description { get; private set; } = string.Empty;
    public int PatientCount { get; private set; }

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
            Name = name.Trim(),
            Description = (description ?? string.Empty).Trim(),
            PatientCount = 0,
        };
    }

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
