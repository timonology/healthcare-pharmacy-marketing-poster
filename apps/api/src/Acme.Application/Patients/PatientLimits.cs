using Acme.Application.Auth;
using Acme.Application.Subscriptions;

namespace Acme.Application.Patients;

/// <summary>How many more contacts a pharmacy's plan allows.</summary>
public sealed record PatientAllowance(int Remaining, string? Error, string PlanName, int Limit)
{
    /// <summary>True when the pharmacy is already at its ceiling.</summary>
    public bool Exhausted => Error is not null || Remaining <= 0;
}

/// <summary>
/// The plan ceiling on stored contacts, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Extracted because a second importer was about to be written without it. The
/// CSV import path bypassed the limit entirely — a Free pharmacy could not add
/// a 51st contact through the form, and could add five thousand through a file.
/// A limit enforced on one route and not another is not a limit.
/// </para>
/// <para>
/// Kept as a query rather than a guard so callers can do something better than
/// refusing: the importer reports the rows it could not fit as skipped, with
/// the reason, instead of trimming silently.
/// </para>
/// </remarks>
public static class PatientLimits
{
    public static async Task<PatientAllowance> RemainingAsync(
        IUserRepository users,
        IPatientRepository patients,
        string ownerId,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null)
            return new PatientAllowance(0, "Account not found.", "", 0);

        var plan = Plans.For(user.Tier);

        if (plan.MaxPatients == Plans.Unlimited)
            return new PatientAllowance(int.MaxValue, null, plan.DisplayName, Plans.Unlimited);

        var current = await patients.CountByOwnerAsync(ownerId, ct);
        var remaining = Math.Max(0, plan.MaxPatients - current);

        return remaining == 0
            ? new PatientAllowance(
                0,
                $"You're already at your {plan.DisplayName} plan limit of {plan.MaxPatients} contacts.",
                plan.DisplayName,
                plan.MaxPatients)
            : new PatientAllowance(remaining, null, plan.DisplayName, plan.MaxPatients);
    }
}
