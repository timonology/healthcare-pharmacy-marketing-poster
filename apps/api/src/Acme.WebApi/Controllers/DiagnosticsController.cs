using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Acme.WebApi.Controllers;

/// <summary>
/// Read-only diagnostics for verifying that the deployed app sees the
/// config values it's supposed to. Deliberately anonymous — everything
/// returned here is non-secret. Only exposes the exact keys the app
/// reads at runtime, never the raw configuration tree.
/// </summary>
[ApiController]
[Route("api/diagnostics")]
[AllowAnonymous]
public sealed class DiagnosticsController(
    IConfiguration config,
    IHostEnvironment env) : ControllerBase
{
    /// <summary>
    /// GET /api/diagnostics/jwt — fingerprint of the signing config.
    /// </summary>
    /// <remarks>
    /// Exists so a smoke test can prove the API and ai-service agree without
    /// scraping two sets of startup logs. A mismatch here makes every
    /// cross-service call fail with a 401 that reads like a login problem, and
    /// it cost a great deal of time to find by hand.
    ///
    /// Development only. The fingerprint is a truncated SHA-256 and cannot be
    /// reversed, but it would still let someone *confirm* a guessed secret, and
    /// that is not a capability worth handing out on a public endpoint for the
    /// sake of a local convenience.
    /// </remarks>
    [HttpGet("jwt")]
    public IActionResult GetJwt()
    {
        if (!env.IsDevelopment()) return NotFound();

        var secret = config["Jwt:Secret"] ?? string.Empty;
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret));

        return Ok(new
        {
            secretFingerprint = Convert.ToHexString(digest)[..12].ToLowerInvariant(),
            issuer = config["Jwt:Issuer"] ?? "(missing)",
            audience = config["Jwt:Audience"] ?? "(missing)",
        });
    }

    /// <summary>
    /// GET /api/diagnostics/config
    /// Returns the runtime values of the config keys that affect
    /// user-visible behaviour (e.g. Public:ApiBaseUrl used in outbound
    /// email links).
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        // Public in Development only. Outside it, this names the storage
        // account, the CORS origin and which integrations are mocked — useful
        // reconnaissance (security review SEC-11) — so only a SuperAdmin sees
        // it, and anyone else gets the same 404 as a path that does not exist.
        if (!env.IsDevelopment() && !User.IsInRole(nameof(Acme.Domain.Users.UserRole.SuperAdmin)))
            return NotFound();

        return Ok(new
        {
            publicApiBaseUrl = config["Public:ApiBaseUrl"] ?? "(missing — using localhost fallback)",
            corsOrigin0 = config["Cors:Origins:0"] ?? "(missing)",
            messagingProvider = config["Messaging:Provider"] ?? "(missing)",
            messagingUseMock = config["Messaging:UseMock"] ?? "(missing)",
            sonarUseMock = config["Sonar:UseMock"] ?? "(missing)",
            blobAccount = DescribeBlobTarget(config["AzureBlob:ConnectionString"]),
            blobContainer = config["AzureBlob:Container"] ?? "(missing)",
            aspNetCoreEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "(unset)",
            serverTimeUtc = DateTime.UtcNow.ToString("O"),
            social = DescribeSocial(),
        });
    }

    /// <summary>
    /// Which social credentials the API can see — "set" or "missing", never a
    /// value — and what that makes available.
    /// </summary>
    /// <remarks>
    /// Exists for "Google Ads is not available on this server yet" after the
    /// settings were added. The page cannot say which one is missing (a
    /// pharmacy sees it), so this does, for an administrator. It also names
    /// settings found under a near-miss key — "GoogleAds__DeveloperToken"
    /// instead of "Social__Google__AdsDeveloperToken" — because a value under
    /// the wrong name looks, in the portal, exactly like one under the right.
    /// </remarks>
    private object DescribeSocial()
    {
        string State(string key) =>
            string.IsNullOrWhiteSpace(config[key]) ? "missing" : "set";

        var clientId = State("Social:Google:ClientId");
        var clientSecret = State("Social:Google:ClientSecret");
        var devToken = State("Social:Google:AdsDeveloperToken");
        var encryption = string.IsNullOrWhiteSpace(config["Social:Encryption:Keys:0:Key"]) ? "missing" : "set";

        string[] nearMisses =
        [
            "Google:ClientId", "Google:ClientSecret", "Google:AdsDeveloperToken", "Google:DeveloperToken",
            "GoogleAds:DeveloperToken", "GoogleAds:ClientId", "GoogleAds:ClientSecret",
            "Social:Google:DeveloperToken", "Social:GoogleAds:DeveloperToken",
            "Social:GoogleAds:ClientId", "Social:GoogleAds:ClientSecret",
            "Social:Google:AdsDevToken", "Social:Google:Ads:DeveloperToken",
        ];

        var missing = new List<string>();
        if (encryption == "missing") missing.Add("Social__Encryption__Keys__0__Key");
        if (clientId == "missing") missing.Add("Social__Google__ClientId");
        if (clientSecret == "missing") missing.Add("Social__Google__ClientSecret");
        // Not required since Google sunset developer tokens (10 September
        // 2026): access comes from the OAuth client's Cloud project.

        return new
        {
            encryptionKey = encryption,
            googleClientId = clientId,
            googleClientSecret = clientSecret,
            googleAdsDeveloperToken = devToken == "set" ? "set (legacy, ignored by Google; remove it)" : "not needed",
            googleAdsApiVersion = config["Social:Google:AdsApiVersion"] ?? "(default)",
            googleAdsValidateOnly = config["Social:Google:AdsValidateOnly"] ?? "(default: false)",
            googleAdsAvailable = missing.Count == 0
                && !string.Equals(config["Social:Google:AdsEnabled"], "false", StringComparison.OrdinalIgnoreCase),
            googleAdsMissing = missing,
            settingsUnderWrongName = nearMisses
                .Where(k => !string.IsNullOrWhiteSpace(config[k]))
                .Select(k => k.Replace(":", "__"))
                .ToList(),
            metaAppId = State("Social:Meta:AppId"),
            metaAppSecret = State("Social:Meta:AppSecret"),
        };
    }

    /// <summary>
    /// Reports which storage account the API will actually talk to, without
    /// ever echoing the account key. Exists because a connection string left
    /// at the "UseDevelopmentStorage=true" default fails at *download* time
    /// with a confusing "Connection refused (127.0.0.1:10000)" deep in the
    /// Azure SDK retry stack — by which point the request has already burned
    /// 20+ seconds. Surfacing the target up front turns that into a
    /// two-second check.
    /// </summary>
    private static string DescribeBlobTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "(missing — set AzureBlob__ConnectionString)";

        if (connectionString.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase))
            return "AZURITE EMULATOR (127.0.0.1:10000) — wrong for production";

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("AccountName", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }

        return "(set, but no AccountName found)";
    }
}
