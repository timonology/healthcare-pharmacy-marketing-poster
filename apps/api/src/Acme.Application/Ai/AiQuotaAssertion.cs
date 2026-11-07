using System.Security.Cryptography;
using System.Text;

namespace Acme.Application.Ai;

/// <summary>
/// The API's signed word that it checked a pharmacy's allowance before asking
/// ai-service to spend money.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> ai-service gates its expensive endpoints on
/// <c>require_staff</c> — "defence in depth: the .NET API already gates the
/// admin surface, but ai-service is independently reachable inside the
/// cluster". Letting pharmacists design posters means that gate has to open to
/// non-staff, and ai-service is not merely reachable inside the cluster: it is
/// its own public App Service, and published on port 8000 by docker-compose.
/// </para>
/// <para>
/// So an authenticated pharmacist with a valid token could call
/// <c>/api/canvas/generate-template</c> directly, in a loop, and never touch
/// the monthly allowance — which lives in .NET, because that is where
/// subscriptions live. A cost control that the customer can step around is not
/// a cost control.
/// </para>
/// <para>
/// <b>What this is.</b> An HMAC over the caller's own user id, the minute, and
/// the kind of work, keyed with the JWT secret the two services already share.
/// The pharmacist's token still goes with the request — identity and audit stay
/// attached to a real person — and this rides alongside it as proof that the
/// request came through the API's allowance check rather than round the side of
/// it. A pharmacist cannot forge one: an HMAC key is not recoverable from the
/// tokens it has signed.
/// </para>
/// <para>
/// <b>Bound to a minute</b>, so a captured assertion is not a reusable licence
/// to spend. ai-service accepts the current or previous minute, which tolerates
/// clock skew and a request that straddles the boundary without widening the
/// window to anything useful.
/// </para>
/// <para>
/// This is not a replacement for the allowance check; it is what makes the
/// allowance check unavoidable.
/// </para>
/// </remarks>
public static class AiQuotaAssertion
{
    /// <summary>Header the assertion travels in.</summary>
    public const string HeaderName = "X-Quota-Assertion";

    /// <summary>Work this assertion authorises. Must match ai-service's copy.</summary>
    public const string PosterDesignPurpose = "poster-design";

    /// <summary>
    /// A pharmacy's stock photo search, signed after the API's per-user rate
    /// limit. The Pexels key is shared by every pharmacy; ai-service will not
    /// search for a non-staff caller without this.
    /// </summary>
    public const string StockSearchPurpose = "stock-search";

    private static byte[]? _key;

    /// <summary>Called once at startup, from Program.cs.</summary>
    public static void Configure(string secret) => _key = Encoding.UTF8.GetBytes(secret);

    /// <summary>
    /// Signs "<paramref name="userId"/> may spend on <paramref name="purpose"/> now".
    /// </summary>
    /// <returns>
    /// The assertion, or null when no secret is configured — in which case the
    /// caller sends no header and ai-service refuses a non-staff request. That
    /// is the right way round: a misconfigured deployment cannot generate, and
    /// nobody is billed for the confusion.
    /// </returns>
    public static string? Sign(string userId, string purpose, DateTimeOffset now)
    {
        if (_key is null || string.IsNullOrWhiteSpace(userId)) return null;

        using var mac = new HMACSHA256(_key);
        var payload = $"{userId}|{purpose}|{now.ToUnixTimeSeconds() / 60}";
        return Convert.ToBase64String(mac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
