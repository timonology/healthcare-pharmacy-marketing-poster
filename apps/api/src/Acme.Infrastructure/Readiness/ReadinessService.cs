using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Acme.Application.Auth;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Infrastructure.Messaging;
using Acme.Infrastructure.Options;
using Acme.Infrastructure.Persistence;
using Acme.Infrastructure.Social;
using Acme.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Acme.Infrastructure.Readiness;

/// <summary>One dependency's verdict. Never carries a secret.</summary>
/// <param name="Status">ok, warn, fail or skip.</param>
public sealed record ReadinessCheck(string Name, string Status, string Detail, long Ms);

/// <summary>Everything the deployment depends on, checked from inside it.</summary>
/// <param name="WebBaseUrl">The address emails and social connections are built on.</param>
/// <param name="SocialCallbacks">The exact redirect URI per provider — what Meta and Google must allow.</param>
public sealed record ReadinessReport(
    string Status,
    string Environment,
    string? Build,
    string WebBaseUrl,
    IReadOnlyDictionary<string, string> SocialCallbacks,
    IReadOnlyList<ReadinessCheck> Checks);

/// <summary>
/// Can this deployment do everything it is configured to do, right now?
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Production broke three times in a week in ways no
/// request-level test could see from outside: Mailjet keys that came only from
/// a development file, a sender Mailjet had not verified, a social encryption
/// key that disappeared with the same file, and an address change that made
/// Facebook refuse every new connection. Each was a dependency — or the
/// configuration of one — and each could have been asked about directly.
/// </para>
/// <para>
/// <b>What a check may do.</b> Read, and only free reads: a database ping, a
/// probe blob written and deleted, Mailjet's sender list, Stripe's price list,
/// Meta's app token, Google's token endpoint with a deliberately invalid code.
/// Nothing is sent to a customer, nothing is charged, nothing is created that
/// outlives the check.
/// </para>
/// <para>
/// <b>What it may say.</b> Verdicts and setting <i>names</i>. Never a value, a
/// key, a token or a connection string — the report is returned over HTTP.
/// </para>
/// <para>
/// Every check runs in parallel under its own time limit, and none throws: a
/// check that cannot answer is a <c>warn</c> or <c>fail</c> with a sentence,
/// not a 500 that hides the other twelve answers.
/// </para>
/// </remarks>
public sealed class ReadinessService(
    MongoContext mongo,
    IBlobStorageService blobs,
    ITokenCipher cipher,
    IAuthLinkBuilder links,
    IHttpClientFactory http,
    IConfiguration config,
    IHostEnvironment env,
    IOptions<MessagingOptions> messaging,
    IOptions<StripeOptions> stripe,
    IOptions<MetaOptions> meta,
    IOptions<GoogleOptions> google,
    IOptions<LinkedInOptions> linkedIn,
    IOptions<TikTokOptions> tikTok,
    IOptions<XOptions> x,
    IOptions<AiServiceOptions> aiService,
    ILogger<ReadinessService> log)
{
    private static readonly TimeSpan PerCheck = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The HTTP client for the checks, registered without request logging.
    /// </summary>
    /// <remarks>
    /// Several checks carry a credential — Meta's app secret in a query string,
    /// Stripe's and Mailjet's keys in headers — and IHttpClientFactory's default
    /// logging writes request URLs to the log. Same rule as every social client
    /// in this codebase (RemoveAllLoggers).
    /// </remarks>
    public const string HttpClientName = "readiness";

    public async Task<ReadinessReport> RunAsync(CancellationToken ct)
    {
        var checks = await Task.WhenAll(
            Run("database", DatabaseAsync, ct),
            Run("storage", StorageAsync, ct),
            Run("email", EmailAsync, ct),
            Run("payments", StripeAsync, ct),
            Run("facebook_instagram", MetaAsync, ct),
            Run("google", GoogleAsync, ct),
            Run("linkedin", LinkedInAsync, ct),
            Run("tiktok", TikTokAsync, ct),
            Run("x", XAsync, ct),
            Run("social_tokens", SocialTokensAsync, ct),
            Run("public_addresses", PublicAddressesAsync, ct),
            Run("ai_service", AiServiceAsync, ct),
            Run("client_addresses", ClientAddressKeyAsync, ct));

        var overall = checks.Any(c => c.Status == "fail") ? "fail"
            : checks.Any(c => c.Status == "warn") ? "warn"
            : "ok";

        var webBase = links.BuildAppLink("/").TrimEnd('/');
        var callbacks = new Dictionary<string, string>
        {
            ["meta"] = links.BuildAppLink("/social/callback/meta"),
            ["google"] = links.BuildAppLink("/social/callback/google"),
            ["youtube"] = links.BuildAppLink("/social/callback/youtube"),
            ["linkedin"] = links.BuildAppLink("/social/callback/linkedin"),
            ["tiktok"] = links.BuildAppLink("/social/callback/tiktok"),
            ["x"] = links.BuildAppLink("/social/callback/x"),
        };

        var build = Environment.GetEnvironmentVariable("BUILD_ID");
        return new ReadinessReport(
            overall, env.EnvironmentName, string.IsNullOrWhiteSpace(build) ? null : build,
            webBase, callbacks, checks);
    }

    private async Task<ReadinessCheck> Run(
        string name, Func<CancellationToken, Task<(string Status, string Detail)>> check, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerCheck);
        try
        {
            var (status, detail) = await check(cts.Token);
            return new ReadinessCheck(name, status, detail, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ReadinessCheck(name, "fail", $"no answer within {PerCheck.TotalSeconds:0}s", watch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            // Logged in full here; the report gets the exception type and a
            // trimmed message only, in case a provider echoes something back.
            log.LogWarning(ex, "Readiness check {Check} threw", name);
            var message = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            return new ReadinessCheck(name, "fail", $"{ex.GetType().Name}: {message}", watch.ElapsedMilliseconds);
        }
    }

    private bool IsProduction => env.IsProduction();

    /* ── checks ──────────────────────────────────────────────────────── */

    private async Task<(string, string)> DatabaseAsync(CancellationToken ct)
    {
        await mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
        var users = await mongo.Database.GetCollection<BsonDocument>("users")
            .EstimatedDocumentCountAsync(cancellationToken: ct);
        return users == 0
            ? ("warn", $"reachable, database '{mongo.DatabaseName}' has no accounts — is it the right database?")
            : ("ok", $"reachable, {users} account(s) in '{mongo.DatabaseName}'");
    }

    private async Task<(string, string)> StorageAsync(CancellationToken ct)
    {
        // Write, read back, delete: the three things the product does with blobs.
        var key = $"readiness/probe-{Guid.NewGuid():N}.txt";
        var payload = Encoding.UTF8.GetBytes("readiness probe");
        await blobs.UploadAsync(key, new MemoryStream(payload), "text/plain", ct);
        try
        {
            await using var read = await blobs.DownloadAsync(key, ct);
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy, ct);
            if (!copy.ToArray().AsSpan().SequenceEqual(payload))
                return ("fail", "a blob written to storage did not read back the same");
        }
        finally
        {
            try { await blobs.DeleteAsync(key, CancellationToken.None); } catch { /* best effort */ }
        }
        return ("ok", "write, read and delete work");
    }

    private async Task<(string, string)> EmailAsync(CancellationToken ct)
    {
        var o = messaging.Value;
        if (o.UseMock)
            return (IsProduction ? "fail" : "warn", "Messaging__UseMock is on: email is logged, never sent");
        if (!string.Equals(o.Provider, "mailjet", StringComparison.OrdinalIgnoreCase))
            return ("warn", $"provider is '{o.Provider}', which this check cannot verify");
        if (string.IsNullOrWhiteSpace(o.Mailjet.ApiKey) || string.IsNullOrWhiteSpace(o.Mailjet.SecretKey))
            return ("fail", "Messaging__Mailjet__ApiKey / SecretKey are not set");

        var (verified, detail) = await MailjetProbe.SenderAsync(http.CreateClient(HttpClientName), o, ct);
        return (verified switch { true => "ok", false => "fail", null => "warn" }, detail);
    }

    private async Task<(string, string)> StripeAsync(CancellationToken ct)
    {
        var o = stripe.Value;
        if (!o.Enabled) return ("skip", "Stripe is disabled (Stripe__Enabled)");
        if (string.IsNullOrWhiteSpace(o.SecretKey)) return ("fail", "Stripe__SecretKey is not set");

        var problems = new List<string>();
        var testKey = o.SecretKey.StartsWith("sk_test_", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(o.WebhookSecret))
            problems.Add("Stripe__WebhookSecret is not set, so paid plans never activate");

        var prices = new Dictionary<string, string>
        {
            ["Stripe__Prices__StarterMonthly"] = o.Prices.StarterMonthly,
            ["Stripe__Prices__StarterYearly"] = o.Prices.StarterYearly,
            ["Stripe__Prices__ProMonthly"] = o.Prices.ProMonthly,
            ["Stripe__Prices__ProYearly"] = o.Prices.ProYearly,
        };
        foreach (var pack in o.CreditPacks)
            prices[$"credit pack {pack.Key}"] = pack.PriceId;

        var client = http.CreateClient(HttpClientName);
        foreach (var (setting, id) in prices)
        {
            if (string.IsNullOrWhiteSpace(id)) { problems.Add($"{setting} is not set"); continue; }

            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.stripe.com/v1/prices/{Uri.EscapeDataString(id)}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.SecretKey);
            using var res = await client.SendAsync(req, ct);

            if ((int)res.StatusCode is 401 or 403)
                return ("fail", "Stripe refused Stripe__SecretKey");
            if (!res.IsSuccessStatusCode)
            {
                problems.Add($"{setting} is not a price on this Stripe account ({(int)res.StatusCode})");
                continue;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("active", out var active) && !active.GetBoolean())
                problems.Add($"{setting} is archived in Stripe");
        }

        if (problems.Count > 0) return ("fail", string.Join("; ", problems));

        // Where Stripe sends people back. Sign-in is per address, so a return
        // to another host arrives signed out — exactly what a domain move leaves
        // behind when only Public__WebBaseUrl is updated.
        var site = Uri.TryCreate(links.BuildAppLink("/"), UriKind.Absolute, out var w) ? w.Host : "";
        var strays = new[]
            {
                ("Stripe__SuccessUrl", o.SuccessUrl), ("Stripe__CancelUrl", o.CancelUrl),
                ("Stripe__PortalReturnUrl", o.PortalReturnUrl),
            }
            .Where(p => Uri.TryCreate(p.Item2, UriKind.Absolute, out var u)
                        && !string.Equals(u.Host, site, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Item1)
            .ToList();
        if (strays.Count > 0 && site.Length > 0)
            return ("warn", $"prices valid, but {string.Join(", ", strays)} return people to a different " +
                            $"address from {site}, where they arrive signed out");
        return testKey && IsProduction
            ? ("warn", "every price is valid, but Stripe__SecretKey is a TEST key: no real payment is taken")
            : ("ok", $"{prices.Count} price(s) valid{(testKey ? " (test mode)" : "")}");
    }

    private async Task<(string, string)> MetaAsync(CancellationToken ct)
    {
        var o = meta.Value;
        if (!o.IsConfigured) return ("warn", "Social__Meta__AppId / AppSecret are not set: Facebook and Instagram cannot connect");

        // An app access token is what Meta issues for a valid id and secret,
        // free and without any user. A refusal means the pair is wrong.
        var url = $"https://graph.facebook.com/{o.GraphVersion}/oauth/access_token" +
                  $"?client_id={Uri.EscapeDataString(o.AppId)}&client_secret={Uri.EscapeDataString(o.AppSecret)}" +
                  "&grant_type=client_credentials";
        var client = http.CreateClient(HttpClientName);
        using var res = await client.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode)
            return ("fail", $"Meta refused Social__Meta__AppId / AppSecret ({(int)res.StatusCode})");

        var callback = links.BuildAppLink("/social/callback/meta");
        var site = Uri.TryCreate(callback, UriKind.Absolute, out var c) ? c.Host : "";

        // A laptop's localhost is never in a production app's App Domains, and
        // need not be: the domain check means something only for a real address.
        if (LooksLocal(callback))
            return ("ok", $"app credentials accepted; {site} is a local address, so App Domains was not checked");

        // App Domains. Meta lists no API for the exact Valid OAuth Redirect
        // URIs, but the domain must be in App Domains before Meta accepts any
        // redirect on it — and a domain move that forgets it is precisely the
        // 22 October failure. Read with the app token just issued.
        string? appToken = null;
        try
        {
            using var tokenDoc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            appToken = tokenDoc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { /* reported below as unreadable */ }

        var domains = new List<string>();
        if (appToken is not null)
        {
            using var app = await client.GetAsync(
                $"https://graph.facebook.com/{o.GraphVersion}/{Uri.EscapeDataString(o.AppId)}" +
                $"?fields=app_domains&access_token={Uri.EscapeDataString(appToken)}", ct);
            if (app.IsSuccessStatusCode)
            {
                // An unexpected shape is "could not read", a warning below —
                // not an exception that would fail the check outright.
                try
                {
                    using var appDoc = System.Text.Json.JsonDocument.Parse(await app.Content.ReadAsStringAsync(ct));
                    if (appDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && appDoc.RootElement.TryGetProperty("app_domains", out var list)
                        && list.ValueKind == System.Text.Json.JsonValueKind.Array)
                        domains.AddRange(list.EnumerateArray()
                            .Where(d => d.ValueKind == System.Text.Json.JsonValueKind.String)
                            .Select(d => d.GetString()!.Trim())
                            .Where(d => d.Length > 0));
                }
                catch (System.Text.Json.JsonException) { /* left empty: reported as unreadable */ }
            }
        }

        var manual = $"{callback} must also be in Facebook Login → Valid OAuth Redirect URIs (Meta offers no way to read that list)";
        if (domains.Count == 0)
            return ("warn", $"app credentials accepted, but App Domains could not be read; confirm {site} is listed there. {manual}");

        // "example.com" in App Domains covers www.example.com too.
        var listed = domains.Any(d =>
            string.Equals(site, d, StringComparison.OrdinalIgnoreCase)
            || site.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        return listed
            ? ("ok", $"app credentials accepted; {site} is in App Domains. {manual}")
            : ("fail", $"{site} is not in the Meta app's App Domains ({string.Join(", ", domains)}), so Facebook " +
                       $"refuses to return people to it. Add it under App Settings → Basic → App Domains. {manual}");
    }

    private async Task<(string, string)> GoogleAsync(CancellationToken ct)
    {
        var o = google.Value;
        if (!o.IsConfigured) return ("warn", "Social__Google__ClientId / ClientSecret are not set: Google cannot connect");

        if (o.ClientId.Contains("://", StringComparison.Ordinal)
            || !o.ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            return ("fail", "Social__Google__ClientId is not a Google client id (it should end .apps.googleusercontent.com and have no http://)");

        // Exchanging a code that cannot exist: Google says invalid_grant when
        // it recognises the client, invalid_client when it does not. Free, and
        // no user is involved.
        using var res = await http.CreateClient(HttpClientName).PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = "readiness-probe-not-a-real-code",
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret,
                ["redirect_uri"] = links.BuildAppLink("/social/callback/google"),
                ["grant_type"] = "authorization_code",
            }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (body.Contains("invalid_client", StringComparison.Ordinal) || body.Contains("unauthorized_client", StringComparison.Ordinal))
            return ("fail", "Google refused Social__Google__ClientId / ClientSecret");

        // Developer tokens were sunset on 10 September 2026: Ads access now
        // belongs to this client's Cloud project. A token left set is ignored
        // today and may be rejected by a later API version.
        return !string.IsNullOrWhiteSpace(o.AdsDeveloperToken)
            ? ("warn", "client accepted; Social__Google__AdsDeveloperToken is set, but Google sunset developer tokens (10 Sep 2026) — remove it")
            : ("ok", "client accepted; Google Ads access comes from this client's Cloud project (Basic access needed for real ad accounts)");
    }

    private async Task<(string, string)> LinkedInAsync(CancellationToken ct)
    {
        var o = linkedIn.Value;
        if (string.IsNullOrWhiteSpace(o.ClientId) || string.IsNullOrWhiteSpace(o.ClientSecret))
            return ("warn", "Social__LinkedIn__ClientId / ClientSecret are not set: LinkedIn cannot connect");
        if (!o.ProfilesEnabled && !o.PagesEnabled)
            return ("warn", "Social__LinkedIn__ProfilesEnabled and PagesEnabled are both false: LinkedIn is switched off");

        // Same trick as Google's: redeem a code that cannot exist. LinkedIn
        // says invalid_client for a wrong id or secret, and something else
        // (invalid_request: the code does not match) for a pair it knows.
        using var res = await http.CreateClient(HttpClientName).PostAsync("https://www.linkedin.com/oauth/v2/accessToken",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = "readiness-probe-not-a-real-code",
                ["redirect_uri"] = links.BuildAppLink("/social/callback/linkedin"),
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret,
            }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (body.Contains("invalid_client", StringComparison.Ordinal) || res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return ("fail", "LinkedIn refused Social__LinkedIn__ClientId / ClientSecret");
        if ((int)res.StatusCode >= 500)
            return ("warn", $"LinkedIn sign-in answered {(int)res.StatusCode}; could not check the app credentials");

        var offers = o.ProfilesEnabled && o.PagesEnabled ? "profiles and Pages"
            : o.PagesEnabled ? "Pages only" : "profiles (Pages are off until LinkedIn approves Community Management)";
        return ("ok", $"app credentials accepted; offering {offers}; API version {o.ApiVersion}");
    }

    private async Task<(string, string)> TikTokAsync(CancellationToken ct)
    {
        var o = tikTok.Value;
        if (!o.HasCredentials)
            return ("warn", "Social__TikTok__ClientKey / ClientSecret are not set: TikTok cannot connect");
        if (!o.Enabled)
            return ("warn", "Social__TikTok__Enabled is false: TikTok is switched off");

        // A code that cannot exist: TikTok says invalid_client for a key or
        // secret it does not know, something else for a pair it does.
        using var res = await http.CreateClient(HttpClientName).PostAsync("https://open.tiktokapis.com/v2/oauth/token/",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_key"] = o.ClientKey,
                ["client_secret"] = o.ClientSecret,
                ["code"] = "readiness-probe-not-a-real-code",
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = links.BuildAppLink("/social/callback/tiktok"),
            }), ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (body.Contains("invalid_client", StringComparison.Ordinal) || body.Contains("non_existent_client", StringComparison.Ordinal))
            return ("fail", "TikTok refused Social__TikTok__ClientKey / ClientSecret");
        if ((int)res.StatusCode >= 500)
            return ("warn", $"TikTok sign-in answered {(int)res.StatusCode}; could not check the app credentials");
        return ("ok", "app credentials accepted");
    }

    private async Task<(string, string)> XAsync(CancellationToken ct)
    {
        var o = x.Value;
        if (!o.HasCredentials)
            return ("warn", "Social__X__ClientId / ClientSecret are not set: X cannot connect");
        if (!o.Enabled)
            return ("warn", "Social__X__Enabled is false: X is switched off");

        // A code that cannot exist, with the app's Basic credentials: X says
        // unauthorized_client (401) for an id or secret it does not know, and
        // invalid_request for a pair it does. Free — no API credits used.
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.com/2/oauth2/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = "readiness-probe-not-a-real-code",
                ["redirect_uri"] = links.BuildAppLink("/social/callback/x"),
                ["code_verifier"] = "readiness-probe-verifier-readiness-probe-verifier",
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(o.ClientId) + ":" + Uri.EscapeDataString(o.ClientSecret))));
        using var res = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        var body = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized || body.Contains("unauthorized_client", StringComparison.Ordinal))
            return ("fail", "X refused Social__X__ClientId / ClientSecret (use the OAuth 2.0 Client ID and Secret, and make the app a Web App)");
        if ((int)res.StatusCode >= 500)
            return ("warn", $"X sign-in answered {(int)res.StatusCode}; could not check the app credentials");
        return ("ok", "app credentials accepted (posting also needs API credits on the X developer account)");
    }

    private async Task<(string, string)> SocialTokensAsync(CancellationToken ct)
    {
        // Not just "is a key configured": can the key open what is stored? A
        // new key under the old version number passes every other test and
        // breaks every existing connection.
        var docs = await mongo.Collection<SocialConnectionDocument>("social_connections")
            // Live connections only. One awaiting re-authorisation or revoked is
            // unusable anyway, and after a key rotation its old token may
            // legitimately use a retired key — not a broken deployment.
            //
            // Written as "not those two" rather than "== Active": documents
            // from before the status field have no `status` at all, and an
            // equality test in the database does not match a missing field.
            .Find(d => d.AccessToken != null
                       && d.Status != (int)Acme.Domain.Social.ConnectionStatus.NeedsReauth
                       && d.Status != (int)Acme.Domain.Social.ConnectionStatus.Revoked)
            .SortByDescending(d => d.UpdatedAtUtc)
            .Limit(25)
            .ToListAsync(ct);

        if (!cipher.IsConfigured)
            return docs.Count == 0
                ? ("warn", "Social__Encryption__Keys is not set: nobody can connect a social account")
                : ("fail", $"Social__Encryption__Keys is not set: nobody can connect, and {docs.Count}+ stored connection(s) are unreadable");

        if (docs.Count == 0) return ("ok", "encryption key set; no stored connections to open yet");

        var unreadable = 0;
        foreach (var d in docs)
        {
            try
            {
                var s = d.AccessToken!;
                cipher.Decrypt(EncryptedSecret.Create(s.CipherText, s.Nonce, s.Tag, s.KeyVersion));
            }
            catch
            {
                unreadable++;
            }
        }

        return unreadable == 0
            ? ("ok", $"the {docs.Count} most recent stored connection(s) open with the configured key")
            : ("fail", $"{unreadable} of the {docs.Count} most recent stored connection(s) cannot be opened — " +
                       "the encryption key is not the one they were saved with");
    }

    private Task<(string, string)> PublicAddressesAsync(CancellationToken ct)
    {
        var problems = new List<string>();
        var web = links.BuildAppLink("/");
        if (LooksLocal(web))
            problems.Add($"links point at {web} — set Public__WebBaseUrl to the public web address");

        var api = config["Public:ApiBaseUrl"];
        if (string.IsNullOrWhiteSpace(api) || LooksLocal(api))
            problems.Add("Public__ApiBaseUrl is not the public API address: email images and unsubscribe links will not work");

        if (string.IsNullOrWhiteSpace(config["Public:WebBaseUrl"]))
            problems.Add("Public__WebBaseUrl is not set (falling back to Cors__Origins__0) — set it, so a CORS change cannot move every link");

        // On a laptop, localhost links are correct; the same links on a
        // deployment send every email recipient to their own machine.
        if (env.IsDevelopment() && problems.Count > 0)
            return Task.FromResult(("ok", $"{env.EnvironmentName}: local addresses are expected ({web.TrimEnd('/')})"));

        var status = problems.Count == 0 ? "ok" : problems.Any(p => p.Contains("point at")) ? "fail" : "warn";
        return Task.FromResult((status, problems.Count == 0 ? $"web {web.TrimEnd('/')}, api {api}" : string.Join("; ", problems)));
    }

    private async Task<(string, string)> AiServiceAsync(CancellationToken ct)
    {
        var baseUrl = aiService.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl)) return ("fail", "AiService__BaseUrl is not set: no AI design and no video");

        using var res = await http.CreateClient(HttpClientName).GetAsync($"{baseUrl.TrimEnd('/')}/health", ct);
        if (!res.IsSuccessStatusCode) return ("fail", $"{baseUrl} answered {(int)res.StatusCode}");

        using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var build = doc.RootElement.TryGetProperty("build", out var b) ? b.GetString() : null;
        return ("ok", $"reachable from the API{(build is null ? "" : $", build {build}")}");
    }

    private Task<(string, string)> ClientAddressKeyAsync(CancellationToken ct) =>
        Task.FromResult(string.IsNullOrWhiteSpace(config["Edge:ClientIpKey"])
            ? (IsProduction ? "warn" : "skip",
               "Edge__ClientIpKey is not set: client addresses can be spoofed by calling the API directly")
            : ("ok", "set (whether the web app signs with the same key is checked through the web app)"));

    private static bool LooksLocal(string url) =>
        url.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        || url.Contains("127.0.0.1", StringComparison.Ordinal)
        || url.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
}
