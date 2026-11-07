using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/*
 * LinkedIn: connect a profile (and, with Community Management approval, the
 * company Pages the person administers), post a poster or a video to it.
 *
 * The credential. LinkedIn access tokens last 60 days and, for most apps,
 * come with no refresh token — the pharmacy reconnects every two months, and
 * the connection's ExpiresAtUtc says when. Apps LinkedIn has approved for
 * refresh tokens get one lasting a year; then the refresh token is the
 * credential (marked, see LinkedInCredential) and every call exchanges it
 * for a fresh access token, as YouTube does.
 *
 * One consent, several connections. A profile and its Pages share the token
 * from the same sign-in; its fingerprint (RefreshToken on the connection) is
 * what keeps disconnecting one from revoking the others.
 */

/// <summary>What is stored as a LinkedIn connection's credential.</summary>
internal static class LinkedInCredential
{
    private const string RefreshPrefix = "li-refresh:";

    public static string FromRefreshToken(string refreshToken) => RefreshPrefix + refreshToken;

    /// <summary>True when the credential is a refresh token to exchange, rather than an access token to use.</summary>
    public static bool IsRefresh(string credential, out string refreshToken)
    {
        if (credential.StartsWith(RefreshPrefix, StringComparison.Ordinal))
        {
            refreshToken = credential[RefreshPrefix.Length..];
            return true;
        }

        refreshToken = string.Empty;
        return false;
    }
}

/// <summary>Turns a stored LinkedIn credential into an access token.</summary>
internal static class LinkedInTokens
{
    public const string TokenUrl = "https://www.linkedin.com/oauth/v2/accessToken";

    public sealed class RefreshFailed(string message, bool isAuthFailure, bool isRetryable) : Exception(message)
    {
        public bool IsAuthFailure { get; } = isAuthFailure;
        public bool IsRetryable { get; } = isRetryable;
    }

    public static async Task<string> AccessTokenAsync(
        HttpClient http, LinkedInOptions options, string credential, CancellationToken ct)
    {
        if (!LinkedInCredential.IsRefresh(credential, out var refreshToken))
            return credential;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
        });

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(TokenUrl, form, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new RefreshFailed("Could not reach LinkedIn to sign in.", false, true);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode && LinkedInJson.Get(text, "access_token") is { Length: > 0 } token)
                return token;

            if ((int)response.StatusCode >= 500)
                throw new RefreshFailed("LinkedIn sign-in is having problems.", false, true);

            // LinkedIn answers an expired or withdrawn refresh token with a
            // 400/401 and "invalid_request"/"invalid_grant". Only reconnecting fixes it.
            throw new RefreshFailed(
                "The connection to LinkedIn is no longer valid. Reconnect it in Social & Ads.",
                isAuthFailure: true, isRetryable: false);
        }
    }
}

/// <summary>Small readers for LinkedIn's JSON, which never throw.</summary>
internal static class LinkedInJson
{
    public static string? Get(string body, params string[] path)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var el = json.RootElement;
            foreach (var key in path)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out el)) return null;
            }
            return el.ValueKind == JsonValueKind.String ? el.GetString()
                : el.ValueKind == JsonValueKind.Number ? el.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static long? Long(string body, params string[] path) =>
        long.TryParse(Get(body, path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>LinkedIn's error code: <c>{"status":403,"code":"ACCESS_DENIED","message":"…"}</c>.</summary>
    public static string? ErrorCode(string body) => Get(body, "code") ?? Get(body, "error");

    public static string? ErrorMessage(string body) =>
        Get(body, "message") ?? Get(body, "error_description");
}

/// <summary>Request building shared by the provider, the publishers and the probes.</summary>
internal static class LinkedInApi
{
    public const string Rest = "https://api.linkedin.com/rest";
    public const string UserInfoUrl = "https://api.linkedin.com/v2/userinfo";

    /// <summary>Page roles allowed to post organic content (LinkedIn's Posts API permissions).</summary>
    public static readonly string[] PostingRoles = { "ADMINISTRATOR", "CONTENT_ADMINISTRATOR" };

    public static HttpRequestMessage Request(
        HttpMethod method, string url, string accessToken, LinkedInOptions options, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("LinkedIn-Version",
            string.IsNullOrWhiteSpace(options.ApiVersion) ? "202610" : options.ApiVersion.Trim());
        request.Headers.TryAddWithoutValidation("X-Restli-Protocol-Version", "2.0.0");
        return request;
    }

    public static StringContent JsonBody(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public static string AuthorUrn(SocialPlatform platform, string externalId) =>
        platform == SocialPlatform.LinkedInPage
            ? $"urn:li:organization:{externalId}"
            : $"urn:li:person:{externalId}";

    /// <summary>The organisation ids the token's member can post as.</summary>
    /// <returns>Null when LinkedIn refused or did not answer: <paramref name="status"/> says how.</returns>
    public static async Task<(List<string>? Ids, HttpStatusCode Status, string Body)> PostableOrganizationsAsync(
        HttpClient http, string accessToken, LinkedInOptions options, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get,
            $"{Rest}/organizationAcls?q=roleAssignee&state=APPROVED&count=100", accessToken, options);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) return (null, response.StatusCode, body);

        var ids = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (json.RootElement.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in elements.EnumerateArray())
                {
                    var role = e.TryGetProperty("role", out var r) ? r.GetString() : null;
                    if (role is null || !PostingRoles.Contains(role)) continue;
                    if (e.TryGetProperty("state", out var s) && s.GetString() is { } state && state != "APPROVED") continue;

                    // "organizationTarget" in current versions, "organization" in older ones.
                    var urn = (e.TryGetProperty("organizationTarget", out var t) ? t.GetString() : null)
                              ?? (e.TryGetProperty("organization", out var o) ? o.GetString() : null);
                    const string prefix = "urn:li:organization:";
                    if (urn is not null && urn.StartsWith(prefix, StringComparison.Ordinal) && urn.Length > prefix.Length)
                    {
                        var id = urn[prefix.Length..];
                        if (!ids.Contains(id)) ids.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // An answer in a shape we do not know: "could not tell", not a crash.
            return (null, HttpStatusCode.BadGateway, body);
        }

        return (ids, response.StatusCode, body);
    }
}

/// <summary>
/// The post text, in LinkedIn's "little" format.
/// </summary>
/// <remarks>
/// LinkedIn reads a handful of characters in the commentary as markup —
/// <c>@[Name](urn)</c> mentions, <c>{hashtag|…}</c> templates — and every one
/// of them must be escaped with a backslash to appear as typed. Unescaped, a
/// pharmacy's "(open till 9pm)" can cut the post short or have it refused.
/// <c>#word</c> at the start of a word is left alone so hashtags still work;
/// any other # (a lone one, one inside a link) is escaped.
/// </remarks>
public static class LinkedInText
{
    /// <summary>LinkedIn's limit on a post's commentary.</summary>
    public const int Max = 3000;

    private const string Reserved = "\\|{}@[]()<>*_~";

    public static string Commentary(string? caption)
    {
        var text = (caption ?? string.Empty).Replace("\r\n", "\n").Trim();
        var sb = new StringBuilder(text.Length + 16);

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string piece;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // An emoji is two chars: kept or dropped together, never split.
                piece = text.Substring(i, 2);
                i++;
            }
            else
            {
                // A hashtag starts a word and is followed by one: "#FluJab"
                // yes, "book.com/#times" or a lone "#" no.
                var hashtag = c == '#'
                    && (i == 0 || char.IsWhiteSpace(text[i - 1]))
                    && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
                var escape = Reserved.Contains(c) || (c == '#' && !hashtag);
                piece = escape ? "\\" + c : c.ToString();
            }

            // Never cut an escape or an emoji in half.
            if (sb.Length + piece.Length > Max) break;
            sb.Append(piece);
        }

        return sb.ToString();
    }
}

/// <summary>
/// LinkedIn sign-in: the person's profile, and the Pages they administer.
/// </summary>
/// <remarks>
/// <para>
/// Profile posting needs <c>openid profile w_member_social</c> — the
/// self-serve "Sign In with LinkedIn using OpenID Connect" and "Share on
/// LinkedIn" products. Page posting needs <c>w_organization_social</c> and
/// <c>rw_organization_admin</c> from the Community Management API, which
/// LinkedIn reviews; until it is approved the server must not ask for them
/// (see <see cref="LinkedInOptions.PagesEnabled"/>).
/// </para>
/// <para>
/// <b>Disconnecting.</b> When the last connection using a sign-in goes, its
/// access token is revoked at LinkedIn. A refresh-token credential is left
/// alone (LinkedIn's revoke takes access tokens); our copy is deleted either
/// way, and the person can remove the app under LinkedIn → Settings → Data
/// privacy → Permitted services.
/// </para>
/// </remarks>
public sealed class LinkedInOAuthProvider(
    IHttpClientFactory httpFactory,
    IOptions<LinkedInOptions> options,
    ILogger<LinkedInOAuthProvider> log) : ISocialOAuthProvider
{
    private readonly LinkedInOptions _options = options.Value;

    public const string AuthorizeUrl = "https://www.linkedin.com/oauth/v2/authorization";
    public const string RevokeUrl = "https://www.linkedin.com/oauth/v2/revoke";

    public static readonly string[] ProfileScopes = { "openid", "profile", "w_member_social" };
    public static readonly string[] PageScopes = { "w_organization_social", "rw_organization_admin" };

    /// <summary>A sign-in with no expiry given: LinkedIn's documented 60 days.</summary>
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(60);

    /// <summary>At most this many Pages are looked up by name; nobody administers more for a pharmacy.</summary>
    private const int MaxPages = 50;

    public SocialOAuthProviderKind Kind => SocialOAuthProviderKind.LinkedIn;

    public bool IsConfigured => _options.IsConfigured;

    public IReadOnlyList<string> Scopes =>
        (_options.ProfilesEnabled ? ProfileScopes : Array.Empty<string>())
        .Concat(_options.PagesEnabled ? PageScopes : Array.Empty<string>())
        .ToList();

    public string BuildAuthorizationUrl(string state, string redirectUri)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = string.Join(" ", Scopes),
        };

        return AuthorizeUrl + "?" + string.Join("&", query
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    public async Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.LinkedIn);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        });

        using var tokenResponse = await http.PostAsync(LinkedInTokens.TokenUrl, form, ct);
        var tokenText = await tokenResponse.Content.ReadAsStringAsync(ct);

        var accessToken = LinkedInJson.Get(tokenText, "access_token");
        if (!tokenResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException(
                LinkedInJson.Get(tokenText, "error_description")
                ?? LinkedInJson.Get(tokenText, "error")
                ?? $"LinkedIn did not issue a token ({(int)tokenResponse.StatusCode}).");

        var now = DateTime.UtcNow;
        var refreshToken = LinkedInJson.Get(tokenText, "refresh_token");
        string credential;
        DateTime expiresAt;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            credential = LinkedInCredential.FromRefreshToken(refreshToken);
            expiresAt = now.AddSeconds(LinkedInJson.Long(tokenText, "refresh_token_expires_in") ?? 365L * 24 * 3600);
        }
        else
        {
            credential = accessToken;
            expiresAt = LinkedInJson.Long(tokenText, "expires_in") is { } seconds
                ? now.AddSeconds(seconds)
                : now.Add(DefaultLifetime);
        }

        // LinkedIn lists the granted scopes comma-separated.
        var granted = (LinkedInJson.Get(tokenText, "scope") ?? string.Join(",", Scopes))
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var canPostAsMember = _options.ProfilesEnabled && granted.Contains("w_member_social");
        var canPostAsPage = _options.PagesEnabled && granted.Contains("w_organization_social");
        if (!canPostAsMember && !canPostAsPage)
            throw new InvalidOperationException(
                "Permission to post was not given, so nothing can be published. Connect again and allow it.");

        var accounts = new List<DiscoveredAccount>();
        string? memberId = null;

        if (canPostAsMember || granted.Contains("openid"))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LinkedInApi.UserInfoUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode && LinkedInJson.Get(body, "sub") is { Length: > 0 } sub)
            {
                memberId = sub;
                if (canPostAsMember)
                {
                    var name = LinkedInJson.Get(body, "name");
                    accounts.Add(new DiscoveredAccount(
                        sub,
                        string.IsNullOrWhiteSpace(name) ? "LinkedIn profile" : name!,
                        SocialPlatform.LinkedInProfile,
                        AccessToken: credential,
                        // Not a second secret: the same credential, so the sign-in
                        // has a fingerprint and is only revoked with its last user.
                        RefreshToken: credential,
                        ExpiresAtUtc: expiresAt,
                        Scopes: granted,
                        AvatarUrl: LinkedInJson.Get(body, "picture"),
                        AuthorizedByUserId: sub));
                }
            }
            else
            {
                log.LogWarning("LinkedIn profile lookup after consent answered {Status}", (int)response.StatusCode);
                if (!canPostAsPage)
                    throw new InvalidOperationException(
                        $"Signed in to LinkedIn, but could not read your profile: {LinkedInJson.ErrorMessage(body) ?? $"LinkedIn answered {(int)response.StatusCode}."}");
            }
        }

        if (canPostAsPage)
        {
            var (ids, status, body) = await LinkedInApi.PostableOrganizationsAsync(http, accessToken, _options, ct);
            if (ids is null)
            {
                log.LogWarning("LinkedIn Page lookup after consent answered {Status}", (int)status);
                if (accounts.Count == 0)
                    throw new InvalidOperationException(
                        $"Signed in to LinkedIn, but could not list your Pages: {LinkedInJson.ErrorMessage(body) ?? $"LinkedIn answered {(int)status}."}");
            }
            else
            {
                foreach (var id in ids.Take(MaxPages))
                {
                    var name = await OrganizationNameAsync(http, accessToken, id, ct);
                    accounts.Add(new DiscoveredAccount(
                        id,
                        name ?? $"LinkedIn Page {id}",
                        SocialPlatform.LinkedInPage,
                        AccessToken: credential,
                        RefreshToken: credential,
                        ExpiresAtUtc: expiresAt,
                        Scopes: granted,
                        AuthorizedByUserId: memberId));
                }
            }
        }

        if (accounts.Count == 0)
            throw new InvalidOperationException(canPostAsPage
                ? "No LinkedIn Pages were found where you are an admin or content admin. Ask a Page super admin to give you that role, then connect again."
                : "No LinkedIn account could be connected from that sign-in.");

        return accounts;
    }

    private async Task<string?> OrganizationNameAsync(HttpClient http, string accessToken, string id, CancellationToken ct)
    {
        try
        {
            using var request = LinkedInApi.Request(HttpMethod.Get,
                $"{LinkedInApi.Rest}/organizations/{Uri.EscapeDataString(id)}", accessToken, _options);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            return LinkedInJson.Get(body, "localizedName");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // A name is a nicety; the Page can still be connected by id.
            return null;
        }
    }

    public async Task RevokeAsync(string credential, CancellationToken ct)
    {
        if (LinkedInCredential.IsRefresh(credential, out _)) return;

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["token"] = credential,
        });
        using var response = await httpFactory.CreateClient(SocialHttpClients.LinkedIn).PostAsync(RevokeUrl, form, ct);
        if (!response.IsSuccessStatusCode)
            log.LogInformation("LinkedIn token revocation answered {Status}", (int)response.StatusCode);
    }
}

/// <summary>
/// Posts a poster or a video to a LinkedIn profile or Page.
/// </summary>
/// <remarks>
/// <para>
/// Three steps: upload the image or video (LinkedIn holds it as an asset that
/// appears nowhere yet), then create the post that shows it. The
/// classification follows that split:
/// </para>
/// <list type="bullet">
/// <item>anything failing before the post is created is certain: nothing
/// was posted — retried automatically if transient;</item>
/// <item>a timeout, dropped connection or server error while creating the
/// post is <b>Uncertain</b> and never retried automatically;</item>
/// <item>a refused sign-in, or a Page role taken away, marks the connection
/// for reconnecting.</item>
/// </list>
/// <para>
/// A video is processed by LinkedIn after upload; creating the post too soon
/// is refused ("waiting upload"/"processing") without posting, so it is
/// simply asked again a few seconds later, within the budget.
/// </para>
/// </remarks>
public abstract class LinkedInPublisherBase(
    IHttpClientFactory httpFactory,
    IOptions<LinkedInOptions> options,
    ILogger log) : ISocialPublisher
{
    private readonly LinkedInOptions _options = options.Value;

    private const long MaxVideoBytes = 500L * 1024 * 1024;   // LinkedIn's limit for feed video
    private const long MaxImageBytes = 50L * 1024 * 1024;

    /// <summary>The whole publish, end to end. Below the worker's default 300-second lease.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);

    private static readonly TimeSpan[] DefaultProcessingWaits =
    {
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Waits between asking to create a video post that LinkedIn is still
    /// processing. About 80 seconds in all, well inside <see cref="Budget"/>.
    /// Settable so tests need not wait.
    /// </summary>
    public IReadOnlyList<TimeSpan> ProcessingWaits { get; init; } = DefaultProcessingWaits;

    public abstract SocialPlatform Platform { get; }

    /// <summary>
    /// Credentials only — not the Profiles/Pages switches, which govern what
    /// can be newly connected. A connection made before a switch was turned
    /// off keeps publishing.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var api = httpFactory.CreateClient(SocialHttpClients.LinkedIn);
        var upload = httpFactory.CreateClient(SocialHttpClients.LinkedInUpload);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var within = budget.Token;

        // 1. Sign in. Nothing posted yet.
        string accessToken;
        try
        {
            accessToken = await LinkedInTokens.AccessTokenAsync(api, _options, request.AccessToken, within);
        }
        catch (LinkedInTokens.RefreshFailed ex)
        {
            return new PublishOutcome(false, null, null, ex.Message, ex.IsAuthFailure, ex.IsRetryable);
        }

        var author = LinkedInApi.AuthorUrn(Platform, request.ExternalAccountId);

        // 2. Fetch our own file. Nothing posted yet.
        byte[] media;
        try
        {
            using var file = await upload.GetAsync(request.MediaUrl, HttpCompletionOption.ResponseHeadersRead, within);
            if (!file.IsSuccessStatusCode)
                return Retry($"Could not read the {(request.IsVideo ? "video" : "poster")} file ({(int)file.StatusCode}).");
            var max = request.IsVideo ? MaxVideoBytes : MaxImageBytes;
            if (file.Content.Headers.ContentLength > max)
                return Fail($"This {(request.IsVideo ? "video" : "image")} is too large for LinkedIn.");
            media = await file.Content.ReadAsByteArrayAsync(within);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Retry($"Could not read the {(request.IsVideo ? "video" : "poster")} file.");
        }

        if (media.Length == 0) return Fail("The file to post is empty.");

        // 3. Upload it as a LinkedIn asset. Still nothing on anyone's feed.
        string assetUrn;
        try
        {
            var uploaded = request.IsVideo
                ? await UploadVideoAsync(api, upload, accessToken, author, media, within)
                : await UploadImageAsync(api, upload, accessToken, author, media, within);
            if (uploaded.Refusal is { } refusal) return refusal;
            assetUrn = uploaded.Urn!;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // An orphaned asset at worst, never a post: safe to try again.
            return Retry("Could not reach LinkedIn while uploading.");
        }

        // 4. Create the post. This is the call that publishes.
        var postJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["author"] = author,
            ["commentary"] = LinkedInText.Commentary(request.Caption),
            ["visibility"] = "PUBLIC",
            ["distribution"] = new Dictionary<string, object>
            {
                ["feedDistribution"] = "MAIN_FEED",
                ["targetEntities"] = Array.Empty<object>(),
                ["thirdPartyDistributionChannels"] = Array.Empty<object>(),
            },
            ["content"] = new Dictionary<string, object>
            {
                ["media"] = new Dictionary<string, object> { ["id"] = assetUrn },
            },
            ["lifecycleState"] = "PUBLISHED",
            ["isReshareDisabledByAuthor"] = false,
        });

        // Videos can take a minute to process; an image only a moment, if at all.
        var waits = request.IsVideo ? ProcessingWaits : ProcessingWaits.Take(2).ToList();
        for (var attempt = 0; ; attempt++)
        {
            HttpStatusCode status;
            string body;
            string? id;
            try
            {
                using var create = LinkedInApi.Request(HttpMethod.Post, $"{LinkedInApi.Rest}/posts", accessToken, _options,
                    new StringContent(postJson, Encoding.UTF8, "application/json"));
                using var response = await api.SendAsync(create, within);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(within);
                id = response.Headers.TryGetValues("x-restli-id", out var values) ? values.FirstOrDefault() : null;
                id ??= LinkedInJson.Get(body, "id");
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Creating a LinkedIn post was interrupted for {Account}", request.ExternalAccountId);
                return Unknown("The post was interrupted and may have gone through.");
            }

            if ((int)status is >= 200 and < 300)
            {
                if (string.IsNullOrWhiteSpace(id))
                    return Unknown("LinkedIn accepted the post but did not return its id.");
                return new PublishOutcome(true, id, $"https://www.linkedin.com/feed/update/{id}/", null);
            }

            // The video is not ready yet: refused, so nothing was posted.
            if (StillProcessing(status, body))
            {
                if (attempt >= waits.Count)
                    return Retry("LinkedIn is still processing the video.");
                try
                {
                    await Task.Delay(waits[attempt], within);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Retry("LinkedIn is still processing the video.");
                }
                continue;
            }

            // A server error on the publishing call itself may have posted.
            if ((int)status >= 500)
                return Unknown($"LinkedIn answered {(int)status} while creating the post.");

            return Refused(status, body, beforePost: false);
        }
    }

    private static bool StillProcessing(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)) return false;
        var text = (LinkedInJson.ErrorMessage(body) ?? "") + " " + (LinkedInJson.ErrorCode(body) ?? "");
        if (text.Contains("PROCESSING_FAILED", StringComparison.OrdinalIgnoreCase)) return false;
        return text.Contains("WAITING_UPLOAD", StringComparison.OrdinalIgnoreCase)
               || text.Contains("PROCESSING", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Uploaded(string? Urn, PublishOutcome? Refusal);

    private async Task<Uploaded> UploadImageAsync(
        HttpClient api, HttpClient upload, string accessToken, string owner, byte[] image, CancellationToken ct)
    {
        using var init = LinkedInApi.Request(HttpMethod.Post, $"{LinkedInApi.Rest}/images?action=initializeUpload",
            accessToken, _options,
            LinkedInApi.JsonBody(new { initializeUploadRequest = new { owner } }));
        using var initResponse = await api.SendAsync(init, ct);
        var initBody = await initResponse.Content.ReadAsStringAsync(ct);
        if (!initResponse.IsSuccessStatusCode)
            return new Uploaded(null, Refused(initResponse.StatusCode, initBody, beforePost: true));

        var uploadUrl = LinkedInJson.Get(initBody, "value", "uploadUrl");
        var urn = LinkedInJson.Get(initBody, "value", "image");
        if (string.IsNullOrWhiteSpace(uploadUrl) || string.IsNullOrWhiteSpace(urn))
            return new Uploaded(null, Retry("LinkedIn did not return an upload address for the image."));

        using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(image) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var putResponse = await upload.SendAsync(put, ct);
        if (!putResponse.IsSuccessStatusCode)
            return new Uploaded(null, Refused(putResponse.StatusCode, await putResponse.Content.ReadAsStringAsync(ct), beforePost: true));

        return new Uploaded(urn, null);
    }

    private async Task<Uploaded> UploadVideoAsync(
        HttpClient api, HttpClient upload, string accessToken, string owner, byte[] video, CancellationToken ct)
    {
        using var init = LinkedInApi.Request(HttpMethod.Post, $"{LinkedInApi.Rest}/videos?action=initializeUpload",
            accessToken, _options,
            LinkedInApi.JsonBody(new
            {
                initializeUploadRequest = new
                {
                    owner,
                    fileSizeBytes = video.LongLength,
                    uploadCaptions = false,
                    uploadThumbnail = false,
                },
            }));
        using var initResponse = await api.SendAsync(init, ct);
        var initBody = await initResponse.Content.ReadAsStringAsync(ct);
        if (!initResponse.IsSuccessStatusCode)
            return new Uploaded(null, Refused(initResponse.StatusCode, initBody, beforePost: true));

        string? urn;
        string uploadToken;
        var parts = new List<(string Url, long First, long Last)>();
        try
        {
            using var json = JsonDocument.Parse(initBody);
            var value = json.RootElement.GetProperty("value");
            urn = value.TryGetProperty("video", out var v) ? v.GetString() : null;
            uploadToken = value.TryGetProperty("uploadToken", out var t) ? t.GetString() ?? "" : "";
            if (value.TryGetProperty("uploadInstructions", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var part in list.EnumerateArray())
                    parts.Add((part.GetProperty("uploadUrl").GetString() ?? "",
                        part.GetProperty("firstByte").GetInt64(), part.GetProperty("lastByte").GetInt64()));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new Uploaded(null, Retry("LinkedIn's upload instructions could not be read."));
        }

        if (string.IsNullOrWhiteSpace(urn) || parts.Count == 0
            || parts.Any(p => string.IsNullOrWhiteSpace(p.Url) || p.First < 0 || p.Last < p.First || p.Last >= video.LongLength))
            return new Uploaded(null, Retry("LinkedIn's upload instructions did not match the video."));

        var etags = new List<string>();
        foreach (var (url, first, last) in parts)
        {
            var length = (int)(last - first + 1);
            using var put = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = new ByteArrayContent(video, (int)first, length),
            };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var putResponse = await upload.SendAsync(put, ct);
            if (!putResponse.IsSuccessStatusCode)
                return new Uploaded(null, Refused(putResponse.StatusCode, await putResponse.Content.ReadAsStringAsync(ct), beforePost: true));

            // Not response.Headers.ETag: LinkedIn's etag is unquoted, which the typed parser rejects.
            var etag = putResponse.Headers.TryGetValues("ETag", out var values) ? values.FirstOrDefault()?.Trim('"') : null;
            if (string.IsNullOrWhiteSpace(etag))
                return new Uploaded(null, Retry("LinkedIn did not confirm a part of the video upload."));
            etags.Add(etag);
        }

        using var finalize = LinkedInApi.Request(HttpMethod.Post, $"{LinkedInApi.Rest}/videos?action=finalizeUpload",
            accessToken, _options,
            LinkedInApi.JsonBody(new
            {
                finalizeUploadRequest = new { video = urn, uploadToken, uploadedPartIds = etags },
            }));
        using var finalizeResponse = await api.SendAsync(finalize, ct);
        if (!finalizeResponse.IsSuccessStatusCode)
            return new Uploaded(null, Refused(finalizeResponse.StatusCode, await finalizeResponse.Content.ReadAsStringAsync(ct), beforePost: true));

        return new Uploaded(urn, null);
    }

    /// <summary>What LinkedIn's refusal means, in the pharmacy's words.</summary>
    internal PublishOutcome Refused(HttpStatusCode status, string body, bool beforePost)
    {
        var message = LinkedInJson.ErrorMessage(body);

        if (status == HttpStatusCode.Unauthorized)
            return new PublishOutcome(false, null, null,
                "LinkedIn did not accept this account's sign-in. Reconnect it in Social & Ads.", IsAuthFailure: true);

        if (status == HttpStatusCode.Forbidden)
            return new PublishOutcome(false, null, null,
                Platform == SocialPlatform.LinkedInPage
                    ? "LinkedIn refused: you may no longer be an admin of this Page, or permission to post was withdrawn. Reconnect it in Social & Ads."
                    : "LinkedIn refused permission to post for this profile. Reconnect it in Social & Ads and allow posting.",
                IsAuthFailure: true);

        if (status == HttpStatusCode.TooManyRequests)
            return Retry("LinkedIn asked us to slow down.");

        // A write conflict: LinkedIn says to retry, and nothing was written.
        if (status == HttpStatusCode.Conflict)
            return Retry("LinkedIn had a write conflict.");

        if (beforePost && (int)status >= 500)
            return Retry("LinkedIn is having problems.");

        return Fail(message is { Length: > 0 }
            ? $"LinkedIn refused the post: {message}"
            : $"LinkedIn refused the post ({(int)status}).");
    }

    // Retry messages stop at what happened; the publish service adds "Trying again automatically."
    private static PublishOutcome Fail(string error) => new(false, null, null, error);
    private static PublishOutcome Retry(string error) => new(false, null, null, error, IsRetryable: true);
    private static PublishOutcome Unknown(string error) => new(false, null, null, error, OutcomeUnknown: true);
}

public sealed class LinkedInProfilePublisher(
    IHttpClientFactory httpFactory, IOptions<LinkedInOptions> options, ILogger<LinkedInProfilePublisher> log)
    : LinkedInPublisherBase(httpFactory, options, log)
{
    public override SocialPlatform Platform => SocialPlatform.LinkedInProfile;
}

public sealed class LinkedInPagePublisher(
    IHttpClientFactory httpFactory, IOptions<LinkedInOptions> options, ILogger<LinkedInPagePublisher> log)
    : LinkedInPublisherBase(httpFactory, options, log)
{
    public override SocialPlatform Platform => SocialPlatform.LinkedInPage;
}

/// <summary>
/// The daily health check for a LinkedIn connection. Posts nothing: reads the
/// profile, or confirms the person can still post as the Page.
/// </summary>
public abstract class LinkedInProbeBase(
    IHttpClientFactory httpFactory,
    IOptions<LinkedInOptions> options,
    ILogger log) : ISocialConnectionProbe
{
    private readonly LinkedInOptions _options = options.Value;

    public abstract SocialPlatform Platform { get; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task<(ProbeResult Result, string? Detail)> ProbeAsync(
        string externalAccountId, string accessToken, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.LinkedIn);
        string token;
        try
        {
            token = await LinkedInTokens.AccessTokenAsync(http, _options, accessToken, ct);
        }
        catch (LinkedInTokens.RefreshFailed ex)
        {
            return (ex.IsAuthFailure ? ProbeResult.Unauthorized : ProbeResult.Inconclusive, ex.Message);
        }

        try
        {
            return Platform == SocialPlatform.LinkedInPage
                ? await PageAsync(http, token, externalAccountId, ct)
                : await ProfileAsync(http, token, externalAccountId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogInformation("LinkedIn health check could not reach LinkedIn; will look again tomorrow");
            return (ProbeResult.Inconclusive, "Could not reach LinkedIn.");
        }
    }

    private static async Task<(ProbeResult, string?)> ProfileAsync(
        HttpClient http, string token, string memberId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LinkedInApi.UserInfoUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return (ProbeResult.Unauthorized, "LinkedIn did not accept the sign-in. It may have expired; reconnect it.");
        if (!response.IsSuccessStatusCode)
            return (ProbeResult.Inconclusive, LinkedInJson.ErrorMessage(body) ?? $"LinkedIn answered {(int)response.StatusCode}.");

        return LinkedInJson.Get(body, "sub") == memberId
            ? (ProbeResult.Healthy, null)
            : (ProbeResult.Unauthorized, "This sign-in no longer belongs to the connected LinkedIn profile.");
    }

    private async Task<(ProbeResult, string?)> PageAsync(
        HttpClient http, string token, string organizationId, CancellationToken ct)
    {
        var (ids, status, body) = await LinkedInApi.PostableOrganizationsAsync(http, token, _options, ct);

        if (status == HttpStatusCode.Unauthorized)
            return (ProbeResult.Unauthorized, "LinkedIn did not accept the sign-in. It may have expired; reconnect it.");
        if (status == HttpStatusCode.Forbidden)
            return (ProbeResult.Unauthorized, "LinkedIn no longer allows posting to this Page. Reconnect it.");
        if (ids is null)
            return (ProbeResult.Inconclusive, LinkedInJson.ErrorMessage(body) ?? $"LinkedIn answered {(int)status}.");

        return ids.Contains(organizationId)
            ? (ProbeResult.Healthy, null)
            : (ProbeResult.Unauthorized, "You are no longer an admin or content admin of this LinkedIn Page.");
    }
}

public sealed class LinkedInProfileProbe(
    IHttpClientFactory httpFactory, IOptions<LinkedInOptions> options, ILogger<LinkedInProfileProbe> log)
    : LinkedInProbeBase(httpFactory, options, log)
{
    public override SocialPlatform Platform => SocialPlatform.LinkedInProfile;
}

public sealed class LinkedInPageProbe(
    IHttpClientFactory httpFactory, IOptions<LinkedInOptions> options, ILogger<LinkedInPageProbe> log)
    : LinkedInProbeBase(httpFactory, options, log)
{
    public override SocialPlatform Platform => SocialPlatform.LinkedInPage;
}
