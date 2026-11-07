using Acme.Domain.Common;

namespace Acme.Domain.Users;

/// <summary>Where a request has got to.</summary>
public enum AccessRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>
/// Somebody asking to be given access to a pharmacy that is not theirs.
/// </summary>
/// <remarks>
/// <para>
/// The only route by which one account comes to read another pharmacy's
/// patient list without a head office relationship — so it is deliberately a
/// request rather than a grant. Marketing25 staff decide, not the requester
/// and not the pharmacy, because the person asking may be a locum with no
/// existing relationship to anybody in the system.
/// </para>
/// <para>
/// <b>Approval is what creates the membership.</b> The request itself grants
/// nothing at any point; a Pending row is a piece of paper, not a key.
/// </para>
/// </remarks>
public sealed class PharmacyAccessRequest : Entity
{
    /// <summary>Who is asking.</summary>
    public string UserId { get; private set; } = default!;

    /// <summary>Their email, so the review queue needs no extra lookup.</summary>
    public string UserEmail { get; private set; } = default!;

    /// <summary>The pharmacy they want to reach.</summary>
    public string PharmacyId { get; private set; } = default!;

    public string PharmacyEmail { get; private set; } = default!;

    public AccessRequestStatus Status { get; private set; }

    /// <summary>
    /// Why they say they need it.
    /// </summary>
    /// <remarks>
    /// Required. An administrator approving access to somebody's patient list
    /// needs something to weigh, and "no reason given" is itself an answer.
    /// </remarks>
    public string Reason { get; private set; } = default!;

    public string? ReviewedByUserId { get; private set; }
    public string? ReviewedByEmail { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }

    /// <summary>What the reviewer said, shown back to the requester.</summary>
    public string? ReviewNote { get; private set; }

    public bool IsPending => Status == AccessRequestStatus.Pending;

    private PharmacyAccessRequest() { }

    public static PharmacyAccessRequest Raise(
        string userId, string userEmail, string pharmacyId, string pharmacyEmail, string reason)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new DomainException("A request needs somebody to make it.");
        if (string.IsNullOrWhiteSpace(pharmacyId))
            throw new DomainException("A request needs a pharmacy.");
        if (string.Equals(userId, pharmacyId, StringComparison.Ordinal))
            throw new DomainException("You already have access to your own pharmacy.");

        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length < 10)
            throw new DomainException(
                "Please say why you need access — an administrator has to weigh it.");
        if (trimmed.Length > 500)
            throw new DomainException("Please keep the reason under 500 characters.");

        return new PharmacyAccessRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            UserEmail = userEmail,
            PharmacyId = pharmacyId,
            PharmacyEmail = pharmacyEmail,
            Reason = trimmed,
            Status = AccessRequestStatus.Pending,
        };
    }

    /// <summary>
    /// Records a decision.
    /// </summary>
    /// <remarks>
    /// Throws on a second call rather than overwriting. A request that can be
    /// re-decided is one where an approval could be quietly replaced by a
    /// rejection, or vice versa, with only the latest visible — and the
    /// membership created by the first decision would already exist.
    /// </remarks>
    public void Decide(
        AccessRequestStatus outcome, string reviewerId, string reviewerEmail, string? note)
    {
        if (Status != AccessRequestStatus.Pending)
            throw new DomainException("This request has already been decided.");
        if (outcome == AccessRequestStatus.Pending)
            throw new DomainException("A decision must approve or reject.");

        Status = outcome;
        ReviewedByUserId = reviewerId;
        ReviewedByEmail = reviewerEmail;
        ReviewedAtUtc = DateTime.UtcNow;
        ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Touch();
    }
}
