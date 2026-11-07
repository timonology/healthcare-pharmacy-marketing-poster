using System.Text.RegularExpressions;
using Acme.Domain.Users;

namespace Acme.Application.Audit;

/// <summary>
/// One thing somebody did: who, as which pharmacy, to what, and how it ended.
/// </summary>
/// <remarks>
/// <para>
/// The same shape for both streams. <see cref="AuditStream.Audit"/> holds
/// mutations and security events and is kept indefinitely;
/// <see cref="AuditStream.Activity"/> holds views ("opened a poster") and
/// expires after 90 days. They are separate collections so that the record of
/// who deleted a user is never buried under a thousand page opens, and so the
/// view log can expire without touching the security record.
/// </para>
/// <para>
/// <b>What never goes in.</b> Passwords, tokens, card data, and lists of
/// recipients or patients — counts only. <see cref="AuditMeta"/> enforces it on
/// the way in rather than trusting every caller to remember.
/// </para>
/// </remarks>
public sealed record AuditEvent(
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
    IReadOnlyDictionary<string, string> Meta)
{
    public static AuditEvent Create(
        AuditActor actor,
        string action,
        string? entityType,
        string? entityId,
        string result,
        IReadOnlyDictionary<string, string>? meta = null,
        string? pharmacyId = null,
        string? ip = null,
        string? requestId = null,
        DateTime? atUtc = null,
        string? id = null) =>
        new(
            id ?? Guid.NewGuid().ToString("N"),
            atUtc ?? DateTime.UtcNow,
            actor.Id,
            actor.Email,
            actor.Role,
            actor.Kind,
            pharmacyId ?? actor.PharmacyId,
            action,
            entityType,
            entityId,
            result,
            ip,
            requestId,
            AuditMeta.Sanitize(meta));
}

/// <summary>Which collection an event belongs in.</summary>
public enum AuditStream
{
    /// <summary><c>audit_events</c>: mutations and security. No expiry.</summary>
    Audit,

    /// <summary><c>activity_events</c>: views and page opens. Expires after 90 days.</summary>
    Activity,
}

/// <summary>Who is acting. Pharmacy work and staff work are never one list.</summary>
public static class ActorKinds
{
    public const string Pharmacy = "pharmacy";
    public const string Admin = "admin";

    /// <summary>
    /// Seeders, background work and the smoke account — machines, not people.
    /// Kept off the staff security view by default.
    /// </summary>
    public const string System = "system";

    public static readonly IReadOnlyList<string> All = [Pharmacy, Admin, System];
}

public static class AuditResults
{
    public const string Ok = "ok";

    /// <summary>Refused: not signed in, or not allowed (401/403).</summary>
    public const string Denied = "denied";

    /// <summary>Allowed, but it did not work (other 4xx, 5xx, an exception).</summary>
    public const string Failed = "failed";

    /// <summary>The result for an HTTP status.</summary>
    public static string ForStatus(int status) =>
        status < 400 ? Ok : status is 401 or 403 ? Denied : Failed;
}

/// <summary>
/// The person (or process) behind an event, resolved once per request.
/// </summary>
/// <param name="Id">The real actor: an admin who is managing a pharmacy is the
/// admin, not the pharmacy.</param>
/// <param name="PharmacyId">The tenant they acted as: their own pharmacy, the
/// pharmacy an admin is managing, or null for staff work outside any pharmacy.</param>
public sealed record AuditActor(
    string Id, string? Email, string? Role, string Kind, string? PharmacyId)
{
    public static readonly AuditActor Anonymous =
        new("anonymous", null, null, ActorKinds.Pharmacy, null);

    /// <summary>
    /// A user acting on their own account — for the sign-in paths, where the
    /// request carries no token yet and only the service knows who it was.
    /// Staff act for no tenant; a pharmacy user's tenant is their own account.
    /// </summary>
    public static AuditActor ForUser(string id, string? email, UserRole role)
    {
        var staff = role.IsStaff();
        return new(id, email, role.ToString(), staff ? ActorKinds.Admin : ActorKinds.Pharmacy,
            staff ? null : id);
    }

    /// <summary>A process acting on its own account, e.g. <c>"template-seeder"</c>.</summary>
    public static AuditActor System(string name) =>
        new($"system:{name}", null, null, ActorKinds.System, null);
}

/// <summary>
/// The small, safe extra detail an event may carry.
/// </summary>
/// <remarks>
/// Capped in size, and filtered by key and by value: whatever a caller passes,
/// a password, token, card number or list of addresses does not reach the
/// collection. Counts are kept (<c>recipientCount</c>); the lists are dropped.
/// </remarks>
public static class AuditMeta
{
    public const int MaxKeys = 10;
    public const int MaxKeyLength = 40;
    public const int MaxValueLength = 200;

    private static readonly Regex ForbiddenKey = new(
        "password|passwd|secret|token|jwt|authorization|bearer|cookie|card|cvc|cvv|iban|"
        + "sortcode|recipients|addresses|emails|patients|phones",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LooksLikeJwt = new(
        @"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.", RegexOptions.Compiled);

    private static readonly Regex LooksLikeCard = new(
        @"\b(?:\d[ -]?){13,19}\b", RegexOptions.Compiled);

    public static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? meta)
    {
        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        if (meta is null) return clean;

        foreach (var (rawKey, rawValue) in meta)
        {
            if (clean.Count >= MaxKeys) break;
            if (string.IsNullOrWhiteSpace(rawKey) || rawValue is null) continue;

            var key = rawKey.Length > MaxKeyLength ? rawKey[..MaxKeyLength] : rawKey;

            // Keys become document field names: '.' and a leading '$' are
            // operators to Mongo, not names.
            if (key.Contains('.') || key.StartsWith('$')) continue;

            // A count of something sensitive is fine; the thing itself is not.
            var isCount = key.EndsWith("Count", StringComparison.Ordinal)
                          || key.EndsWith("count", StringComparison.Ordinal);
            if (!isCount && ForbiddenKey.IsMatch(key)) continue;

            var value = rawValue;
            if (LooksLikeJwt.IsMatch(value) || LooksLikeCard.IsMatch(value)) value = "[redacted]";
            // More than one address in a value is a list, and lists are counts.
            if (value.Count(c => c == '@') > 1) value = "[list redacted]";
            if (value.Length > MaxValueLength) value = value[..MaxValueLength];

            clean[key] = value;
        }
        return clean;
    }
}
