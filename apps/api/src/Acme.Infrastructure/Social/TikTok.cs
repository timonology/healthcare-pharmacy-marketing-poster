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
 * TikTok: connect an account with Login Kit (web), post a video with the
 * Content Posting API's Direct Post.
 *
 * The credential. TikTok access tokens last 24 hours and refresh tokens a
 * year — and a refresh MAY return a new refresh token, after which only the
 * new one works. So the stored credential is a small JSON blob holding the
 * access token, when it lapses, and the refresh token; any call that has to
 * refresh hands the new blob back (PublishOutcome.RenewedCredential, or the
 * probe's renewal) and the caller saves it. Reusing the access token while it
 * is valid keeps refreshes — and the chance of two racing — rare.
 *
 * Unaudited apps. Until TikTok audits the app, every post is private
 * (SELF_ONLY) and only private TikTok accounts can be posted to; TikTok says
 * so with "unaudited_client_can_only_post_to_private_accounts".
 */

/// <summary>TikTok app credentials (developers.tiktok.com → your app).</summary>
public sealed class TikTokOptions
{
    public const string SectionName = "Social:TikTok";

    /// <summary>TikTok calls it the client key.</summary>
    public string ClientKey { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Set false to hide TikTok while keeping the credentials.</summary>
    public bool Enabled { get; set; } = true;

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientKey) && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsConfigured => HasCredentials && Enabled;
}

/// <summary>What is stored as a TikTok connection's credential.</summary>
public sealed record TikTokCredential(string AccessToken, long AccessExpiresUnix, string RefreshToken)
{
    public string Serialize() => JsonSerializer.Serialize(new { a = AccessToken, ax = AccessExpiresUnix, r = RefreshToken });

    public static TikTokCredential? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            var a = root.TryGetProperty("a", out var av) ? av.GetString() : null;
            var r = root.TryGetProperty("r", out var rv) ? rv.GetString() : null;
            var ax = root.TryGetProperty("ax", out var xv) && xv.TryGetInt64(out var x) ? x : 0;
            return string.IsNullOrWhiteSpace(r) ? null : new TikTokCredential(a ?? "", ax, r!);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public bool AccessValidAt(DateTime nowUtc) =>
        !string.IsNullOrWhiteSpace(AccessToken)
        && DateTimeOffset.FromUnixTimeSeconds(AccessExpiresUnix).UtcDateTime > nowUtc.AddMinutes(10);

    /// <summary>Never print the tokens.</summary>
    public override string ToString() => $"TikTokCredential {{ AccessExpiresUnix = {AccessExpiresUnix} }}";
}

/// <summary>TikTok's JSON: open APIs answer <c>{"data":…,"error":{"code","message"}}</c>, OAuth <c>{"error":"…","error_description":"…"}</c>.</summary>
internal static class TikTokJson
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
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static string? Get(string body, params string[] path)
    {
        using var doc = Parse(body);
        return doc is null ? null : Get(doc.RootElement, path);
    }

    /// <summary>"ok", an error code, or null when the body has none.</summary>
    public static string? ErrorCode(string body)
    {
        using var doc = Parse(body);
        // A proxy's HTML page or an unexpected shape: no code, never an exception.
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("error", out var e)) return null;
        return e.ValueKind == JsonValueKind.Object
            ? Get(e, "code")
            : e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    public static string? ErrorMessage(string body)
    {
        using var doc = Parse(body);
        if (doc is null) return null;
        var root = doc.RootElement;
        return Get(root, "error", "message") is { Length: > 0 } m ? m : Get(root, "error_description");
    }
}

/// <summary>Turns a stored TikTok credential into a usable access token, refreshing when it has lapsed.</summary>
internal static class TikTokTokens
{
    public const string TokenUrl = "https://open.tiktokapis.com/v2/oauth/token/";
    public const string RevokeUrl = "https://open.tiktokapis.com/v2/oauth/revoke/";

    public sealed class RefreshFailed(string message, bool isAuthFailure, bool isRetryable) : Exception(message)
    {
        public bool IsAuthFailure { get; } = isAuthFailure;
        public bool IsRetryable { get; } = isRetryable;
    }

    public static async Task<(string AccessToken, RenewedCredential? Renewed)> AccessTokenAsync(
        HttpClient http, TikTokOptions options, string stored, CancellationToken ct)
    {
        var credential = TikTokCredential.Parse(stored)
            ?? throw new RefreshFailed("The stored TikTok sign-in could not be read. Reconnect it in Social & Ads.", true, false);

        var now = DateTime.UtcNow;
        if (credential.AccessValidAt(now)) return (credential.AccessToken, null);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_key"] = options.ClientKey,
            ["client_secret"] = options.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.RefreshToken,
        });

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(TokenUrl, form, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new RefreshFailed("Could not reach TikTok to sign in.", false, true);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            var access = TikTokJson.Get(body, "access_token");
            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(access))
            {
                var expiresIn = long.TryParse(TikTokJson.Get(body, "expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : 86400;
                var refreshIn = long.TryParse(TikTokJson.Get(body, "refresh_expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0;
                // "You must use the newly-returned token if the value is different."
                var refresh = TikTokJson.Get(body, "refresh_token") is { Length: > 0 } rt ? rt : credential.RefreshToken;
                var renewed = new TikTokCredential(access!, DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeSeconds(), refresh);
                return (access!, new RenewedCredential(
                    renewed.Serialize(),
                    refreshIn > 0 ? now.AddSeconds(refreshIn) : null));
            }

            // TikTok may answer with 200 and an OAuth error; these are its passing ones.
            var error = TikTokJson.Get(body, "error");
            if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429
                || error is "server_error" or "temporarily_unavailable" or "rate_limit_exceeded")
                throw new RefreshFailed("TikTok sign-in is having problems.", false, true);

            // An expired or withdrawn refresh token. Only reconnecting fixes it.
            throw new RefreshFailed(
                "The connection to TikTok is no longer valid. Reconnect it in Social & Ads.",
                isAuthFailure: true, isRetryable: false);
        }
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, string accessToken, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }
}

/// <summary>What a TikTok refusal means, in the pharmacy's words.</summary>
internal static class TikTokRefusal
{
    public sealed record Meaning(string Message, bool IsAuthFailure, bool IsRetryable);

    public static Meaning For(HttpStatusCode status, string body)
    {
        var code = TikTokJson.ErrorCode(body);
        var message = TikTokJson.ErrorMessage(body);

        switch (code)
        {
            case "access_token_invalid":
                return new("TikTok did not accept this account's sign-in. Reconnect it in Social & Ads.", true, false);
            case "scope_not_authorized":
                return new("Permission to post to TikTok was not given. Reconnect it in Social & Ads and allow posting.", true, false);
            case "spam_risk_too_many_posts":
                return new("TikTok's daily posting limit for this account is reached. Retry tomorrow.", false, false);
            case "spam_risk_user_banned_from_posting":
                return new("TikTok has stopped this account from posting. Check the account in the TikTok app.", false, false);
            case "reached_active_user_cap":
                return new("This app's daily TikTok allowance is used up. Retry tomorrow.", false, false);
            case "unaudited_client_can_only_post_to_private_accounts":
                return new("Until TikTok approves Marketing25, only private TikTok accounts can be posted to. " +
                           "Set the account to private in the TikTok app, or wait for approval.", false, false);
            case "privacy_level_option_mismatch":
                return new("The audience chosen is no longer available for this TikTok account (its privacy settings changed). Share the video again and choose who can see it.", false, false);
            case "rate_limit_exceeded":
                return new("TikTok asked us to slow down.", false, true);
        }

        if (status == HttpStatusCode.Unauthorized)
            return new("TikTok did not accept this account's sign-in. Reconnect it in Social & Ads.", true, false);
        if (status == HttpStatusCode.TooManyRequests)
            return new("TikTok asked us to slow down.", false, true);
        if ((int)status >= 500)
            return new("TikTok is having problems.", false, true);

        return new(message is { Length: > 0 }
            ? $"TikTok refused the post: {message}"
            : $"TikTok refused the post ({(code is { Length: > 0 } ? code : ((int)status).ToString(CultureInfo.InvariantCulture))}).",
            false, false);
    }
}

/// <summary>Asks TikTok for the live creator info the share dialog and the publisher need.</summary>
public sealed class TikTokCreatorInfoClient(
    IHttpClientFactory httpFactory,
    IOptions<TikTokOptions> options,
    ILogger<TikTokCreatorInfoClient> log) : ITikTokCreatorInfoClient
{
    public const string Url = "https://open.tiktokapis.com/v2/post/publish/creator_info/query/";

    private readonly TikTokOptions _options = options.Value;

    public async Task<TikTokCreatorInfoResult> GetAsync(string credential, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.TikTok);
        string access;
        RenewedCredential? renewed;
        try
        {
            (access, renewed) = await TikTokTokens.AccessTokenAsync(http, _options, credential, ct);
        }
        catch (TikTokTokens.RefreshFailed ex)
        {
            return new TikTokCreatorInfoResult(null, ex.Message, ex.IsAuthFailure);
        }

        try
        {
            var (info, refusal) = await QueryAsync(http, access, ct);
            return refusal is null
                ? new TikTokCreatorInfoResult(info, null, Renewed: renewed)
                : new TikTokCreatorInfoResult(null, refusal.Message, refusal.IsAuthFailure, renewed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogInformation("TikTok creator info could not be reached");
            return new TikTokCreatorInfoResult(null, "Could not reach TikTok. Try again in a moment.", Renewed: renewed);
        }
    }

    /// <summary>The creator info, or what TikTok's refusal means.</summary>
    internal static async Task<(TikTokCreatorInfo? Info, TikTokRefusal.Meaning? Refusal)> QueryAsync(
        HttpClient http, string accessToken, CancellationToken ct)
    {
        using var request = TikTokTokens.Json(HttpMethod.Post, Url, accessToken, new { });
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // TikTok answers its posting caps with 200 and an error code.
        if (!response.IsSuccessStatusCode || TikTokJson.ErrorCode(body) is { } code && code != "ok")
            return (null, TikTokRefusal.For(response.StatusCode, body));

        using var doc = TikTokJson.Parse(body);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return (null, new TikTokRefusal.Meaning("TikTok's answer about this account could not be read.", false, true));

        var options = new List<string>();
        if (data.TryGetProperty("privacy_level_options", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var o in list.EnumerateArray())
                if (o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } level) options.Add(level);

        bool Flag(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        return (new TikTokCreatorInfo(
            TikTokJson.Get(data, "creator_username") ?? "",
            TikTokJson.Get(data, "creator_nickname") ?? "",
            TikTokJson.Get(data, "creator_avatar_url"),
            options,
            Flag("comment_disabled"),
            Flag("duet_disabled"),
            Flag("stitch_disabled"),
            int.TryParse(TikTokJson.Get(data, "max_video_post_duration_sec"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var max) ? max : 0), null);
    }
}

/// <summary>TikTok Login Kit for the web: connects one TikTok account per sign-in.</summary>
/// <remarks>
/// Scopes: <c>user.info.basic</c> (who signed in) and <c>video.publish</c>
/// (Direct Post). No PKCE: TikTok requires it for mobile and desktop apps
/// only. Disconnecting revokes the sign-in at TikTok — unless the same TikTok
/// user is still connected in another pharmacy (see SocialConnectionService).
/// </remarks>
public sealed class TikTokOAuthProvider(
    IHttpClientFactory httpFactory,
    IOptions<TikTokOptions> options,
    ILogger<TikTokOAuthProvider> log) : ISocialOAuthProvider
{
    public const string AuthorizeUrl = "https://www.tiktok.com/v2/auth/authorize/";
    public const string UserInfoUrl = "https://open.tiktokapis.com/v2/user/info/?fields=open_id,avatar_url,display_name";
    public static readonly string[] Scopes = { "user.info.basic", "video.publish" };

    private readonly TikTokOptions _options = options.Value;

    public SocialOAuthProviderKind Kind => SocialOAuthProviderKind.TikTok;

    public bool IsConfigured => _options.IsConfigured;

    public string BuildAuthorizationUrl(string state, string redirectUri) =>
        AuthorizeUrl
        + "?client_key=" + Uri.EscapeDataString(_options.ClientKey)
        // Comma-separated, and the commas left as they are: that is how
        // TikTok's own examples send them.
        + "&scope=" + string.Join(",", Scopes.Select(scope => Uri.EscapeDataString(scope)))
        + "&response_type=code"
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&state=" + Uri.EscapeDataString(state);

    public async Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.TikTok);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_key"] = _options.ClientKey,
            ["client_secret"] = _options.ClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        });

        using var tokenResponse = await http.PostAsync(TikTokTokens.TokenUrl, form, ct);
        var tokenText = await tokenResponse.Content.ReadAsStringAsync(ct);

        var access = TikTokJson.Get(tokenText, "access_token");
        var refresh = TikTokJson.Get(tokenText, "refresh_token");
        var openId = TikTokJson.Get(tokenText, "open_id");
        if (!tokenResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            throw new InvalidOperationException(
                TikTokJson.Get(tokenText, "error_description")
                ?? TikTokJson.ErrorMessage(tokenText)
                ?? $"TikTok did not issue a token ({(int)tokenResponse.StatusCode}).");

        var granted = (TikTokJson.Get(tokenText, "scope") ?? string.Join(",", Scopes))
            .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!granted.Contains("video.publish"))
            throw new InvalidOperationException(
                "Permission to post videos was not given, so nothing can be published. Connect again and allow it.");

        var now = DateTimeOffset.UtcNow;
        var expiresIn = long.TryParse(TikTokJson.Get(tokenText, "expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : 86400;
        var refreshIn = long.TryParse(TikTokJson.Get(tokenText, "refresh_expires_in"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 365L * 24 * 3600;
        var credential = new TikTokCredential(access!, now.AddSeconds(expiresIn).ToUnixTimeSeconds(), refresh!);

        // Who signed in: a name and a picture for the account list.
        string? name = null, avatar = null;
        using (var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                name = TikTokJson.Get(body, "data", "user", "display_name");
                avatar = TikTokJson.Get(body, "data", "user", "avatar_url");
                openId ??= TikTokJson.Get(body, "data", "user", "open_id");
            }
            else
            {
                log.LogWarning("TikTok user info after consent answered {Status}", (int)response.StatusCode);
            }
        }

        if (string.IsNullOrWhiteSpace(openId))
            throw new InvalidOperationException("TikTok did not say which account signed in. Try connecting again.");

        return new[]
        {
            new DiscoveredAccount(
                openId!,
                string.IsNullOrWhiteSpace(name) ? "TikTok account" : name!,
                SocialPlatform.TikTokAccount,
                AccessToken: credential.Serialize(),
                // None: the refresh token lives inside the credential, and a
                // second copy would go stale at the first rotation.
                RefreshToken: null,
                ExpiresAtUtc: now.AddSeconds(refreshIn).UtcDateTime,
                Scopes: granted,
                AvatarUrl: avatar,
                AuthorizedByUserId: openId),
        };
    }

    public async Task RevokeAsync(string credential, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.TikTok);
        string access;
        try
        {
            // TikTok's revoke takes an access token; a lapsed one is refreshed
            // first. Any rotation is discarded with the connection.
            (access, _) = await TikTokTokens.AccessTokenAsync(http, _options, credential, ct);
        }
        catch (TikTokTokens.RefreshFailed)
        {
            return;   // already unusable at TikTok
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_key"] = _options.ClientKey,
            ["client_secret"] = _options.ClientSecret,
            ["token"] = access,
        });
        using var response = await http.PostAsync(TikTokTokens.RevokeUrl, form, ct);
        if (!response.IsSuccessStatusCode)
            log.LogInformation("TikTok token revocation answered {Status}", (int)response.StatusCode);
    }
}

/// <summary>
/// Posts a video to a TikTok account with Direct Post.
/// </summary>
/// <remarks>
/// <para>
/// Steps: sign in (refreshing, maybe rotating, the credential); read the
/// creator info and check the chosen audience is still offered; open the
/// post (<c>video/init</c>), which returns an upload address; send the file in
/// the chunks TikTok's rules allow; then follow the post's status until TikTok
/// says it is published or failed.
/// </para>
/// <list type="bullet">
/// <item>Anything before the last chunk is sent is certain: nothing was
/// posted — retried automatically if transient.</item>
/// <item>A timeout or server error on the last chunk, or a post still
/// processing when the budget runs out, is <b>Uncertain</b>: TikTok may finish
/// it and publish. Never retried automatically.</item>
/// <item><c>FAILED</c> from TikTok is a plain failure with its reason.</item>
/// </list>
/// </remarks>
public sealed class TikTokPublisher(
    IHttpClientFactory httpFactory,
    IOptions<TikTokOptions> options,
    ILogger<TikTokPublisher> log) : ISocialPublisher
{
    public const string InitUrl = "https://open.tiktokapis.com/v2/post/publish/video/init/";
    public const string StatusUrl = "https://open.tiktokapis.com/v2/post/publish/status/fetch/";

    private readonly TikTokOptions _options = options.Value;

    /// <summary>The whole publish, end to end. Below the worker's default 300-second lease.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);

    /// <summary>TikTok's limit on the caption ("title").</summary>
    public const int CaptionMax = 2200;

    private const long MaxBytes = 500L * 1024 * 1024;
    private const long ChunkSize = 10L * 1024 * 1024;
    private const long MaxSingleChunk = 64L * 1024 * 1024;

    /// <summary>Time between status checks. Settable so tests need not wait.</summary>
    public TimeSpan StatusPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to follow the status before calling the outcome uncertain.</summary>
    public TimeSpan StatusPollLimit { get; init; } = TimeSpan.FromSeconds(150);

    public SocialPlatform Platform => SocialPlatform.TikTokAccount;

    /// <summary>Credentials only: an account connected before TikTok was switched off keeps posting.</summary>
    public bool IsConfigured => _options.HasCredentials;

    /// <summary>TikTok's chunking rule: chunks of 5–64 MB, the last one absorbing the remainder.</summary>
    public static (long ChunkSize, int Count) Chunking(long size) =>
        size <= MaxSingleChunk
            ? (size, 1)
            : (ChunkSize, (int)(size / ChunkSize));

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (!request.IsVideo) return Fail("TikTok only takes videos.");
        if (request.TikTokOptions is not { } choice || choice.Problem() is not null)
            return Fail(request.TikTokOptions?.Problem() ?? "Choose who can see the TikTok post, then retry.");

        var api = httpFactory.CreateClient(SocialHttpClients.TikTok);
        var upload = httpFactory.CreateClient(SocialHttpClients.TikTokUpload);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var within = budget.Token;

        // 1. Sign in. A rotated credential rides on every outcome from here.
        string access;
        RenewedCredential? renewed;
        try
        {
            (access, renewed) = await TikTokTokens.AccessTokenAsync(api, _options, request.AccessToken, within);
        }
        catch (TikTokTokens.RefreshFailed ex)
        {
            return new PublishOutcome(false, null, null, ex.Message, ex.IsAuthFailure, ex.IsRetryable);
        }

        // Saved now, not after a minutes-long upload (see PublishRequest.SaveRenewal).
        if (renewed is not null && request.SaveRenewal is { } save)
            await save(renewed);

        PublishOutcome With(PublishOutcome o) => o with { RenewedCredential = renewed };

        // 2. Creator info: the audience must still be one TikTok offers this account.
        TikTokCreatorInfo creator;
        try
        {
            var (info, refusal) = await TikTokCreatorInfoClient.QueryAsync(api, access, within);
            if (refusal is not null) return With(Refused(refusal));
            creator = info!;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return With(Retry("Could not reach TikTok."));
        }

        if (!creator.PrivacyLevelOptions.Contains(choice.PrivacyLevel))
            return With(Fail(
                "The audience chosen is no longer available for this TikTok account (its privacy settings changed). Share the video again and choose who can see it."));

        // 3. Our own file. Nothing posted yet.
        byte[] video;
        try
        {
            using var file = await upload.GetAsync(request.MediaUrl, HttpCompletionOption.ResponseHeadersRead, within);
            if (!file.IsSuccessStatusCode) return With(Retry($"Could not read the video file ({(int)file.StatusCode})."));
            if (file.Content.Headers.ContentLength > MaxBytes) return With(Fail("This video is too large for TikTok."));
            video = await file.Content.ReadAsByteArrayAsync(within);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return With(Retry("Could not read the video file."));
        }
        if (video.Length == 0) return With(Fail("The video file is empty."));

        // 4. Open the post. TikTok makes nothing public until the file is in.
        var (chunkSize, chunks) = Chunking(video.LongLength);
        var init = new
        {
            post_info = new
            {
                title = Caption(request.Caption),
                privacy_level = choice.PrivacyLevel,
                // The creator's own settings win: a switch they turned off in
                // TikTok cannot be turned back on from here.
                disable_comment = !choice.AllowComments || creator.CommentDisabled,
                disable_duet = !choice.AllowDuet || creator.DuetDisabled,
                disable_stitch = !choice.AllowStitch || creator.StitchDisabled,
                brand_organic_toggle = choice.PromotesOwnBusiness,
                brand_content_toggle = choice.IsPaidPartnership,
            },
            source_info = new
            {
                source = "FILE_UPLOAD",
                video_size = video.LongLength,
                chunk_size = chunkSize,
                total_chunk_count = chunks,
            },
        };

        string publishId;
        Uri uploadUrl;
        try
        {
            using var initRequest = TikTokTokens.Json(HttpMethod.Post, InitUrl, access, init);
            using var response = await api.SendAsync(initRequest, within);
            var body = await response.Content.ReadAsStringAsync(within);
            if (!response.IsSuccessStatusCode || TikTokJson.ErrorCode(body) is { } code && code != "ok")
                return With(Refused(TikTokRefusal.For(response.StatusCode, body)));

            var id = TikTokJson.Get(body, "data", "publish_id");
            var url = TikTokJson.Get(body, "data", "upload_url");
            if (string.IsNullOrWhiteSpace(id) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                return With(Retry("TikTok did not return an upload address."));
            publishId = id!;
            uploadUrl = parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // An unfinished upload is never posted: safe to start again.
            return With(Retry("Could not reach TikTok."));
        }

        // 5. Send the file. The last chunk completes the upload, and TikTok
        // then posts on its own — so only that one can leave us unsure.
        for (var i = 0; i < chunks; i++)
        {
            var first = i * chunkSize;
            var last = i == chunks - 1 ? video.LongLength - 1 : first + chunkSize - 1;
            var isLast = i == chunks - 1;
            try
            {
                using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new ByteArrayContent(video, (int)first, (int)(last - first + 1)),
                };
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                put.Content.Headers.ContentRange = new ContentRangeHeaderValue(first, last, video.LongLength);
                using var response = await upload.SendAsync(put, within);
                if (!response.IsSuccessStatusCode)
                {
                    if (isLast && (int)response.StatusCode >= 500)
                        return With(Unknown($"TikTok answered {(int)response.StatusCode} while receiving the video."));
                    return With((int)response.StatusCode >= 500
                        ? Retry("TikTok did not receive the video.")
                        : Fail($"TikTok refused the video upload ({(int)response.StatusCode})."));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "TikTok upload was interrupted at chunk {Chunk} of {Count}", i + 1, chunks);
                return With(isLast
                    ? Unknown("The upload was interrupted and may have gone through.")
                    : Retry("The upload to TikTok was interrupted."));
            }
        }

        // 6. Follow the post until TikTok says it is out, or has failed.
        var deadline = DateTime.UtcNow.Add(StatusPollLimit);
        string? lastStatus = null;
        while (DateTime.UtcNow < deadline && !within.IsCancellationRequested)
        {
            try
            {
                using var statusRequest = TikTokTokens.Json(HttpMethod.Post, StatusUrl, access, new { publish_id = publishId });
                using var response = await api.SendAsync(statusRequest, within);
                var body = await response.Content.ReadAsStringAsync(within);
                if (response.IsSuccessStatusCode && TikTokJson.ErrorCode(body) is null or "ok")
                {
                    using var doc = TikTokJson.Parse(body);
                    var data = doc is not null && doc.RootElement.TryGetProperty("data", out var d) ? d : default;
                    lastStatus = data.ValueKind == JsonValueKind.Object ? TikTokJson.Get(data, "status") : null;

                    if (lastStatus == "PUBLISH_COMPLETE")
                    {
                        // TikTok's field name, misspelling included. Absent for
                        // private posts, and until moderation finishes.
                        string? postId = null;
                        if (data.TryGetProperty("publicaly_available_post_id", out var ids) && ids.ValueKind == JsonValueKind.Array)
                            foreach (var p in ids.EnumerateArray())
                            {
                                postId = p.ValueKind == JsonValueKind.Number ? p.GetRawText() : p.GetString();
                                if (!string.IsNullOrWhiteSpace(postId)) break;
                            }

                        var permalink = postId is not null && creator.Username.Length > 0
                            ? $"https://www.tiktok.com/@{creator.Username}/video/{postId}"
                            : null;
                        return With(new PublishOutcome(true, postId ?? publishId, permalink, null));
                    }

                    if (lastStatus == "FAILED")
                    {
                        var reason = TikTokJson.Get(data, "fail_reason");
                        return With(Fail(reason is { Length: > 0 }
                            ? $"TikTok could not publish the video: {reason}."
                            : "TikTok could not publish the video."));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // A missed status check is not a failed post: look again.
            }

            try
            {
                await Task.Delay(StatusPollInterval, within);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
        }

        return With(Unknown(
            $"TikTok was still processing the video ({lastStatus ?? "no answer"}). It may appear on the account shortly."));
    }

    /// <summary>The caption, cut to TikTok's limit without splitting an emoji.</summary>
    public static string Caption(string? caption)
    {
        var text = (caption ?? string.Empty).Trim();
        if (text.Length <= CaptionMax) return text;
        var cut = char.IsHighSurrogate(text[CaptionMax - 1]) ? CaptionMax - 1 : CaptionMax;
        return text[..cut];
    }

    private static PublishOutcome Refused(TikTokRefusal.Meaning m) =>
        new(false, null, null, m.Message, m.IsAuthFailure, m.IsRetryable);

    private static PublishOutcome Fail(string error) => new(false, null, null, error);
    private static PublishOutcome Retry(string error) => new(false, null, null, error, IsRetryable: true);
    private static PublishOutcome Unknown(string error) => new(false, null, null, error, OutcomeUnknown: true);
}

/// <summary>
/// The daily health check for a TikTok connection: reads the account with
/// its credential. Refreshes it when lapsed (always, on a daily check), so the
/// rotated credential comes back to be saved.
/// </summary>
public sealed class TikTokProbe(
    IHttpClientFactory httpFactory,
    IOptions<TikTokOptions> options,
    ILogger<TikTokProbe> log) : IRenewingConnectionProbe
{
    private readonly TikTokOptions _options = options.Value;

    public SocialPlatform Platform => SocialPlatform.TikTokAccount;

    public bool IsConfigured => _options.HasCredentials;

    /// <remarks>
    /// Drops any renewal, so it must not be the path the health check uses:
    /// <see cref="ConnectionHealthService"/> calls <see cref="ProbeAndRenewAsync"/>.
    /// </remarks>
    public async Task<(ProbeResult Result, string? Detail)> ProbeAsync(
        string externalAccountId, string accessToken, CancellationToken ct)
    {
        var (result, detail, _) = await ProbeAndRenewAsync(externalAccountId, accessToken, ct);
        return (result, detail);
    }

    public async Task<(ProbeResult Result, string? Detail, RenewedCredential? Renewed)> ProbeAndRenewAsync(
        string externalAccountId, string credential, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.TikTok);
        string access;
        RenewedCredential? renewed;
        try
        {
            (access, renewed) = await TikTokTokens.AccessTokenAsync(http, _options, credential, ct);
        }
        catch (TikTokTokens.RefreshFailed ex)
        {
            return (ex.IsAuthFailure ? ProbeResult.Unauthorized : ProbeResult.Inconclusive, ex.Message, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TikTokOAuthProvider.UserInfoUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized || TikTokJson.ErrorCode(body) == "access_token_invalid")
                return (ProbeResult.Unauthorized, "TikTok did not accept the sign-in. Reconnect it.", renewed);
            if (!response.IsSuccessStatusCode)
                return (ProbeResult.Inconclusive, TikTokJson.ErrorMessage(body) ?? $"TikTok answered {(int)response.StatusCode}.", renewed);

            var openId = TikTokJson.Get(body, "data", "user", "open_id");
            return openId is null || openId == externalAccountId
                ? (ProbeResult.Healthy, null, renewed)
                : (ProbeResult.Unauthorized, "This sign-in no longer belongs to the connected TikTok account.", renewed);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogInformation("TikTok health check could not reach TikTok; will look again tomorrow");
            return (ProbeResult.Inconclusive, "Could not reach TikTok.", renewed);
        }
    }
}
