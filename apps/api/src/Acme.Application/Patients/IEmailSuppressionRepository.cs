namespace Acme.Application.Patients;

/// <summary>
/// The addresses a pharmacy may no longer send marketing email to (security
/// review R2-06). One list per pharmacy, keyed by address: a person who
/// unsubscribes is unsubscribed however many patient records carry their email.
/// </summary>
/// <remarks>
/// There is deliberately no way for a pharmacy to remove an address. Only the
/// person can unsubscribe, and re-subscribing on their behalf is exactly what
/// the list exists to prevent.
/// </remarks>
public interface IEmailSuppressionRepository
{
    /// <summary>Adds the address; already present is not an error.</summary>
    Task AddAsync(string ownerId, string email, string source, CancellationToken ct);

    Task<bool> IsSuppressedAsync(string ownerId, string email, CancellationToken ct);

    /// <summary>Which of these addresses are suppressed for this pharmacy (lower-case).</summary>
    Task<IReadOnlySet<string>> FindSuppressedAsync(
        string ownerId, IReadOnlyCollection<string> emails, CancellationToken ct);
}
