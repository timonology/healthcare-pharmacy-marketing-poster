using Acme.Application.Common;
namespace Acme.Application.Audit;

/// <summary>
/// Writes audit and activity events. Never throws.
/// </summary>
/// <remarks>
/// <para>
/// Most events are written for you: an endpoint marked <c>[Audit]</c> or
/// <c>[ActivityLog]</c> produces one event per request, after the response is
/// decided, with the outcome the caller actually got (ok, denied or failed).
/// A service adds what only it knows through <see cref="IAuditScope"/>.
/// </para>
/// <para>
/// Call this directly only for work that is not an HTTP request — seeders,
/// background workers — using an <see cref="AuditActor.System"/> actor.
/// </para>
/// <para>
/// <b>Never throws</b>, because the thing being recorded has already happened.
/// Failing a sign-in, or a deletion, because the audit store is briefly
/// unavailable would turn a logging fault into an outage. Failures are logged.
/// </para>
/// </remarks>
public interface IAuditLogger
{
    /// <summary>Records a mutation or security event in <c>audit_events</c>.</summary>
    Task RecordAsync(AuditEvent e, CancellationToken ct);

    /// <summary>
    /// Records a view in <c>activity_events</c>, unless the same actor did the
    /// same thing to the same entity within <paramref name="dedupeWindow"/>.
    /// </summary>
    /// <returns>True if written; false if it was a duplicate (or the write failed).</returns>
    Task<bool> RecordActivityAsync(AuditEvent e, TimeSpan dedupeWindow, CancellationToken ct);
}

/// <summary>Persistence for both streams. Infrastructure implements it over Mongo.</summary>
public interface IAuditEventStore
{
    Task InsertAsync(AuditStream stream, AuditEvent e, CancellationToken ct);

    /// <summary>Inserts unless an event with this id exists. For one-time backfills.</summary>
    Task<bool> InsertIfMissingAsync(AuditStream stream, AuditEvent e, CancellationToken ct);

    /// <summary>
    /// Whether this actor did this action to this entity, as this pharmacy, at
    /// or after <paramref name="sinceUtc"/>. The pharmacy matters for views with
    /// no entity: an admin opening shop A's dashboard and then shop B's has
    /// made two views, not one.
    /// </summary>
    Task<bool> ExistsSinceAsync(
        AuditStream stream, string actorId, string action, string? entityId, string? pharmacyId,
        DateTime sinceUtc, CancellationToken ct);

    Task<AuditEventPage> SearchAsync(AuditStream stream, AuditEventQuery query, CancellationToken ct);
}

/// <summary>
/// A search over one stream.
/// </summary>
/// <remarks>
/// The <c>Visible*</c> fields are the access rule and are set by
/// <see cref="AuditEventService"/> from the caller's identity — never from the
/// request. When either is set an event is visible if its pharmacy is in
/// <see cref="VisiblePharmacyIds"/> <i>or</i> its actor is
/// <see cref="VisibleActorId"/>.
/// </remarks>
public sealed record AuditEventQuery(
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? Actor = null,
    string? PharmacyId = null,
    string? ActionPrefix = null,
    string? EntityType = null,
    string? EntityId = null,
    IReadOnlyList<string>? ActorKinds = null,
    bool SecurityDefault = false,
    IReadOnlyList<string>? VisiblePharmacyIds = null,
    string? VisibleActorId = null,
    int Skip = 0,
    int Take = 50,
    /// <summary>By event time: newest first unless Oldest.</summary>
    ListSort Sort = ListSort.Newest);

public sealed record AuditEventPage(IReadOnlyList<AuditEvent> Items, long Total);
