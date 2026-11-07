using Acme.Application.Audit;
using Acme.Infrastructure.Audit;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Audit;

/// <summary>
/// The view de-duplication: same person, same thing, same pharmacy, inside the
/// window — one row. Anything that differs is a second view.
/// </summary>
public class AuditLoggerTests
{
    private static readonly AuditActor Admin = new("admin-1", "a@x.test", "Admin", ActorKinds.Admin, null);

    private static AuditEvent View(string action, string? entityId, string? pharmacyId, DateTime? at = null) =>
        AuditEvent.Create(Admin, action, null, entityId, AuditResults.Ok, pharmacyId: pharmacyId, atUtc: at);

    private static (AuditLogger Logger, MemoryStore Store) Create()
    {
        var store = new MemoryStore();
        return (new AuditLogger(store, new MemoryCache(new MemoryCacheOptions()), NullLogger<AuditLogger>.Instance), store);
    }

    [Fact]
    public async Task A_repeat_view_inside_the_window_is_one_row()
    {
        var (logger, store) = Create();

        (await logger.RecordActivityAsync(View("poster.view", "p1", "shop-a"), TimeSpan.FromSeconds(60), default)).Should().BeTrue();
        (await logger.RecordActivityAsync(View("poster.view", "p1", "shop-a"), TimeSpan.FromSeconds(60), default)).Should().BeFalse();

        store.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_same_dashboard_in_two_pharmacies_is_two_views()
    {
        // An admin managing shop A, then shop B: the dashboard has no entity
        // id, so without the pharmacy in the key the second view would vanish
        // for an hour.
        var (logger, store) = Create();
        var hour = TimeSpan.FromHours(1);

        await logger.RecordActivityAsync(View("dashboard.view", null, "shop-a"), hour, default);
        await logger.RecordActivityAsync(View("dashboard.view", null, "shop-b"), hour, default);

        store.Rows.Select(r => r.PharmacyId).Should().BeEquivalentTo(new[] { "shop-a", "shop-b" });
    }

    [Fact]
    public async Task Another_instance_sees_the_first_instances_row()
    {
        // Two API instances do not share a memory cache; the store check is
        // what keeps the second from writing a duplicate.
        var store = new MemoryStore();
        AuditLogger Instance() => new(store, new MemoryCache(new MemoryCacheOptions()), NullLogger<AuditLogger>.Instance);

        await Instance().RecordActivityAsync(View("patient.view", "pt1", "shop-a"), TimeSpan.FromSeconds(60), default);
        (await Instance().RecordActivityAsync(View("patient.view", "pt1", "shop-a"), TimeSpan.FromSeconds(60), default))
            .Should().BeFalse();

        store.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_view_after_the_window_is_a_new_row()
    {
        var store = new MemoryStore();
        AuditLogger Instance() => new(store, new MemoryCache(new MemoryCacheOptions()), NullLogger<AuditLogger>.Instance);
        var now = DateTime.UtcNow;

        await Instance().RecordActivityAsync(View("video.view", "v1", "shop-a", now.AddMinutes(-5)), TimeSpan.FromSeconds(60), default);
        await Instance().RecordActivityAsync(View("video.view", "v1", "shop-a", now), TimeSpan.FromSeconds(60), default);

        store.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_broken_store_never_breaks_the_request()
    {
        var logger = new AuditLogger(new MemoryStore { Broken = true },
            new MemoryCache(new MemoryCacheOptions()), NullLogger<AuditLogger>.Instance);

        var record = () => logger.RecordAsync(View("poster.delete", "p1", "shop-a"), default);
        var view = () => logger.RecordActivityAsync(View("poster.view", "p1", "shop-a"), TimeSpan.FromSeconds(60), default);

        await record.Should().NotThrowAsync();
        await view.Should().NotThrowAsync();
    }

    private sealed class MemoryStore : IAuditEventStore
    {
        public List<AuditEvent> Rows { get; } = [];
        public bool Broken { get; init; }

        public Task InsertAsync(AuditStream stream, AuditEvent e, CancellationToken ct)
        {
            if (Broken) throw new InvalidOperationException("store down");
            Rows.Add(e);
            return Task.CompletedTask;
        }

        public Task<bool> InsertIfMissingAsync(AuditStream stream, AuditEvent e, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ExistsSinceAsync(
            AuditStream stream, string actorId, string action, string? entityId, string? pharmacyId,
            DateTime sinceUtc, CancellationToken ct)
        {
            if (Broken) throw new InvalidOperationException("store down");
            return Task.FromResult(Rows.Any(r =>
                r.ActorId == actorId && r.Action == action && r.EntityId == entityId
                && r.PharmacyId == pharmacyId && r.AtUtc >= sinceUtc));
        }

        public Task<AuditEventPage> SearchAsync(AuditStream stream, AuditEventQuery query, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
