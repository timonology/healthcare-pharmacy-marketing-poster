using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/*
 * X (formerly Twitter): connect an account with OAuth 2.0 (authorization code
 * with PKCE), post a poster or video with the v2 media upload and POST
 * /2/tweets.
 *
 * The credential. X access tokens last two hours; with offline.access a
 * refresh token comes too, and every refresh returns a NEW refresh token — the
 * old one stops working. So, as for TikTok, the stored credential is a small
 * JSON blob (access token, when it lapses, refresh token), any call that
 * refreshes hands the new blob back, and the caller saves it at once.
 *
 * PKCE without storing anything. The code verifier is derived from the app's
 * client secret and the flow's state (HMAC). The state is public, the secret
 * is not, so nobody else can work out the verifier — and the callback can
 * recompute it without a second store.
 *
 * Billing. X's API is pay-per-use: each post and media upload is charged to
 * the developer account's credits. Out of credits, X answers 402.
 */

/// <summary>X app credentials (console.x.com → your app → Keys and tokens → OAuth 2.0).</summary>
public sealed class XOptions
{
    public const string SectionName = "Social:X";

    /// <summary>The OAuth 2.0 Client ID — not the API key (consumer key) of OAuth 1.0a.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The OAuth 2.0 Client Secret. The app must be a confidential client ("Web App").</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Set false to hide X while keeping the credentials.</summary>
    public bool Enabled { get; set; } = true;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsConfigured => HasCredentials && Enabled;
}

/// <summary>What is stored as an X connection's credential.</summary>
public sealed record XCredential(string AccessToken, long AccessExpiresUnix, string RefreshToken)
{
    public string Serialize() => JsonSerializer.Serialize(new { a = AccessToken, ax = AccessExpiresUnix, r = RefreshToken });

    public static XCredential? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var a = root.TryGetProperty("a", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString() : null;
            var r = root.TryGetProperty("r", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString() : null;
            var ax = root.TryGetProperty("ax", out var xv) && xv.TryGetInt64(out var x) ? x : 0;
            return string.IsNullOrWhiteSpace(r) ? null : new XCredential(a ?? "", ax, r!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Usable for a few more minutes at least.</summary>
    public bool AccessValidAt(DateTime nowUtc) =>
        !string.IsNullOrWhiteSpace(AccessToken)
        && DateTimeOffset.FromUnixTimeSeconds(AccessExpiresUnix).UtcDateTime > nowUtc.AddMinutes(5);

    /// <summary>Never print the tokens.</summary>
    public override string ToString() => $"XCredential {{ AccessExpiresUnix = {AccessExpiresUnix} }}";
}

/// <summary>
/// X's JSON. v2 refusals are problem documents (<c>title</c>, <c>detail</c>,
/// <c>status</c>), sometimes with an <c>errors</c> list; OAuth refusals are
/// <c>{"error","error_description"}</c>.
/// </summary>
internal static class XJson
{
    public static JsonDocument? Parse(string body)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch (JsonException) { return null; }
    }

    public static string? Get(JsonElement el, params string[] path)
    {
        foreach (var key in path)
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null,
        };
    }

    public static string? Get(string body, params string[] path)
    {
        using var doc = Parse(body);
        return doc is null ? null : Get(doc.RootElement, path);
    }

    /// <summary>The most specific human-readable reason in a refusal, or null.</summary>
    public static string? Message(string body)
    {
        using var doc = Parse(body);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = doc.RootElement;

        if (Get(root, "detail") is { Length: > 0 } detail) return detail;
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            foreach (var e in errors.EnumerateArray())
                if ((Get(e, "message") ?? Get(e, "detail")) is { Length: > 0 } m) return m;
        return Get(root, "error_description") is { Length: > 0 } d ? d
            : Get(root, "title") is { Length: > 0 } t ? t
            : Get(root, "error");
    }

    /// <summary>The problem title (for example <c>CreditsDepleted</c>), or null.</summary>
    public static string? Title(string body) => Get(body, "title");
}

/// <summary>Turns a stored X credential into a usable access token, refreshing when it has lapsed.</summary>
internal static class XTokens
{
    public const string TokenUrl = "https://api.x.com/2/oauth2/token";
    public const string RevokeUrl = "https://api.x.com/2/oauth2/revoke";

    public sealed class RefreshFailed(string message, bool isAuthFailure, bool isRetryable) : Exception(message)
    {
        public bool IsAuthFailure { get; } = isAuthFailure;
        public bool IsRetryable { get; } = isRetryable;
    }

    /// <summary>HTTP Basic with the client id and secret: how a confidential client authenticates to X.</summary>
    public static AuthenticationHeaderValue ClientAuth(XOptions options) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Uri.EscapeDataString(options.ClientId) + ":" + Uri.EscapeDataString(options.ClientSecret))));

    public static HttpRequestMessage TokenRequest(XOptions options, IDictionary<string, string> form) =>
        new(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
            Headers = { Authorization = ClientAuth(options) },
        };

    public static async Task<(string AccessToken, RenewedCredential? Renewed)> AccessTokenAsync(
        HttpClient http, XOptions options, string stored, CancellationToken ct)
    {
        var credential = XCredential.Parse(stored)
            ?? throw new RefreshFailed("The stored X sign-in could not be read. Reconnect it in Social & Ads.", true, false);

        if (credential.AccessValidAt(DateTime.UtcNow)) return (credential.AccessToken, null);

        HttpResponseMessage response;
        try
        {
            using var request = TokenRequest(options, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = credential.RefreshToken,
            });
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new RefreshFailed("Could not reach X to sign in.", false, true);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var access = XJson.Get(body, "access_token");
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(access))
            {
                var expiresIn = long.TryParse(XJson.Get(body, "expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : 7200;
                // X rotates the refresh token on every refresh; keep the old
                // one only if (unexpectedly) no new one came back.
                var refresh = XJson.Get(body, "refresh_token") is { Length: > 0 } rt ? rt : credential.RefreshToken;
                var renewed = new XCredential(access!, DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds(), refresh);
                // X does not say when a refresh token lapses: no expiry stored.
                return (access!, new RenewedCredential(renewed.Serialize(), null));
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new RefreshFailed("X sign-in is having problems.", false, true);

            // A used, revoked or expired refresh token, or the app's
            // credentials changed. Only reconnecting fixes the first three.
            if (XJson.Get(body, "error") == "unauthorized_client")
                throw new RefreshFailed("X refused this app's credentials. An administrator needs to check Social__X__ClientId / ClientSecret.", false, false);

            throw new RefreshFailed(
                "The connection to X is no longer valid. Reconnect it in Social & Ads.",
                isAuthFailure: true, isRetryable: false);
        }
    }

    public static HttpRequestMessage Bearer(HttpMethod method, string url, string accessToken, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}

/// <summary>What an X refusal means, in the pharmacy's words.</summary>
internal static class XRefusal
{
    public sealed record Meaning(string Message, bool IsAuthFailure, bool IsRetryable);

    public const string CreditsMessage =
        "Marketing25's X API credits are used up, so nothing can be posted to X until an administrator tops them up. Retry after that.";

    public const string AppNotEnrolledMessage =
        "X refused Marketing25's app: it must belong to a Project with API access in the X Developer Console. " +
        "Nothing is wrong with this account — an administrator needs to fix the app, then Retry.";

    /// <summary>X's 403 for an app not attached to a Project (or otherwise barred) — ours to fix, not the pharmacy's.</summary>
    public static bool IsAppRefused(string body) =>
        body.Contains("client-not-enrolled", StringComparison.OrdinalIgnoreCase)
        || body.Contains("client-forbidden", StringComparison.OrdinalIgnoreCase);

    public static Meaning For(HttpStatusCode status, string body)
    {
        var message = XJson.Message(body);
        var title = XJson.Title(body) ?? "";

        if (status == HttpStatusCode.Forbidden && IsAppRefused(body))
            return new(AppNotEnrolledMessage, false, false);

        if (status == HttpStatusCode.Unauthorized)
            return new("X did not accept this account's sign-in. Reconnect it in Social & Ads.", true, false);
        if (status == HttpStatusCode.PaymentRequired || title.Contains("Credits", StringComparison.OrdinalIgnoreCase))
            return new(CreditsMessage, false, false);
        if (status == HttpStatusCode.TooManyRequests)
            return new("X asked us to slow down.", false, true);
        if ((int)status >= 500)
            return new("X is having problems.", false, true);

        if (status == HttpStatusCode.Forbidden && message is { } m)
        {
            if (m.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                return new("X refused the post because the same text is already on this account. Change the caption and retry.", false, false);
            if (m.Contains("scope", StringComparison.OrdinalIgnoreCase) || m.Contains("permission", StringComparison.OrdinalIgnoreCase))
                return new("Permission to post to X was not given. Reconnect it in Social & Ads and allow posting.", true, false);
        }

        return new(message is { Length: > 0 }
            ? $"X refused the post: {message}"
            : $"X refused the post ({((int)status).ToString(CultureInfo.InvariantCulture)}).",
            false, false);
    }
}

/// <summary>X sign-in: OAuth 2.0 with PKCE. One account per sign-in.</summary>
/// <remarks>
/// Scopes: <c>tweet.read tweet.write users.read</c> (X requires the read
/// scopes alongside posting), <c>media.write</c> (uploads) and
/// <c>offline.access</c> (a refresh token, so posts can go out when nobody is
/// signed in). Disconnecting revokes the grant at X — unless the same X user
/// is still connected in another pharmacy (see SocialConnectionService).
/// </remarks>
public sealed class XOAuthProvider(
    IHttpClientFactory httpFactory,
    IOptions<XOptions> options,
    ILogger<XOAuthProvider> log) : ISocialOAuthProvider
{
    public const string AuthorizeUrl = "https://x.com/i/oauth2/authorize";
    public const string MeUrl = "https://api.x.com/2/users/me?user.fields=profile_image_url";
    public static readonly string[] Scopes = { "tweet.read", "tweet.write", "users.read", "media.write", "offline.access" };

    private readonly XOptions _options = options.Value;

    public SocialOAuthProviderKind Kind => SocialOAuthProviderKind.X;

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// The PKCE code verifier for a flow: base64url(HMAC-SHA256(client secret,
    /// "x-pkce:" + state)) — 43 characters, inside RFC 7636's 43–128.
    /// </summary>
    public static string Verifier(string clientSecret, string state)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(clientSecret));
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes("x-pkce:" + state)));
    }

    /// <summary>The S256 challenge: base64url(SHA-256(verifier)).</summary>
    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string BuildAuthorizationUrl(string state, string redirectUri) =>
        AuthorizeUrl
        + "?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(_options.ClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&scope=" + Uri.EscapeDataString(string.Join(" ", Scopes))
        + "&state=" + Uri.EscapeDataString(state)
        + "&code_challenge=" + Challenge(Verifier(_options.ClientSecret, state))
        + "&code_challenge_method=S256";

    /// <summary>Not usable: X's code cannot be redeemed without the flow's state.</summary>
    public Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct) =>
        throw new InvalidOperationException("X sign-in needs the connection attempt's state. Start again.");

    public async Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, string state, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.X);

        using var tokenRequest = XTokens.TokenRequest(_options, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = Verifier(_options.ClientSecret, state),
        });
        using var tokenResponse = await http.SendAsync(tokenRequest, ct);
        var tokenText = await tokenResponse.Content.ReadAsStringAsync(ct);

        var access = XJson.Get(tokenText, "access_token");
        var refresh = XJson.Get(tokenText, "refresh_token");
        if (!tokenResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(access))
        {
            var reason = XJson.Message(tokenText);
            throw new InvalidOperationException(
                XJson.Get(tokenText, "error") == "unauthorized_client"
                    ? "X refused this app's credentials (unauthorized_client). Check that the app is a confidential client (Web App) and Social__X__ClientId / ClientSecret are the OAuth 2.0 ones."
                    : reason is { Length: > 0 }
                        ? reason
                        : $"X did not issue a token ({(int)tokenResponse.StatusCode}).");
        }
        if (string.IsNullOrWhiteSpace(refresh))
            throw new InvalidOperationException(
                "X did not give us lasting access (no refresh token), so scheduled posts could not go out. Connect again and allow access.");

        var granted = (XJson.Get(tokenText, "scope") ?? string.Join(" ", Scopes))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!granted.Contains("tweet.write") || !granted.Contains("media.write"))
            throw new InvalidOperationException(
                "Permission to post (with pictures and videos) was not given, so nothing can be published. Connect again and allow it.");

        var expiresIn = long.TryParse(XJson.Get(tokenText, "expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : 7200;
        var credential = new XCredential(access!, DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds(), refresh!);

        // Who signed in.
        using var meRequest = XTokens.Bearer(HttpMethod.Get, MeUrl, access!);
        using var meResponse = await http.SendAsync(meRequest, ct);
        var me = await meResponse.Content.ReadAsStringAsync(ct);
        if (!meResponse.IsSuccessStatusCode)
        {
            log.LogWarning("X users/me after consent answered {Status}", (int)meResponse.StatusCode);
            throw new InvalidOperationException(
                meResponse.StatusCode == HttpStatusCode.PaymentRequired
                    ? XRefusal.CreditsMessage
                    : XJson.Message(me) ?? $"X did not say which account signed in ({(int)meResponse.StatusCode}).");
        }

        var id = XJson.Get(me, "data", "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("X did not say which account signed in. Try connecting again.");
        var name = XJson.Get(me, "data", "name");
        var username = XJson.Get(me, "data", "username");

        var display = (name, username) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{name} (@{username})",
            (_, { Length: > 0 }) => "@" + username,
            ({ Length: > 0 }, _) => name!,
            _ => "X account",
        };

        return new[]
        {
            new DiscoveredAccount(
                id!,
                display,
                SocialPlatform.XAccount,
                AccessToken: credential.Serialize(),
                // None: the refresh token lives inside the credential and
                // rotates on every refresh; a second copy would go stale.
                RefreshToken: null,
                // X does not publish a refresh-token lifetime.
                ExpiresAtUtc: null,
                Scopes: granted,
                AvatarUrl: XJson.Get(me, "data", "profile_image_url"),
                AuthorizedByUserId: id),
        };
    }

    /// <summary>Revokes the refresh token, which ends the grant. No refresh needed first.</summary>
    public async Task RevokeAsync(string credential, CancellationToken ct)
    {
        if (XCredential.Parse(credential) is not { } c) return;
        var http = httpFactory.CreateClient(SocialHttpClients.X);
        using var request = new HttpRequestMessage(HttpMethod.Post, XTokens.RevokeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = c.RefreshToken,
                ["token_type_hint"] = "refresh_token",
            }),
        };
        request.Headers.Authorization = XTokens.ClientAuth(_options);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            log.LogInformation("X token revocation answered {Status}", (int)response.StatusCode);
    }
}

/// <summary>Posts a poster or video to an X account.</summary>
/// <remarks>
/// <para>
/// Steps: sign in (refreshing, and so rotating, the credential when the
/// two-hour access token has lapsed); fetch our file; upload it to X in
/// segments (<c>initialize</c>, <c>append</c>, <c>finalize</c>, then wait for
/// a video to be processed); then create the post with the media attached.
/// </para>
/// <list type="bullet">
/// <item>Everything before the post is created is certain: nothing was
/// posted. An uploaded file that is never attached shows nowhere and lapses
/// on its own. Retried automatically when transient.</item>
/// <item>A timeout, dropped connection or server error on creating the post,
/// or a success with no id, is <b>Uncertain</b> — X may have posted it.
/// Never retried automatically.</item>
/// </list>
/// </remarks>
public sealed class XPublisher(
    IHttpClientFactory httpFactory,
    IOptions<XOptions> options,
    ILogger<XPublisher> log) : ISocialPublisher
{
    public const string UploadInitUrl = "https://api.x.com/2/media/upload/initialize";
    public const string UploadBase = "https://api.x.com/2/media/upload";
    public const string PostsUrl = "https://api.x.com/2/tweets";

    /// <summary>The whole publish, end to end. Below the worker's default 300-second lease.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);

    public const long MaxImageBytes = 5L * 1024 * 1024;
    public const long MaxVideoBytes = 512L * 1024 * 1024;

    /// <summary>Upload segment size. X takes up to 5 MB per append.</summary>
    public const int SegmentSize = 4 * 1024 * 1024;

    private static readonly Regex MediaId = new("^[0-9]{1,19}$", RegexOptions.CultureInvariant);

    private readonly XOptions _options = options.Value;

    /// <summary>How long to wait between processing checks, given X's check_after_secs. Settable so tests need not wait.</summary>
    public Func<int, TimeSpan> ProcessingDelay { get; init; } = s => TimeSpan.FromSeconds(Math.Clamp(s, 1, 10));

    /// <summary>How long to wait for X to process a video before giving up (nothing posted).</summary>
    public TimeSpan ProcessingLimit { get; init; } = TimeSpan.FromSeconds(150);

    public SocialPlatform Platform => SocialPlatform.XAccount;

    /// <summary>Credentials only: an account connected before X was switched off keeps posting.</summary>
    public bool IsConfigured => _options.HasCredentials;

    /// <summary>The MIME type from the file's first bytes, or null when it is not one we send.</summary>
    public static string? SniffImageType(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F'
            && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "image/webp";
        if (b.Length >= 4 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "image/gif";
        return null;
    }

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var text = (request.Caption ?? string.Empty).Trim();
        if (!XText.Fits(text))
            return Fail($"The caption is too long for X ({XText.WeightedLength(text)} of {XText.MaxWeightedLength}). Shorten it and share again.");

        var api = httpFactory.CreateClient(SocialHttpClients.X);
        var upload = httpFactory.CreateClient(SocialHttpClients.XUpload);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var within = budget.Token;

        // 1. Sign in. A rotated credential rides on every outcome from here.
        string access;
        RenewedCredential? renewed;
        try
        {
            (access, renewed) = await XTokens.AccessTokenAsync(api, _options, request.AccessToken, within);
        }
        catch (XTokens.RefreshFailed ex)
        {
            return new PublishOutcome(false, null, null, ex.Message, ex.IsAuthFailure, ex.IsRetryable);
        }

        // Saved now: X has already retired the old refresh token.
        if (renewed is not null && request.SaveRenewal is { } save)
            await save(renewed);

        PublishOutcome With(PublishOutcome o) => o with { RenewedCredential = renewed };

        // 2. Our own file. Nothing posted yet.
        var limit = request.IsVideo ? MaxVideoBytes : MaxImageBytes;
        byte[] media;
        try
        {
            using var file = await upload.GetAsync(request.MediaUrl, HttpCompletionOption.ResponseHeadersRead, within);
            if (!file.IsSuccessStatusCode) return With(Retry($"Could not read the {(request.IsVideo ? "video" : "image")} file ({(int)file.StatusCode})."));
            if (file.Content.Headers.ContentLength > limit) return With(TooBig(request.IsVideo));
            media = await file.Content.ReadAsByteArrayAsync(within);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return With(Retry("Could not read the file to post."));
        }
        if (media.Length == 0) return With(Fail("The file to post is empty."));
        if (media.LongLength > limit) return With(TooBig(request.IsVideo));

        string mediaType;
        if (request.IsVideo)
        {
            mediaType = "video/mp4";
        }
        else if (SniffImageType(media) is { } sniffed)
        {
            mediaType = sniffed;
        }
        else
        {
            return With(Fail("X could not be sent this image: it is not a PNG, JPEG, WebP or GIF."));
        }

        // 3. Upload. Failures here leave nothing on the account.
        string mediaId;
        try
        {
            var (id, refusal) = await UploadAsync(api, access, media, mediaType, request.IsVideo, within);
            if (refusal is not null) return With(refusal);
            mediaId = id!;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "X media upload was interrupted");
            return With(Retry("The upload to X was interrupted."));
        }

        // 4. Create the post. From here a lost answer means "maybe posted".
        object body = text.Length > 0
            ? new { text, media = new { media_ids = new[] { mediaId } } }
            : new { media = new { media_ids = new[] { mediaId } } };
        try
        {
            using var post = XTokens.Bearer(HttpMethod.Post, PostsUrl, access, XTokens.Json(body));
            using var response = await api.SendAsync(post, within);
            var answer = await response.Content.ReadAsStringAsync(within);

            if (response.IsSuccessStatusCode)
            {
                var postId = XJson.Get(answer, "data", "id");
                return With(string.IsNullOrWhiteSpace(postId)
                    ? Unknown("X answered without the post's id. Check the account before retrying.")
                    : new PublishOutcome(true, postId, $"https://x.com/i/web/status/{postId}", null));
            }

            // A server error on the call that posts: it may have gone through.
            if ((int)response.StatusCode >= 500)
                return With(Unknown($"X answered {(int)response.StatusCode} while creating the post. It may have been posted; check the account before retrying."));

            return With(Refused(XRefusal.For(response.StatusCode, answer)));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Creating the X post was interrupted");
            return With(Unknown("The connection to X dropped while creating the post. It may have been posted; check the account before retrying."));
        }
    }

    /// <summary>initialize → append (segments) → finalize → wait for processing. Returns the media id or the refusal.</summary>
    private async Task<(string? MediaId, PublishOutcome? Refusal)> UploadAsync(
        HttpClient api, string access, byte[] media, string mediaType, bool isVideo, CancellationToken ct)
    {
        using (var init = XTokens.Bearer(HttpMethod.Post, UploadInitUrl, access, XTokens.Json(new
               {
                   media_type = mediaType,
                   total_bytes = media.LongLength,
                   media_category = isVideo ? "tweet_video" : "tweet_image",
               })))
        using (var response = await api.SendAsync(init, ct))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) return (null, UploadRefused(response.StatusCode, body));
            var id = XJson.Get(body, "data", "id");
            if (id is null || !MediaId.IsMatch(id)) return (null, Retry("X did not open the upload."));

            var segments = (int)((media.LongLength + SegmentSize - 1) / SegmentSize);
            for (var i = 0; i < segments; i++)
            {
                var offset = i * SegmentSize;
                var count = (int)Math.Min(SegmentSize, media.LongLength - offset);
                // Names quoted by hand: .NET leaves them bare, which some
                // multipart parsers refuse.
                using var form = new MultipartFormDataContent
                {
                    { new StringContent(i.ToString(CultureInfo.InvariantCulture)), "\"segment_index\"" },
                };
                var part = new ByteArrayContent(media, offset, count);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, "\"media\"", "\"media\"");

                using var append = XTokens.Bearer(HttpMethod.Post, $"{UploadBase}/{id}/append", access, form);
                using var appended = await api.SendAsync(append, ct);
                if (!appended.IsSuccessStatusCode)
                    return (null, UploadRefused(appended.StatusCode, await appended.Content.ReadAsStringAsync(ct)));
            }

            using var finalize = XTokens.Bearer(HttpMethod.Post, $"{UploadBase}/{id}/finalize", access);
            using var finalized = await api.SendAsync(finalize, ct);
            var finalBody = await finalized.Content.ReadAsStringAsync(ct);
            if (!finalized.IsSuccessStatusCode) return (null, UploadRefused(finalized.StatusCode, finalBody));

            var (state, after, error) = Processing(finalBody);
            var deadline = DateTime.UtcNow.Add(ProcessingLimit);
            while (state is "pending" or "in_progress")
            {
                if (DateTime.UtcNow >= deadline)
                    return (null, Retry("X took too long to process the video. Nothing was posted."));
                await Task.Delay(ProcessingDelay(after), ct);

                using var status = XTokens.Bearer(HttpMethod.Get,
                    $"{UploadBase}?command=STATUS&media_id={id}", access);
                using var checkedResponse = await api.SendAsync(status, ct);
                var statusBody = await checkedResponse.Content.ReadAsStringAsync(ct);
                if (!checkedResponse.IsSuccessStatusCode)
                {
                    // A missed check is not a failed upload: look again, unless X says no.
                    if ((int)checkedResponse.StatusCode is >= 500 or 429) continue;
                    return (null, UploadRefused(checkedResponse.StatusCode, statusBody));
                }
                (state, after, error) = Processing(statusBody);
            }

            if (state == "failed")
                return (null, Fail(error is { Length: > 0 }
                    ? $"X could not process the {(isVideo ? "video" : "image")}: {error}"
                    : $"X could not process the {(isVideo ? "video" : "image")}."));

            return (id, null);
        }
    }

    private static (string? State, int CheckAfter, string? Error) Processing(string body)
    {
        using var doc = XJson.Parse(body);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("processing_info", out var info) || info.ValueKind != JsonValueKind.Object)
            return (null, 0, null);   // an image: ready as soon as it is finalised
        var after = int.TryParse(XJson.Get(info, "check_after_secs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 2;
        return (XJson.Get(info, "state"), after, XJson.Get(info, "error", "message"));
    }

    /// <summary>
    /// A refused upload step. A 403 here is almost always the media scope
    /// missing from an older grant, which only reconnecting fixes.
    /// </summary>
    private static PublishOutcome UploadRefused(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Forbidden && !XRefusal.IsAppRefused(body)
            && XJson.Title(body)?.Contains("Credits", StringComparison.OrdinalIgnoreCase) != true)
            return new PublishOutcome(false, null, null,
                "X did not allow this account to upload pictures or videos. Reconnect it in Social & Ads and allow it.",
                IsAuthFailure: true);
        return Refused(XRefusal.For(status, body));
    }

    private static string TooLarge(bool isVideo) => isVideo
        ? "This video is too large to send to X from here (512 MB at most)."
        : "This image is too large for X (5 MB at most), and its smaller version could not be prepared.";

    /// <summary>
    /// A poster is normally sent as its small JPEG; the full PNG arriving
    /// here means that version could not be made this time, which a retry
    /// may fix. A video over our cap will not shrink.
    /// </summary>
    private static PublishOutcome TooBig(bool isVideo) =>
        isVideo ? Fail(TooLarge(true)) : Retry(TooLarge(false));

    private static PublishOutcome Refused(XRefusal.Meaning m) =>
        new(false, null, null, m.Message, m.IsAuthFailure, m.IsRetryable);

    private static PublishOutcome Fail(string error) => new(false, null, null, error);
    private static PublishOutcome Retry(string error) => new(false, null, null, error, IsRetryable: true);
    private static PublishOutcome Unknown(string error) => new(false, null, null, error, OutcomeUnknown: true);
}

/// <summary>
/// The daily health check for an X connection. The access token lasts two
/// hours, so this nearly always refreshes — and a refresh X accepts proves
/// the grant is alive, without a billed read. The rotated credential comes
/// back to be saved.
/// </summary>
public sealed class XProbe(
    IHttpClientFactory httpFactory,
    IOptions<XOptions> options,
    ILogger<XProbe> log) : IRenewingConnectionProbe
{
    private readonly XOptions _options = options.Value;

    public SocialPlatform Platform => SocialPlatform.XAccount;

    public bool IsConfigured => _options.HasCredentials;

    /// <remarks>Drops any renewal: the health check calls <see cref="ProbeAndRenewAsync"/>.</remarks>
    public async Task<(ProbeResult Result, string? Detail)> ProbeAsync(
        string externalAccountId, string accessToken, CancellationToken ct)
    {
        var (result, detail, _) = await ProbeAndRenewAsync(externalAccountId, accessToken, ct);
        return (result, detail);
    }

    public async Task<(ProbeResult Result, string? Detail, RenewedCredential? Renewed)> ProbeAndRenewAsync(
        string externalAccountId, string credential, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.X);
        try
        {
            var (_, renewed) = await XTokens.AccessTokenAsync(http, _options, credential, ct);
            return (ProbeResult.Healthy, null, renewed);
        }
        catch (XTokens.RefreshFailed ex)
        {
            if (!ex.IsAuthFailure)
                log.LogInformation("X health check could not refresh; will look again tomorrow");
            return (ex.IsAuthFailure ? ProbeResult.Unauthorized : ProbeResult.Inconclusive, ex.Message, null);
        }
    }
}
