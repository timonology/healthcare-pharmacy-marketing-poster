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
    /// <summary>
    /// Every group this pharmacy can use: their own, plus every global one.
    /// </summary>
    /// <remarks>
    /// Counts are computed per pharmacy rather than read from the group. A
    /// global group has a different number of members for every pharmacy, so
    /// the stored counter cannot be right for anyone — and showing another
    /// pharmacy's total would leak their patient volume to a competitor.
    /// </remarks>
    public async Task<IReadOnlyList<PatientGroupDto>> ListAsync(string ownerId, CancellationToken ct)
    {
        var items = await groups.ListVisibleToAsync(ownerId, ct);
        var counts = await patients.CountsByGroupAsync(ownerId, ct);

        logger.LogInformation(
            "Patient groups for owner={OwnerId}: {Total} visible ({Global} global)",
            ownerId, items.Count, items.Count(g => g.IsGlobal));

        return items.Select(g => ToDto(g, counts.GetValueOrDefault(g.Id))).ToList();
    }

    public async Task<Result<PatientGroupDto>> GetAsync(string id, string ownerId, CancellationToken ct)
    {
        var g = await groups.FindByIdAsync(id, ct);

        // AllowsMembershipFrom, not an ownership test: a global group is
        // visible to every pharmacy even though none of them owns it.
        if (g is null || !g.AllowsMembershipFrom(ownerId))
            return Result<PatientGroupDto>.NotFound("Group not found.");

        var counts = await patients.CountsByGroupAsync(ownerId, ct);
        return Result<PatientGroupDto>.Success(ToDto(g, counts.GetValueOrDefault(g.Id)));
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
        return Result<PatientGroupDto>.Success(ToDto(group, 0));
    }

    public async Task<Result<PatientGroupDto>> UpdateAsync(
        string id,
        string ownerId,
        UpsertPatientGroupRequest request,
        CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(id, ct);
        if (group is null || !group.AllowsMembershipFrom(ownerId))
            return Result<PatientGroupDto>.NotFound("Group not found.");

        // Visible is not editable. Renaming a shared group would rename it for
        // every pharmacy on the platform, so a pharmacy is told plainly rather
        // than given a 404 that looks like the group vanished.
        if (!group.CanBeEditedBy(ownerId, isSuperAdmin: false))
            return Result<PatientGroupDto>.Forbidden(
                "This is a general group shared across the platform. You can add and remove your own "
                + "patients, but only an administrator can rename it.");

        try { group.Update(request.Name, request.Description); }
        catch (DomainException ex) { return Result<PatientGroupDto>.Invalid(ex.Message); }

        await groups.UpdateAsync(group, ct);
        var counts = await patients.CountsByGroupAsync(ownerId, ct);
        return Result<PatientGroupDto>.Success(ToDto(group, counts.GetValueOrDefault(group.Id)));
    }

    public async Task<Result<bool>> DeleteAsync(string id, string ownerId, CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(id, ct);
        if (group is null || !group.AllowsMembershipFrom(ownerId))
            return Result<bool>.NotFound("Group not found.");

        if (!group.CanBeEditedBy(ownerId, isSuperAdmin: false))
            return Result<bool>.Forbidden(
                "This is a general group shared across the platform and cannot be deleted here. "
                + "Remove your patients from it instead.");

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
        if (group is null || !group.AllowsMembershipFrom(ownerId))
            return Result<IReadOnlyList<PatientDto>>.NotFound("Group not found.");

        var members = await patients.ListByGroupAsync(ownerId, id, ct);
        var dtos = (IReadOnlyList<PatientDto>)members.Select(p => new PatientDto(
            p.Id, p.OwnerId, p.FullName, p.Email, p.Phone,
            p.Notes, p.GroupIds, p.CreatedAtUtc, p.UpdatedAtUtc)).ToList();
        return Result<IReadOnlyList<PatientDto>>.Success(dtos);
    }

    /// <summary>
    /// Maps a group for one pharmacy's view of it.
    /// </summary>
    /// <param name="patientCount">
    /// <b>This pharmacy's</b> members, not the group's stored counter. Passing
    /// it in rather than reading <c>g.PatientCount</c> is what stops a global
    /// group reporting somebody else's total.
    /// </param>
    private static PatientGroupDto ToDto(PatientGroup g, int patientCount) => new(
        g.Id,
        g.OwnerId,
        g.Name,
        g.Description,
        patientCount,
        g.IsGlobal,
        // A pharmacy may never edit a global group. The flag drives the UI;
        // the service refuses regardless of what the UI renders.
        CanEdit: !g.IsGlobal,
        g.CreatedAtUtc,
        g.UpdatedAtUtc);
}
