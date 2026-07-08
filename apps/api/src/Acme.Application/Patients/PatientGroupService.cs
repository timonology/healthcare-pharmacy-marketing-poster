using Acme.Application.Common;
using Acme.Domain.Common;
using Acme.Domain.Patients;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Patients;

public sealed class PatientGroupService(
    IPatientGroupRepository groups,
    IPatientRepository patients,
    ILogger<PatientGroupService> logger)
{
    public async Task<IReadOnlyList<PatientGroupDto>> ListAsync(string ownerId, CancellationToken ct)
    {
        var items = await groups.ListByOwnerAsync(ownerId, ct);
        logger.LogInformation("Patient groups list for owner={OwnerId} returned {Count}", ownerId, items.Count);
        return items.Select(ToDto).ToList();
    }

    public async Task<Result<PatientGroupDto>> GetAsync(string id, string ownerId, CancellationToken ct)
    {
        var g = await groups.FindByIdAsync(id, ct);
        if (g is null || g.OwnerId != ownerId)
            return Result<PatientGroupDto>.NotFound("Group not found.");
        return Result<PatientGroupDto>.Success(ToDto(g));
    }

    public async Task<Result<PatientGroupDto>> CreateAsync(
        string ownerId,
        UpsertPatientGroupRequest request,
        CancellationToken ct)
    {
        PatientGroup group;
        try { group = PatientGroup.Create(ownerId, request.Name, request.Description); }
        catch (DomainException ex) { return Result<PatientGroupDto>.Invalid(ex.Message); }

        await groups.AddAsync(group, ct);
        logger.LogInformation("Patient group created id={GroupId} owner={OwnerId} name='{Name}'",
            group.Id, group.OwnerId, group.Name);
        return Result<PatientGroupDto>.Success(ToDto(group));
    }

    public async Task<Result<PatientGroupDto>> UpdateAsync(
        string id,
        string ownerId,
        UpsertPatientGroupRequest request,
        CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(id, ct);
        if (group is null || group.OwnerId != ownerId)
            return Result<PatientGroupDto>.NotFound("Group not found.");

        try { group.Update(request.Name, request.Description); }
        catch (DomainException ex) { return Result<PatientGroupDto>.Invalid(ex.Message); }

        await groups.UpdateAsync(group, ct);
        return Result<PatientGroupDto>.Success(ToDto(group));
    }

    public async Task<Result<bool>> DeleteAsync(string id, string ownerId, CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(id, ct);
        if (group is null || group.OwnerId != ownerId)
            return Result<bool>.NotFound("Group not found.");

        // Detach patients from the group before deletion.
        var members = await patients.ListByGroupAsync(ownerId, id, ct);
        foreach (var patient in members)
        {
            patient.RemoveFromGroup(id);
            await patients.UpdateAsync(patient, ct);
        }

        await groups.DeleteAsync(id, ct);
        return Result<bool>.Success(true);
    }

    public async Task<Result<IReadOnlyList<PatientDto>>> ListMembersAsync(
        string id,
        string ownerId,
        CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(id, ct);
        if (group is null || group.OwnerId != ownerId)
            return Result<IReadOnlyList<PatientDto>>.NotFound("Group not found.");

        var members = await patients.ListByGroupAsync(ownerId, id, ct);
        var dtos = (IReadOnlyList<PatientDto>)members.Select(p => new PatientDto(
            p.Id, p.OwnerId, p.FullName, p.Email, p.Phone,
            p.Notes, p.GroupIds, p.CreatedAtUtc, p.UpdatedAtUtc)).ToList();
        return Result<IReadOnlyList<PatientDto>>.Success(dtos);
    }

    private static PatientGroupDto ToDto(PatientGroup g) => new(
        g.Id,
        g.OwnerId,
        g.Name,
        g.Description,
        g.PatientCount,
        g.CreatedAtUtc,
        g.UpdatedAtUtc);
}
