using Acme.Application.Admin;
using Acme.Application.Common;
using Acme.Domain.Admin;
using Acme.Domain.Common;
using Acme.Domain.Patients;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Patients;

/// <param name="PharmaciesUsing">
/// How many pharmacies have put at least one patient in this group. Admin
/// console only — it is a measure of adoption, and neither it nor
/// <c>TotalPatients</c> is ever returned to a pharmacy.
/// </param>
public sealed record GlobalGroupDto(
    string Id,
    string Name,
    string Description,
    long TotalPatients,
    int PharmaciesUsing,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// Global patient groups. SuperAdmin only.
/// </summary>
/// <remarks>
/// <para>
/// A global group is a shared <i>definition</i> — "Diabetes review", "Over 65s"
/// — that every pharmacy can file their own patients under. The definition is
/// shared; the membership is not. Each pharmacy sees only their own patients in
/// it and cannot tell whether anyone else uses it at all.
/// </para>
/// <para>
/// Separate from <see cref="PatientGroupService"/> because the audiences differ
/// completely: that one is pharmacy-facing and owner-scoped, this one is
/// platform-wide and role-gated. Sharing a class would have meant every method
/// carrying both an owner id and a role, and one of them being ignored.
/// </para>
/// </remarks>
public sealed class GlobalGroupService(
    IPatientGroupRepository groups,
    IPatientRepository patients,
    IAdminAuditRepository audit,
    ILogger<GlobalGroupService> log)
{
    public async Task<Result<IReadOnlyList<GlobalGroupDto>>> ListAsync(
        AdminContext actor, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<IReadOnlyList<GlobalGroupDto>>.Forbidden(
                "Only a SuperAdmin can manage general groups.");

        var items = await groups.ListGlobalAsync(ct);
        var result = new List<GlobalGroupDto>(items.Count);

        foreach (var group in items)
            result.Add(await ToDtoAsync(group, ct));

        return Result<IReadOnlyList<GlobalGroupDto>>.Success(result);
    }

    public async Task<Result<GlobalGroupDto>> CreateAsync(
        AdminContext actor, UpsertPatientGroupRequest request, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<GlobalGroupDto>.Forbidden("Only a SuperAdmin can create general groups.");

        // Names are checked for collisions because every pharmacy sees this
        // list. Two groups called "Diabetes review" would be indistinguishable
        // in a dropdown, and patients would end up split between them.
        var existing = await groups.ListGlobalAsync(ct);
        if (existing.Any(g => string.Equals(g.Name, request.Name?.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Result<GlobalGroupDto>.Conflict($"A general group called '{request.Name}' already exists.");

        PatientGroup group;
        try { group = PatientGroup.CreateGlobal(request.Name, request.Description); }
        catch (DomainException ex) { return Result<GlobalGroupDto>.Invalid(ex.Message); }

        await groups.AddAsync(group, ct);

        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId, actor.Email, AdminAction.ManageGlobalGroup,
                null, null, $"Created general group '{group.Name}'"),
            ct);

        log.LogInformation("SuperAdmin {Actor} created global group '{Name}'", PersonalData.Mask(actor.Email), group.Name);

        return Result<GlobalGroupDto>.Success(await ToDtoAsync(group, ct));
    }

    public async Task<Result<GlobalGroupDto>> UpdateAsync(
        AdminContext actor, string id, UpsertPatientGroupRequest request, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<GlobalGroupDto>.Forbidden("Only a SuperAdmin can edit general groups.");

        var group = await groups.FindByIdAsync(id, ct);
        if (group is null || !group.IsGlobal)
            return Result<GlobalGroupDto>.NotFound("General group not found.");

        var previous = group.Name;

        try { group.Update(request.Name, request.Description); }
        catch (DomainException ex) { return Result<GlobalGroupDto>.Invalid(ex.Message); }

        await groups.UpdateAsync(group, ct);

        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId, actor.Email, AdminAction.ManageGlobalGroup,
                null, null,
                previous == group.Name
                    ? $"Edited general group '{group.Name}'"
                    : $"Renamed general group '{previous}' → '{group.Name}'"),
            ct);

        return Result<GlobalGroupDto>.Success(await ToDtoAsync(group, ct));
    }

    /// <summary>
    /// Deletes a global group and detaches it from every pharmacy's patients.
    /// </summary>
    /// <remarks>
    /// The detach happens first. If deletion succeeded but the detach failed,
    /// patients across the platform would keep a reference to a group that no
    /// longer exists — which shows up as a blank entry in a campaign audience,
    /// silently narrowing who a message reaches.
    /// </remarks>
    public async Task<Result<bool>> DeleteAsync(
        AdminContext actor, string id, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<bool>.Forbidden("Only a SuperAdmin can delete general groups.");

        var group = await groups.FindByIdAsync(id, ct);
        if (group is null || !group.IsGlobal)
            return Result<bool>.NotFound("General group not found.");

        var detached = await patients.RemoveGroupFromAllPatientsAsync(id, ct);
        await groups.DeleteAsync(id, ct);

        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId, actor.Email, AdminAction.ManageGlobalGroup,
                null, null,
                $"Deleted general group '{group.Name}' — removed from {detached} patient(s)"),
            ct);

        log.LogInformation(
            "SuperAdmin {Actor} deleted global group '{Name}', detached from {Count} patients",
            PersonalData.Mask(actor.Email), group.Name, detached);

        return Result<bool>.Success(true);
    }

    private async Task<GlobalGroupDto> ToDtoAsync(PatientGroup group, CancellationToken ct)
    {
        var total = await patients.CountInGroupAcrossPlatformAsync(group.Id, ct);

        // Adoption is left at zero rather than computed. Counting distinct
        // owners needs a second aggregation per group, and this list is shown
        // on a page nobody visits often — not worth the query until someone
        // asks for the number.
        return new GlobalGroupDto(
            group.Id, group.Name, group.Description, total, 0,
            group.CreatedAtUtc, group.UpdatedAtUtc);
    }
}
