using System.Globalization;
using System.Text;
using Acme.Application.Admin;
using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Domain.Users;

namespace Acme.Application.Audit;

/// <summary>Which list the admin console is showing. Never one undifferentiated list.</summary>
public enum AuditTab
{
    /// <summary>
    /// <c>audit_events</c>. Opens on staff actions, sign-in events and anything
    /// refused (401/403); machines (seeders, the smoke account) are left out
    /// unless asked for.
    /// </summary>
    Security,

    /// <summary>What staff (Admin, SuperAdmin) did and opened.</summary>
    Staff,

    /// <summary>What pharmacy users did and opened.</summary>
    Pharmacy,
}

/// <summary>Within the staff and pharmacy tabs: changes, or views.</summary>
public enum AuditStreamView
{
    Changes,
    Views,
}

/// <summary>Within the security tab.</summary>
public enum SecurityView
{
    /// <summary>Staff actions, <c>auth.*</c>, and anything refused. No system rows.</summary>
    Default,

    /// <summary>Every audit row a person caused, pharmacy edits included.</summary>
    People,

    /// <summary>Seeders, background work and the smoke account.</summary>
    System,
}

/// <summary>The console's filters. Visibility is not among them — it comes from the caller.</summary>
public sealed record AuditEventSearch(
    AuditTab Tab,
    AuditStreamView Stream = AuditStreamView.Changes,
    SecurityView View = SecurityView.Default,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? Actor = null,
    string? PharmacyId = null,
    string? Action = null,
    string? EntityType = null,
    string? EntityId = null,
    int Skip = 0,
    int Take = 50,
    ListSort Sort = ListSort.Newest);

public sealed record AuditEventDto(
    string Id,
    DateTime AtUtc,
    string ActorId,
    string? ActorEmail,
    string? ActorRole,
    string ActorKind,
    string? PharmacyId,
    string Action,
    string? EntityType,
    string? EntityId,
    string Result,
    string? Ip,
    string? RequestId,
    IReadOnlyDictionary<string, string> Meta);

public sealed record AuditEventListDto(
    IReadOnlyList<AuditEventDto> Items, long Total, int Skip, int Take);

public sealed record AuditCsv(string FileName, string Content, int Rows, bool Truncated);

/// <summary>
/// Reads the audit and activity logs for the admin console, with the
/// visibility rules applied from the caller's identity.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who sees what.</b> A SuperAdmin sees every tenant on every tab. An Admin
/// sees events in the pharmacies they manage, and their own actions — not
/// another administrator's work elsewhere, and not unassigned shops. Pharmacy
/// users never reach this service (the controller is behind the admin policy).
/// </para>
/// <para>
/// The rule is applied as a filter in the query, not by trimming a page
/// afterwards, so totals and paging are honest and an Admin cannot page past
/// it.
/// </para>
/// </remarks>
public sealed class AuditEventService(IAuditEventStore store, IUserRepository users)
{
    public const int PageMax = 200;
    public const int ExportMax = 10_000;

    public async Task<Result<AuditEventListDto>> SearchAsync(
        AdminContext caller, AuditEventSearch search, CancellationToken ct)
    {
        var take = Math.Clamp(search.Take, 1, PageMax);
        var skip = Math.Max(0, search.Skip);

        var built = await BuildAsync(caller, search with { Skip = skip, Take = take }, ct);
        if (built is null) return Result<AuditEventListDto>.Forbidden("Only administrators can read the audit log.");

        var (stream, query) = built.Value;
        var page = await store.SearchAsync(stream, query, ct);
        return Result<AuditEventListDto>.Success(
            new AuditEventListDto(page.Items.Select(ToDto).ToList(), page.Total, skip, take));
    }

    /// <summary>
    /// The current filters as CSV, newest first, at most <see cref="ExportMax"/>
    /// rows. Cells that a spreadsheet would run as a formula are neutralised.
    /// </summary>
    public async Task<Result<AuditCsv>> ExportCsvAsync(
        AdminContext caller, AuditEventSearch search, CancellationToken ct)
    {
        var built = await BuildAsync(caller, search with { Skip = 0, Take = ExportMax }, ct);
        if (built is null) return Result<AuditCsv>.Forbidden("Only administrators can export the audit log.");

        var (stream, query) = built.Value;
        var page = await store.SearchAsync(stream, query, ct);

        var sb = new StringBuilder();
        sb.AppendLine("atUtc,actorEmail,actorId,actorRole,actorKind,pharmacyId,action,entityType,entityId,result,ip,requestId,meta");
        foreach (var e in page.Items)
        {
            var meta = string.Join("; ", e.Meta.Select(kv => $"{kv.Key}={kv.Value}"));
            sb.AppendJoin(',',
                Cell(e.AtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
                Cell(e.ActorEmail), Cell(e.ActorId), Cell(e.ActorRole), Cell(e.ActorKind),
                Cell(e.PharmacyId), Cell(e.Action), Cell(e.EntityType), Cell(e.EntityId),
                Cell(e.Result), Cell(e.Ip), Cell(e.RequestId), Cell(meta));
            sb.AppendLine();
        }

        var name = $"{search.Tab.ToString().ToLowerInvariant()}-"
                   + (search.Tab == AuditTab.Security ? search.View.ToString().ToLowerInvariant()
                                                      : search.Stream.ToString().ToLowerInvariant())
                   + $"-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";

        return Result<AuditCsv>.Success(
            new AuditCsv(name, sb.ToString(), page.Items.Count, page.Total > page.Items.Count));
    }

    /// <summary>Which collection, and the query including the caller's visibility. Null if not staff.</summary>
    public async Task<(AuditStream Stream, AuditEventQuery Query)?> BuildAsync(
        AdminContext caller, AuditEventSearch s, CancellationToken ct)
    {
        if (!caller.Role.IsStaff()) return null;

        var stream = s.Tab == AuditTab.Security || s.Stream == AuditStreamView.Changes
            ? AuditStream.Audit
            : AuditStream.Activity;

        IReadOnlyList<string> kinds;
        var securityDefault = false;
        switch (s.Tab)
        {
            case AuditTab.Security:
                switch (s.View)
                {
                    case SecurityView.System:
                        kinds = [ActorKinds.System];
                        break;
                    case SecurityView.People:
                        kinds = [ActorKinds.Pharmacy, ActorKinds.Admin];
                        break;
                    default:
                        kinds = [ActorKinds.Pharmacy, ActorKinds.Admin];
                        securityDefault = true;
                        break;
                }
                break;
            case AuditTab.Staff:
                kinds = [ActorKinds.Admin];
                break;
            default:
                kinds = [ActorKinds.Pharmacy];
                break;
        }

        // Visibility, from the caller — never from the request.
        IReadOnlyList<string>? visiblePharmacies = null;
        string? visibleActor = null;
        if (caller.Role != UserRole.SuperAdmin)
        {
            var managed = await users.ListManagedByAsync(caller.UserId, ct);
            visiblePharmacies = managed.Select(u => u.Id).ToList();
            visibleActor = caller.UserId;
        }

        var query = new AuditEventQuery(
            FromUtc: s.FromUtc is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
            ToUtc: s.ToUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null,
            Actor: Clip(s.Actor, 254),
            PharmacyId: Clip(s.PharmacyId, 64),
            ActionPrefix: Clip(s.Action, 80),
            EntityType: Clip(s.EntityType, 40),
            EntityId: Clip(s.EntityId, 64),
            ActorKinds: kinds,
            SecurityDefault: securityDefault,
            VisiblePharmacyIds: visiblePharmacies,
            VisibleActorId: visibleActor,
            Skip: s.Skip,
            Take: s.Take,
            Sort: s.Sort);

        return (stream, query);
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        return v.Length > max ? v[..max] : v;
    }

    /// <summary>
    /// A CSV cell: quoted when needed, and a leading <c>= + - @</c> (or tab/CR)
    /// prefixed with an apostrophe, so an email or meta value cannot run as a
    /// formula when the file is opened in a spreadsheet.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    private static AuditEventDto ToDto(AuditEvent e) => new(
        e.Id, e.AtUtc, e.ActorId, e.ActorEmail, e.ActorRole, e.ActorKind, e.PharmacyId,
        e.Action, e.EntityType, e.EntityId, e.Result, e.Ip, e.RequestId, e.Meta);
}
