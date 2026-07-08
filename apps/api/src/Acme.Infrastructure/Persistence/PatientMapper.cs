using System.Reflection;
using Acme.Domain.Patients;

namespace Acme.Infrastructure.Persistence;

internal static class PatientMapper
{
    private static readonly ConstructorInfo PatientCtor = typeof(Patient)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("Patient parameterless ctor missing.");

    private static readonly ConstructorInfo GroupCtor = typeof(PatientGroup)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("PatientGroup parameterless ctor missing.");

    public static Patient Hydrate(PatientDocument d)
    {
        var p = (Patient)PatientCtor.Invoke(null);
        Set(p, nameof(Patient.Id), d.Id);
        Set(p, nameof(Patient.OwnerId), d.OwnerId);
        Set(p, nameof(Patient.FullName), d.FullName);
        Set(p, nameof(Patient.Email), d.Email);
        Set(p, nameof(Patient.Phone), d.Phone);
        Set(p, nameof(Patient.Notes), d.Notes ?? string.Empty);
        Set(p, nameof(Patient.GroupIds), (IReadOnlyList<string>)(d.GroupIds ?? new()).ToList());
        Set(p, nameof(Patient.CreatedAtUtc), d.CreatedAtUtc);
        Set(p, nameof(Patient.UpdatedAtUtc), d.UpdatedAtUtc);
        return p;
    }

    public static PatientDocument ToDocument(Patient p) => new()
    {
        Id = p.Id,
        OwnerId = p.OwnerId,
        FullName = p.FullName,
        Email = p.Email,
        Phone = p.Phone,
        Notes = p.Notes,
        GroupIds = p.GroupIds.ToList(),
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc,
    };

    public static PatientGroup HydrateGroup(PatientGroupDocument d)
    {
        var g = (PatientGroup)GroupCtor.Invoke(null);
        Set(g, nameof(PatientGroup.Id), d.Id);
        Set(g, nameof(PatientGroup.OwnerId), d.OwnerId);
        Set(g, nameof(PatientGroup.Name), d.Name);
        Set(g, nameof(PatientGroup.Description), d.Description ?? string.Empty);
        Set(g, nameof(PatientGroup.PatientCount), d.PatientCount);
        Set(g, nameof(PatientGroup.CreatedAtUtc), d.CreatedAtUtc);
        Set(g, nameof(PatientGroup.UpdatedAtUtc), d.UpdatedAtUtc);
        return g;
    }

    public static PatientGroupDocument ToDocument(PatientGroup g) => new()
    {
        Id = g.Id,
        OwnerId = g.OwnerId,
        Name = g.Name,
        Description = g.Description,
        PatientCount = g.PatientCount,
        CreatedAtUtc = g.CreatedAtUtc,
        UpdatedAtUtc = g.UpdatedAtUtc,
    };

    private static void Set(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}
