namespace Acme.WebApi.Auditing;

/// <summary>
/// This endpoint changes something (or is a security event): record one
/// <c>audit_events</c> row per request, with the outcome the caller got.
/// </summary>
/// <remarks>
/// <para>
/// Written by <see cref="AuditMiddleware"/> after the response is decided, so a
/// request refused by authorization — which never reaches the controller, let
/// alone a service — is still recorded, as <c>denied</c>.
/// </para>
/// <para>
/// Every POST/PUT/PATCH/DELETE action needs this or <see cref="NoAuditAttribute"/>;
/// <c>scripts/check-audit.mjs</c> fails the build otherwise, so a new endpoint
/// cannot silently go unrecorded.
/// </para>
/// </remarks>
/// <param name="action">Dotted name, e.g. <c>poster.delete</c>. Prefixes: poster, video,
/// campaign, patient, brand, display, auth, template, subscription, admin…</param>
/// <param name="entityType">What <c>entityId</c> refers to, e.g. <c>poster</c>.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditAttribute(string action, string? entityType = null) : Attribute
{
    public string Action { get; } = action;
    public string? EntityType { get; } = entityType;

    /// <summary>Route value holding the entity id. Defaults to <c>id</c>.</summary>
    public string IdRoute { get; init; } = "id";

    /// <summary>Recorded instead of <see cref="Action"/> when the result is not ok, e.g. <c>auth.login.failed</c>.</summary>
    public string? FailedAction { get; init; }

    /// <summary>Record only refusals (401/403) — for endpoints too frequent to log when they work.</summary>
    public bool OnlyWhenDenied { get; init; }

    /// <summary>
    /// Route value holding the pharmacy acted on, for staff endpoints such as
    /// <c>api/admin/pharmacies/{pharmacyId}/…</c>. A service's
    /// <see cref="Acme.Application.Audit.IAuditScope.SetPharmacy"/> wins over it.
    /// </summary>
    public string? PharmacyRoute { get; init; }

    /// <summary>
    /// The caller is a machine, not a person — e.g. <c>"stripe"</c> for the
    /// billing webhook — and is recorded as <c>system:{name}</c>.
    /// </summary>
    public string? SystemActor { get; init; }
}

/// <summary>
/// An allow-listed view: record "opened X" in <c>activity_events</c>, at most
/// once per actor, action and entity within <see cref="DedupeSeconds"/>.
/// </summary>
/// <remarks>
/// Views are opt-in, never inferred. A GET without this attribute is not
/// logged — list polls, health checks, token refreshes and the kiosk's
/// 30-second content poll stay out by construction. A refused view (401/403)
/// is recorded in <c>audit_events</c> as <c>denied</c>, because trying to open
/// another pharmacy's patient is a security event, not activity. Anonymous
/// views (the public template gallery) are not recorded: there is no "who".
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ActivityLogAttribute(string action, string? entityType = null) : Attribute
{
    public string Action { get; } = action;
    public string? EntityType { get; } = entityType;
    public string IdRoute { get; init; } = "id";

    /// <inheritdoc cref="AuditAttribute.PharmacyRoute"/>
    public string? PharmacyRoute { get; init; }

    public int DedupeSeconds { get; init; } = 60;
}

/// <summary>A mutating endpoint that is deliberately not audited, and why.</summary>
/// <remarks>The reason is required so the decision is reviewable, not an omission.</remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class NoAuditAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
