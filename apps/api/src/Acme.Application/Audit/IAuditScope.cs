using System.Globalization;

namespace Acme.Application.Audit;

/// <summary>
/// What a service knows about the current request's audit event that the HTTP
/// layer does not.
/// </summary>
/// <remarks>
/// <para>
/// One per request (scoped). The endpoint's <c>[Audit]</c> attribute names the
/// action; a service fills in the rest — the entity it created, the pharmacy an
/// admin switched into, how many recipients a campaign had, whose sign-in
/// failed. The event is written once, after the response, with all of it.
/// </para>
/// <para>
/// Outside a request (a seeder, a worker) nothing reads the scope, so calling
/// it is harmless; such code records through <see cref="IAuditLogger"/> instead.
/// </para>
/// </remarks>
public interface IAuditScope
{
    /// <summary>The entity acted on. Overrides the route's id.</summary>
    void SetEntity(string? entityType, string? entityId);

    /// <summary>Sets the entity id only if nothing has set one — e.g. from a created resource.</summary>
    void SetEntityIdIfEmpty(string? entityId);

    /// <summary>The tenant acted as, when it is not the caller's own (e.g. an impersonation target).</summary>
    void SetPharmacy(string? pharmacyId);

    /// <summary>Replaces the attribute's action, when one endpoint does two different things.</summary>
    void SetAction(string action);

    /// <summary>The actor, when the request is not signed in yet (sign-in, password reset).</summary>
    void SetActor(AuditActor actor);

    /// <summary>Adds sanitised detail. See <see cref="AuditMeta"/> for what is refused.</summary>
    void AddMeta(string key, object? value);

    /// <summary>Records nothing for this request (e.g. switching back to your own pharmacy).</summary>
    void Suppress();

    AuditScopeState State { get; }
}

/// <summary>A snapshot of what services have said about the current request.</summary>
public sealed record AuditScopeState(
    string? EntityType,
    string? EntityId,
    bool EntitySet,
    string? PharmacyId,
    string? Action,
    AuditActor? Actor,
    IReadOnlyDictionary<string, string> Meta,
    bool Suppressed);

/// <inheritdoc />
public sealed class AuditScope : IAuditScope
{
    private string? _entityType;
    private string? _entityId;
    private bool _entitySet;
    private string? _pharmacyId;
    private string? _action;
    private AuditActor? _actor;
    private bool _suppressed;
    private readonly Dictionary<string, string> _meta = new(StringComparer.Ordinal);

    public void SetEntity(string? entityType, string? entityId)
    {
        _entityType = entityType;
        _entityId = entityId;
        _entitySet = true;
    }

    public void SetEntityIdIfEmpty(string? entityId)
    {
        if (_entityId is null && !string.IsNullOrWhiteSpace(entityId)) _entityId = entityId;
    }

    public void SetPharmacy(string? pharmacyId) => _pharmacyId = pharmacyId;

    public void SetAction(string action)
    {
        if (!string.IsNullOrWhiteSpace(action)) _action = action;
    }

    public void SetActor(AuditActor actor) => _actor = actor;

    public void AddMeta(string key, object? value)
    {
        if (value is null || string.IsNullOrWhiteSpace(key)) return;
        _meta[key] = value switch
        {
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    public void Suppress() => _suppressed = true;

    public AuditScopeState State => new(
        _entityType, _entityId, _entitySet, _pharmacyId, _action, _actor,
        new Dictionary<string, string>(_meta, StringComparer.Ordinal), _suppressed);
}
