using Acme.Application.Audit;
using Acme.Application.Common;
using Acme.Domain.Admin;
using Acme.Domain.Users;
using Acme.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Acme.Infrastructure.Audit;

/// <summary>
/// Copies the history in <c>admin_audit</c> into <c>audit_events</c>, once, so
/// the new console shows staff actions from before it existed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once, up to a cutoff.</b> The first run stores a cutoff (now) in
/// <c>migrations</c>; only rows older than it are copied. <c>admin_audit</c> is
/// still written for now (the legacy endpoint reads it), and from the cutoff on
/// the same actions are recorded directly by the request — copying them too
/// would show every one twice.
/// </para>
/// <para>
/// <b>Idempotent and resumable.</b> Rows keep their ids and are upserted, so a
/// run interrupted half way simply carries on next boot; the marker is only
/// marked complete after the last row.
/// </para>
/// </remarks>
public sealed class LegacyAuditBackfill(
    MongoContext ctx,
    IAuditEventStore store,
    ILogger<LegacyAuditBackfill> log) : IStartupTask
{
    private const string MarkerId = "audit-legacy-backfill";

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var markers = ctx.Collection<MigrationMarker>("migrations");

        // $setOnInsert: the first boot fixes the cutoff, later boots keep it.
        var marker = await markers.FindOneAndUpdateAsync(
            Builders<MigrationMarker>.Filter.Eq(m => m.Id, MarkerId),
            Builders<MigrationMarker>.Update.SetOnInsert(m => m.CutoffUtc, DateTime.UtcNow),
            new FindOneAndUpdateOptions<MigrationMarker>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After,
            },
            ct);

        if (marker.CompletedAtUtc is not null) return;

        var legacy = ctx.Collection<AdminAuditDocument>("admin_audit");
        var users = ctx.Collection<LegacyUserRole>("users");

        // Roles are looked up once per person, not once per row.
        var roles = new Dictionary<string, UserRole?>(StringComparer.Ordinal);
        async Task<UserRole?> RoleOf(string? id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (roles.TryGetValue(id, out var known)) return known;
            var row = await users.Find(u => u.Id == id).FirstOrDefaultAsync(ct);
            // Stored as an int, and absent on accounts that predate the field —
            // which were pharmacy users (see UserFieldBackfill). No row: unknown.
            return roles[id] = row is null ? null : (UserRole)(row.Role ?? (int)UserRole.PharmacyUser);
        }

        var copied = 0;
        using var cursor = await legacy
            .Find(d => d.CreatedAtUtc < marker.CutoffUtc)
            .SortBy(d => d.CreatedAtUtc)
            .ToCursorAsync(ct);

        while (await cursor.MoveNextAsync(ct))
        {
            foreach (var d in cursor.Current)
            {
                var actorRole = await RoleOf(d.ActorId);
                var targetRole = await RoleOf(d.TargetUserId);
                var e = ToEvent(d, actorRole, targetRole);
                if (await store.InsertIfMissingAsync(AuditStream.Audit, e, ct)) copied++;
            }
        }

        await markers.UpdateOneAsync(
            Builders<MigrationMarker>.Filter.Eq(m => m.Id, MarkerId),
            Builders<MigrationMarker>.Update.Set(m => m.CompletedAtUtc, DateTime.UtcNow),
            cancellationToken: ct);

        log.LogInformation("Copied {Count} legacy admin_audit row(s) into audit_events", copied);
    }

    /// <summary>One legacy row as an event.</summary>
    internal static AuditEvent ToEvent(AdminAuditDocument d, UserRole? actorRole, UserRole? targetRole)
    {
        var staffActor = actorRole?.IsStaff() ?? true; // admin_audit was a staff log
        var actor = new AuditActor(
            d.ActorId, d.ActorEmail, actorRole?.ToString(),
            staffActor ? ActorKinds.Admin : ActorKinds.Pharmacy,
            PharmacyId: null);

        // The tenant is the target when the target is a pharmacy; staff targets
        // (creating an administrator) belong to no tenant.
        var pharmacyId = targetRole is { } r && !r.IsStaff() ? d.TargetUserId : null;

        var meta = new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "admin_audit" };
        if (!string.IsNullOrWhiteSpace(d.Detail)) meta["detail"] = d.Detail;

        return AuditEvent.Create(
            actor,
            ActionName((AdminAction)d.Action, staffActor),
            entityType: d.TargetUserId is null ? null : "user",
            entityId: d.TargetUserId,
            result: AuditResults.Ok,
            meta: meta,
            pharmacyId: pharmacyId,
            atUtc: DateTime.SpecifyKind(d.CreatedAtUtc, DateTimeKind.Utc),
            id: d.Id);
    }

    /// <summary>The new name for each legacy action.</summary>
    /// <remarks>
    /// Acting as another pharmacy was one action in the old log. A member (a
    /// head office) switching into a branch is not impersonation, so it maps
    /// by who did it.
    /// </remarks>
    public static string ActionName(AdminAction action, bool staffActor) => action switch
    {
        AdminAction.CreateAdmin => "admin.admin.create",
        AdminAction.UpdateAdmin => "admin.admin.update",
        AdminAction.DeactivateUser => "admin.user.deactivate",
        AdminAction.ReactivateUser => "admin.user.reactivate",
        AdminAction.AssignUserToAdmin => "admin.user.assign",
        AdminAction.UnassignUserFromAdmin => "admin.user.unassign",
        AdminAction.OverrideSubscriptionTier => "admin.subscription.override",
        AdminAction.ChangeRole => "admin.user.role",
        AdminAction.GrantVideoCredits => "admin.credits.grant",
        AdminAction.GenerateTemplate => "admin.template.generate",
        AdminAction.ViewPatientContact => "admin.patient.view_contact",
        AdminAction.ResendCampaign => "admin.campaign.resend",
        AdminAction.ChangePlatformSetting => "admin.settings.change",
        AdminAction.StartActingAs => staffActor ? "admin.impersonate.start" : "auth.act_as.start",
        AdminAction.StopActingAs => staffActor ? "admin.impersonate.stop" : "auth.act_as.stop",
        AdminAction.RequestPharmacyAccess => staffActor ? "admin.access.request" : "pharmacy.access.request",
        AdminAction.ReviewPharmacyAccess => "admin.access.review",
        AdminAction.InvitePharmacist => "admin.invite",
        AdminAction.ManageGlobalGroup => "admin.global_group.manage",
        AdminAction.SetHeadOffice => "admin.head_office.set",
        AdminAction.ViewBranchDetail => staffActor ? "admin.branch.view" : "pharmacy.branch.view",
        _ => $"admin.legacy.{(int)action}",
    };

    internal sealed class MigrationMarker
    {
        [BsonId] public string Id { get; set; } = default!;
        [BsonElement("cutoffUtc")] public DateTime CutoffUtc { get; set; }
        [BsonElement("completedAtUtc")] [BsonIgnoreIfNull] public DateTime? CompletedAtUtc { get; set; }
    }

    /// <summary>Just the role: the backfill must not depend on the full user mapping.</summary>
    internal sealed class LegacyUserRole
    {
        [BsonId] public string Id { get; set; } = default!;
        [BsonElement("role")] public int? Role { get; set; }
    }
}
