using Acme.Domain.Admin;

namespace Acme.Application.Admin;

/// <summary>
/// Server-side filter for the audit log. Every field is optional.
/// </summary>
public sealed record AuditQuery(
    // Scopes the feed to one administrator. Set by the service from the
    // caller's own identity for non-SuperAdmins — never read from the request.
    string? ActorId = null,
    AdminAction? Action = null,
    // Substring match against actor or target email.
    string? Search = null,
    DateTime? FromUtc = null,
    int Skip = 0,
    int Take = 50);

public sealed record AuditPage(IReadOnlyList<AdminAuditEntry> Items, long Total);

public interface IAdminAuditRepository
{
    Task AddAsync(AdminAuditEntry entry, CancellationToken ct);

    /// <summary>Most recent first, filtered and paged.</summary>
    Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct);
}
