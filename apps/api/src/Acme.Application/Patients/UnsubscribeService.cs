using Acme.Application.Audit;
using Acme.Application.Common;
using Acme.Application.Auth;

namespace Acme.Application.Patients;

/// <summary>What the unsubscribe page shows before and after the click.</summary>
public sealed record UnsubscribePreview(string PharmacyName, string MaskedEmail, bool Unsubscribed);

/// <summary>
/// Stops a pharmacy's marketing email to one address, from the link in the
/// email itself (security review R2-06).
/// </summary>
/// <remarks>
/// <para>
/// Anonymous on purpose: the person clicking has no account. The signed token
/// is the authority, and it can do exactly one thing — add its own address to
/// that pharmacy's suppression list. Campaigns skip suppressed addresses at
/// creation and again at send.
/// </para>
/// <para>
/// The address is never shown in full or written to the audit log: the page
/// is reachable by anyone holding the link, and the audit rule is counts, not
/// recipients.
/// </para>
/// </remarks>
public sealed class UnsubscribeService(
    IEmailSuppressionRepository suppressions,
    IUserRepository users,
    IAuditScope audit)
{
    public const string SourceLink = "unsubscribe-link";

    public async Task<Result<UnsubscribePreview>> PreviewAsync(string? token, CancellationToken ct)
    {
        if (!UnsubscribeToken.TryRead(token, out var ownerId, out var email))
            return Result<UnsubscribePreview>.Invalid("This unsubscribe link is not valid.");

        var pharmacy = await PharmacyNameAsync(ownerId, ct);
        var already = await suppressions.IsSuppressedAsync(ownerId, email, ct);
        return Result<UnsubscribePreview>.Success(new UnsubscribePreview(pharmacy, Mask(email), already));
    }

    public async Task<Result<UnsubscribePreview>> UnsubscribeAsync(string? token, CancellationToken ct)
    {
        if (!UnsubscribeToken.TryRead(token, out var ownerId, out var email))
            return Result<UnsubscribePreview>.Invalid("This unsubscribe link is not valid.");

        await suppressions.AddAsync(ownerId, email, SourceLink, ct);
        audit.SetEntity("pharmacy", ownerId);

        var pharmacy = await PharmacyNameAsync(ownerId, ct);
        return Result<UnsubscribePreview>.Success(new UnsubscribePreview(pharmacy, Mask(email), true));
    }

    private async Task<string> PharmacyNameAsync(string ownerId, CancellationToken ct)
    {
        var owner = await users.FindByIdAsync(ownerId, ct);
        return owner?.PharmacyDisplayName ?? "this pharmacy";
    }

    /// <summary>"jane.doe@example.com" → "j•••e@example.com" — the same masking the logs use.</summary>
    public static string Mask(string email) => PersonalData.Mask(email);
}
