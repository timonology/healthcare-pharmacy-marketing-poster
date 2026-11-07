using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Acme.WebApi;

/// <summary>Named rate-limit policies. One place, like <see cref="AuthPolicies"/>.</summary>
/// <remarks>
/// <para>
/// Limits are per API instance (in memory). With two instances each ceiling
/// doubles — still a wall for a script, and no shared store to run for a cost
/// and abuse guard. Every number can be overridden under <c>RateLimits:*</c>
/// (the integration suite raises the per-address ones, because all its traffic
/// comes from one address).
/// </para>
/// <para>
/// Anonymous endpoints are limited per client address — the real one, since
/// forwarded headers are processed first. Signed-in endpoints are limited per
/// person.
/// </para>
/// </remarks>
public static class RateLimits
{
    /// <summary>
    /// A pharmacy's stock-photo searches: 20 a minute each. The Pexels key is
    /// shared by every pharmacy, so one loop would empty it for everybody.
    /// </summary>
    public const string StockSearch = "stock-search";

    /// <summary>
    /// Sign-in, sign-up, password reset completion, email verification and
    /// invitation acceptance: 10 a minute per address (security review SEC-05).
    /// A person makes a few; a credential-stuffing run makes thousands. The
    /// per-account lock in <c>ILoginThrottle</c> covers a slow, distributed run.
    /// </summary>
    public const string Auth = "auth";

    /// <summary>
    /// "Forgot password" and "resend verification": 5 per 15 minutes per address.
    /// Each sends an email, so without this anyone could flood an inbox — and
    /// our sending reputation — through us.
    /// </summary>
    public const string AccountEmail = "account-email";

    /// <summary>Screen pairing: 10 a minute per address, so codes cannot be guessed at speed.</summary>
    public const string DisplayClaim = "display-claim";

    /// <summary>
    /// Sharing a poster by email: 10 sends an hour per person (SEC-06). Each send
    /// is also capped in recipients, in the controller.
    /// </summary>
    public const string ShareEmail = "share-email";

    public static IServiceCollection AddAppRateLimits(this IServiceCollection services, IConfiguration config) =>
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Logged, because a refusal used to leave no trace. "Reset emails
                // are not arriving" and "every reset is being refused because all
                // users share the web app's address" look identical from outside
                // — the web app answers 202 either way — and only this line tells
                // them apart. The address is truncated: enough to see that every
                // refusal is the same one, not enough to identify a person.
                var http = context.HttpContext;
                var policy = http.GetEndpoint()?.Metadata
                    .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "(unnamed)";
                http.RequestServices.GetService<ILoggerFactory>()?
                    .CreateLogger("Acme.WebApi.RateLimits")
                    .LogWarning(
                        "Rate limit {Policy} refused {Method} {Path} from {Address}. If every refusal " +
                        "names the same address, users are sharing one — check Edge__ClientIpKey and " +
                        "CLIENT_IP_SIGNING_KEY match.",
                        policy, http.Request.Method, http.Request.Path.Value,
                        Truncated(http.Connection.RemoteIpAddress));
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsync(
                    "{\"error\":\"Too many attempts. Wait a moment and try again.\"}", ct);
            };

            o.AddPolicy(StockSearch, http => Window(PerPerson(http),
                Limit(config, "StockSearchPerMinute", 20), TimeSpan.FromMinutes(1)));

            o.AddPolicy(Auth, http => Window(PerAddress(http),
                Limit(config, "AuthPerMinute", 10), TimeSpan.FromMinutes(1)));

            o.AddPolicy(AccountEmail, http => Window(PerAddress(http),
                Limit(config, "AccountEmailPer15Minutes", 5), TimeSpan.FromMinutes(15)));

            o.AddPolicy(DisplayClaim, http => Window(PerAddress(http),
                Limit(config, "DisplayClaimPerMinute", 10), TimeSpan.FromMinutes(1)));

            o.AddPolicy(ShareEmail, http => Window(PerPerson(http),
                Limit(config, "ShareEmailPerHour", 10), TimeSpan.FromHours(1)));
        });

    private static RateLimitPartition<string> Window(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
        });

    private static int Limit(IConfiguration config, string name, int fallback) =>
        Math.Max(1, config.GetValue($"RateLimits:{name}", fallback));

    /// <summary>1.2.3.4 → 1.2.3.x; IPv6 to its first four groups.</summary>
    public static string Truncated(System.Net.IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var text = address.ToString();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return text[..(text.LastIndexOf('.') + 1)] + "x";
        var groups = text.Split(':');
        return string.Join(':', groups.Take(4)) + ":…";
    }

    private static string PerAddress(HttpContext http) =>
        "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    private static string PerPerson(HttpContext http) =>
        http.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
        ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? PerAddress(http);
}
