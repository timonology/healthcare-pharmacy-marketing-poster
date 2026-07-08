using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Subscriptions;
using Acme.Domain.Common;
using Acme.Domain.Patients;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Patients;

public sealed class PatientService(
    IPatientRepository patients,
    IPatientGroupRepository groups,
    IUserRepository users,
    ILogger<PatientService> logger)
{
    public async Task<Result<IReadOnlyList<PatientDto>>> ListAsync(PatientQuery query, CancellationToken ct)
    {
        var items = await patients.ListAsync(query, ct);
        logger.LogInformation(
            "Patient list for owner={OwnerId} returned {Count} (search='{Search}', groupId='{GroupId}')",
            query.OwnerId, items.Count, query.Search ?? "", query.GroupId ?? "");
        return Result<IReadOnlyList<PatientDto>>.Success(items.Select(ToDto).ToList());
    }

    public Task<int> CountAsync(PatientQuery query, CancellationToken ct) =>
        patients.CountAsync(query, ct);

    public async Task<Result<PatientDto>> GetAsync(string id, string ownerId, CancellationToken ct)
    {
        var p = await patients.FindByIdAsync(id, ct);
        if (p is null || p.OwnerId != ownerId) return Result<PatientDto>.NotFound("Patient not found.");
        return Result<PatientDto>.Success(ToDto(p));
    }

    public async Task<Result<PatientDto>> CreateAsync(
        string ownerId,
        UpsertPatientRequest request,
        CancellationToken ct)
    {
        if (await EnforcePatientLimit(ownerId, 1, ct) is { } limitFailure)
            return limitFailure;

        if (await ValidateGroupIds(ownerId, request.GroupIds, ct) is { } groupFailure)
            return groupFailure;

        Patient patient;
        try
        {
            patient = Patient.Create(
                ownerId,
                request.FullName,
                request.Email,
                request.Phone,
                request.Notes,
                request.GroupIds);
        }
        catch (DomainException ex) { return Result<PatientDto>.Invalid(ex.Message); }

        await patients.AddAsync(patient, ct);
        logger.LogInformation(
            "Patient created id={PatientId} owner={OwnerId} name='{Name}'",
            patient.Id, patient.OwnerId, patient.FullName);
        await RefreshGroupCountsAsync(ownerId, patient.GroupIds, ct);
        return Result<PatientDto>.Success(ToDto(patient));
    }

    public async Task<Result<PatientDto>> UpdateAsync(
        string id,
        string ownerId,
        UpsertPatientRequest request,
        CancellationToken ct)
    {
        var patient = await patients.FindByIdAsync(id, ct);
        if (patient is null || patient.OwnerId != ownerId)
            return Result<PatientDto>.NotFound("Patient not found.");

        if (request.GroupIds is not null
            && await ValidateGroupIds(ownerId, request.GroupIds, ct) is { } groupFailure)
            return groupFailure;

        var previousGroups = patient.GroupIds;

        try
        {
            patient.Update(
                request.FullName,
                request.Email,
                request.Phone,
                request.Notes);
            if (request.GroupIds is not null) patient.ReplaceGroups(request.GroupIds);
        }
        catch (DomainException ex) { return Result<PatientDto>.Invalid(ex.Message); }

        await patients.UpdateAsync(patient, ct);
        await RefreshGroupCountsAsync(ownerId, previousGroups.Union(patient.GroupIds), ct);
        return Result<PatientDto>.Success(ToDto(patient));
    }

    public async Task<Result<bool>> DeleteAsync(string id, string ownerId, CancellationToken ct)
    {
        var patient = await patients.FindByIdAsync(id, ct);
        if (patient is null || patient.OwnerId != ownerId)
            return Result<bool>.NotFound("Patient not found.");

        await patients.DeleteAsync(id, ct);
        await RefreshGroupCountsAsync(ownerId, patient.GroupIds, ct);
        return Result<bool>.Success(true);
    }

    public async Task<Result<bool>> DeleteManyAsync(
        string ownerId,
        IReadOnlyList<string> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0) return Result<bool>.Success(true);
        var affected = await patients.FindManyByIdsAsync(ownerId, ids, ct);
        await patients.DeleteManyAsync(ownerId, ids, ct);
        var groupIds = affected.SelectMany(p => p.GroupIds).Distinct().ToList();
        await RefreshGroupCountsAsync(ownerId, groupIds, ct);
        return Result<bool>.Success(true);
    }

    public async Task<Result<PatientDto>> AddToGroupAsync(
        string patientId,
        string ownerId,
        string groupId,
        CancellationToken ct)
    {
        var patient = await patients.FindByIdAsync(patientId, ct);
        if (patient is null || patient.OwnerId != ownerId)
            return Result<PatientDto>.NotFound("Patient not found.");

        var group = await groups.FindByIdAsync(groupId, ct);
        if (group is null || group.OwnerId != ownerId)
            return Result<PatientDto>.NotFound("Group not found.");

        try { patient.AddToGroup(groupId); }
        catch (DomainException ex) { return Result<PatientDto>.Invalid(ex.Message); }

        await patients.UpdateAsync(patient, ct);
        await RefreshGroupCountsAsync(ownerId, new[] { groupId }, ct);
        return Result<PatientDto>.Success(ToDto(patient));
    }

    public async Task<Result<PatientDto>> RemoveFromGroupAsync(
        string patientId,
        string ownerId,
        string groupId,
        CancellationToken ct)
    {
        var patient = await patients.FindByIdAsync(patientId, ct);
        if (patient is null || patient.OwnerId != ownerId)
            return Result<PatientDto>.NotFound("Patient not found.");

        patient.RemoveFromGroup(groupId);
        await patients.UpdateAsync(patient, ct);
        await RefreshGroupCountsAsync(ownerId, new[] { groupId }, ct);
        return Result<PatientDto>.Success(ToDto(patient));
    }

    public async Task<Result<int>> BulkAddToGroupAsync(
        string ownerId,
        IReadOnlyList<string> patientIds,
        string groupId,
        CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(groupId, ct);
        if (group is null || group.OwnerId != ownerId)
            return Result<int>.NotFound("Group not found.");

        var affected = await patients.FindManyByIdsAsync(ownerId, patientIds, ct);
        var changed = 0;
        foreach (var patient in affected)
        {
            if (patient.GroupIds.Contains(groupId)) continue;
            patient.AddToGroup(groupId);
            await patients.UpdateAsync(patient, ct);
            changed++;
        }

        await RefreshGroupCountsAsync(ownerId, new[] { groupId }, ct);
        return Result<int>.Success(changed);
    }

    public async Task<Result<BulkImportResult>> BulkImportAsync(
        string ownerId,
        IReadOnlyList<UpsertPatientRequest> requests,
        CancellationToken ct)
    {
        if (requests.Count == 0)
            return Result<BulkImportResult>.Success(new BulkImportResult(0, 0, Array.Empty<string>()));

        if (await EnforcePatientLimitOrTrim(ownerId, requests.Count, ct) is { remaining: var allowed, error: var err })
        {
            if (err is not null) return Result<BulkImportResult>.Forbidden(err);
            if (allowed < requests.Count)
                requests = requests.Take(allowed).ToList();
        }

        // Resolve group ids only once.
        var allGroupIds = requests.SelectMany(r => r.GroupIds ?? Array.Empty<string>()).Distinct().ToList();
        if (allGroupIds.Count > 0)
        {
            if (await ValidateGroupIdsAnyOf(ownerId, allGroupIds, ct) is { } groupFailure)
                return Result<BulkImportResult>.Invalid(groupFailure);
        }

        var imported = new List<Patient>();
        var errors = new List<string>();
        var skipped = 0;

        foreach (var (request, index) in requests.Select((r, i) => (r, i)))
        {
            try
            {
                imported.Add(Patient.Create(
                    ownerId,
                    request.FullName,
                    request.Email,
                    request.Phone,
                    request.Notes,
                    request.GroupIds));
            }
            catch (DomainException ex)
            {
                skipped++;
                if (errors.Count < 10) errors.Add($"Row {index + 1}: {ex.Message}");
            }
        }

        await patients.AddManyAsync(imported, ct);
        await RefreshGroupCountsAsync(ownerId, imported.SelectMany(p => p.GroupIds).Distinct(), ct);

        return Result<BulkImportResult>.Success(new BulkImportResult(imported.Count, skipped, errors));
    }

    private async Task<Result<PatientDto>?> EnforcePatientLimit(string ownerId, int extra, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<PatientDto>.NotFound("User not found.");

        var plan = Plans.For(user.Tier);
        if (plan.MaxPatients == Plans.Unlimited) return null;

        var current = await patients.CountByOwnerAsync(ownerId, ct);
        if (current + extra <= plan.MaxPatients) return null;

        return Result<PatientDto>.Forbidden(
            $"You've reached your {plan.DisplayName} plan limit of {plan.MaxPatients} patients. Upgrade to add more.");
    }

    private async Task<(int remaining, string? error)?> EnforcePatientLimitOrTrim(string ownerId, int wanted, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return (0, "User not found.");

        var plan = Plans.For(user.Tier);
        if (plan.MaxPatients == Plans.Unlimited) return (wanted, null);

        var current = await patients.CountByOwnerAsync(ownerId, ct);
        var allowed = Math.Max(0, plan.MaxPatients - current);

        if (allowed == 0)
            return (0, $"You're already at your {plan.DisplayName} plan limit of {plan.MaxPatients} patients.");

        return (Math.Min(wanted, allowed), null);
    }

    private async Task<Result<PatientDto>?> ValidateGroupIds(
        string ownerId,
        IReadOnlyList<string>? groupIds,
        CancellationToken ct)
    {
        if (groupIds is null || groupIds.Count == 0) return null;

        foreach (var id in groupIds.Distinct())
        {
            var group = await groups.FindByIdAsync(id, ct);
            if (group is null || group.OwnerId != ownerId)
                return Result<PatientDto>.Invalid($"Group {id} not found.");
        }
        return null;
    }

    private async Task<string?> ValidateGroupIdsAnyOf(string ownerId, IReadOnlyList<string> groupIds, CancellationToken ct)
    {
        foreach (var id in groupIds)
        {
            var group = await groups.FindByIdAsync(id, ct);
            if (group is null || group.OwnerId != ownerId)
                return $"Group {id} not found.";
        }
        return null;
    }

    private async Task RefreshGroupCountsAsync(string ownerId, IEnumerable<string> groupIds, CancellationToken ct)
    {
        foreach (var groupId in groupIds.Distinct())
        {
            var group = await groups.FindByIdAsync(groupId, ct);
            if (group is null || group.OwnerId != ownerId) continue;
            var members = await patients.ListByGroupAsync(ownerId, groupId, ct);
            group.SetPatientCount(members.Count);
            await groups.UpdateAsync(group, ct);
        }
    }

    private static PatientDto ToDto(Patient p) => new(
        p.Id,
        p.OwnerId,
        p.FullName,
        p.Email,
        p.Phone,
        p.Notes,
        p.GroupIds,
        p.CreatedAtUtc,
        p.UpdatedAtUtc);
}
