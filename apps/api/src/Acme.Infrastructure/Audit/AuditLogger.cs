using Acme.Application.Audit;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Audit;

/// <inheritdoc />
public sealed class AuditLogger(
    IAuditEventStore store,
    IMemoryCache cache,
    ILogger<AuditLogger> log) : IAuditLogger
{
    public async Task RecordAsync(AuditEvent e, CancellationToken ct)
    {
        try
        {
            await store.InsertAsync(AuditStream.Audit, e, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The event has happened; refusing it now helps nobody. Loud in the
            // logs so a broken audit store is noticed.
            log.LogError(ex, "Audit write failed: {Action} by {Actor} ({Result})",
                e.Action, e.ActorId, e.Result);
        }
    }

    public async Task<bool> RecordActivityAsync(AuditEvent e, TimeSpan dedupeWindow, CancellationToken ct)
    {
        // Same actor, same action, same entity, inside the window: one row.
        //
        // Two layers. The memory cache answers the common case — somebody
        // reopening a poster, or React rendering a page twice — without a
        // database round trip. The store check covers a second API instance,
        // which has its own cache.
        var key = $"activity:{e.ActorId}|{e.PharmacyId}|{e.Action}|{e.EntityId}";
        if (cache.TryGetValue(key, out _)) return false;

        try
        {
            var since = e.AtUtc - dedupeWindow;
            if (await store.ExistsSinceAsync(AuditStream.Activity, e.ActorId, e.Action, e.EntityId, e.PharmacyId, since, ct))
            {
                cache.Set(key, true, dedupeWindow);
                return false;
            }

            await store.InsertAsync(AuditStream.Activity, e, ct);
            cache.Set(key, true, dedupeWindow);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Activity write failed: {Action} by {Actor}", e.Action, e.ActorId);
            return false;
        }
    }
}
