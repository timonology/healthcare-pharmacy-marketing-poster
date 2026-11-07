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
 * YouTube: connect a channel with Google sign-in, upload a video to it.
 *
 * The credential stored on a YouTube connection is the Google *refresh*
 * token — in both AccessToken and RefreshToken. An access token lasts an
 * hour, so storing one would make the connection look broken an hour after
 * connecting; the publisher and the health probe exchange the refresh token
 * for a fresh access token on every call instead. (Google Ads does the same;
 * see GoogleOAuthProvider.)
 */

/// <summary>Exchanges a Google refresh token for an access token.</summary>
internal static class GoogleTokens
{
    public sealed class RefreshFailed(string message, bool isAuthFailure, bool isRetryable) : Exception(message)
    {
        public bool IsAuthFailure { get; } = isAuthFailure;
        public bool IsRetryable { get; } = isRetryable;
    }

    public static async Task<string> AccessTokenAsync(
        HttpClient http, GoogleOptions options, string refreshToken, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        });

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Nothing was sent to YouTube yet: safe to try again later.
            throw new RefreshFailed("Could not reach Google to sign in. Trying again automatically.", false, true);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            }
            catch (JsonException)
            {
                // An HTML error page from a proxy or an outage: nothing was posted.
                throw new RefreshFailed("Google sign-in is having problems. Trying again automatically.", false, true);
            }

            using var _ = json;
            if (response.IsSuccessStatusCode
                && json.RootElement.TryGetProperty("access_token", out var at)
                && at.GetString() is { Length: > 0 } token)
                return token;

            var error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (error == "invalid_grant")
                throw new RefreshFailed(
                    "The connection to this YouTube channel is no longer valid. Reconnect it in Social & Ads.",
                    isAuthFailure: true, isRetryable: false);

            var serverSide = (int)response.StatusCode >= 500;
            throw new RefreshFailed(
                serverSide
                    ? "Google sign-in is having problems. Trying again automatically."
                    : "Google would not issue an access token for this channel.",
                isAuthFailure: false, isRetryable: serverSide);
        }
    }
}

/// <summary>
/// Google sign-in for a YouTube channel.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="GoogleOAuthProvider"/> (which connects Google Ads)
/// so each consent asks only for what it is used for. <c>youtube.upload</c>
/// posts videos; <c>youtube.readonly</c> is needed to find the channel the
/// signed-in account owns (<c>channels?mine=true</c>) and to check it later.
/// </para>
/// <para>
/// <b>No revocation at Google on disconnect.</b> Google revokes a whole
/// sign-in, not one scope, so withdrawing the YouTube grant could also cut off
/// the same person's Google Ads connection. Disconnecting deletes our copy of
/// the credential; to withdraw it at Google too, the pharmacy removes the app
/// at myaccount.google.com/permissions.
/// </para>
/// </remarks>
public sealed class YouTubeOAuthProvider(
    IHttpClientFactory httpFactory,
    IOptions<GoogleOptions> options,
    ILogger<YouTubeOAuthProvider> log) : ISocialOAuthProvider
{
    private readonly GoogleOptions _options = options.Value;

    public static readonly string[] Scopes =
    {
        "https://www.googleapis.com/auth/youtube.upload",
        "https://www.googleapis.com/auth/youtube.readonly",
    };

    public SocialOAuthProviderKind Kind => SocialOAuthProviderKind.YouTube;

    public bool IsConfigured => _options.IsYouTubeConfigured;

    public string BuildAuthorizationUrl(string state, string redirectUri)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["scope"] = string.Join(" ", Scopes),
            ["response_type"] = "code",
            // Both needed to be sure of a refresh token (see GoogleOAuthProvider).
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            // Not merged with any Ads grant: this consent is for YouTube only.
            ["include_granted_scopes"] = "false",
        };

        return "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", query
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
    }

    public async Task<IReadOnlyList<DiscoveredAccount>> ExchangeCodeAsync(
        string code, string redirectUri, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.Google);

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        });

        using var tokenResponse = await http.PostAsync("https://oauth2.googleapis.com/token", form, ct);
        var tokenText = await tokenResponse.Content.ReadAsStringAsync(ct);
        using var tokenJson = JsonDocument.Parse(string.IsNullOrWhiteSpace(tokenText) ? "{}" : tokenText);

        if (tokenJson.RootElement.TryGetProperty("error", out var err))
        {
            var description = tokenJson.RootElement.TryGetProperty("error_description", out var d) ? d.GetString() : err.GetString();
            throw new InvalidOperationException(description ?? "Google rejected the sign-in.");
        }

        var accessToken = tokenJson.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        var refreshToken = tokenJson.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Google did not return an access token.");
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                "Google did not return a refresh token. Remove this app's access at " +
                "myaccount.google.com/permissions and connect YouTube again.");

        // The scopes actually granted: Google lets people untick one.
        var granted = tokenJson.RootElement.TryGetProperty("scope", out var sc)
            ? (sc.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : Scopes;
        if (!granted.Contains(Scopes[0]))
            throw new InvalidOperationException(
                "Permission to upload videos was not given, so nothing can be posted. Connect again and allow it.");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://www.googleapis.com/youtube/v3/channels?part=snippet&mine=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var channelResponse = await http.SendAsync(request, ct);
        var channelText = await channelResponse.Content.ReadAsStringAsync(ct);

        if (!channelResponse.IsSuccessStatusCode)
        {
            log.LogWarning("YouTube channel lookup after consent answered {Status}", (int)channelResponse.StatusCode);
            throw new InvalidOperationException(
                $"Signed in to Google, but could not read your YouTube channel: {YouTubeErrors.Message(channelText) ?? $"YouTube answered {(int)channelResponse.StatusCode}."}");
        }

        using var channels = JsonDocument.Parse(string.IsNullOrWhiteSpace(channelText) ? "{}" : channelText);
        var accounts = new List<DiscoveredAccount>();
        if (channels.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;
                var snippet = item.TryGetProperty("snippet", out var sn) ? sn : default;
                var title = snippet.ValueKind == JsonValueKind.Object && snippet.TryGetProperty("title", out var t)
                    ? t.GetString()
                    : null;
                string? avatar = null;
                if (snippet.ValueKind == JsonValueKind.Object
                    && snippet.TryGetProperty("thumbnails", out var th)
                    && th.TryGetProperty("default", out var def)
                    && def.TryGetProperty("url", out var url))
                    avatar = url.GetString();

                accounts.Add(new DiscoveredAccount(
                    id!,
                    string.IsNullOrWhiteSpace(title) ? "YouTube channel" : title!,
                    SocialPlatform.YouTubeChannel,
                    // The refresh token is the credential (see the file header).
                    AccessToken: refreshToken!,
                    RefreshToken: refreshToken,
                    ExpiresAtUtc: null,
                    Scopes: granted,
                    AvatarUrl: avatar));
            }
        }

        if (accounts.Count == 0)
            throw new InvalidOperationException(
                "This Google account has no YouTube channel. Create one at youtube.com, then connect again.");

        return accounts;
    }
}

/// <summary>Reads YouTube's error body: <c>{"error":{"message","errors":[{"reason"}]}}</c>.</summary>
internal static class YouTubeErrors
{
    public static string? Reason(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("errors", out var list)
                && list.ValueKind == JsonValueKind.Array)
                foreach (var e in list.EnumerateArray())
                    if (e.TryGetProperty("reason", out var r)) return r.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public static string? Message(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m))
                return m.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}

/// <summary>Title and description from the post's caption.</summary>
/// <remarks>
/// The composer has one caption box for every platform. On YouTube its first
/// line becomes the title (YouTube requires one, up to 100 characters) and
/// the whole caption the description. YouTube refuses angle brackets in
/// both, so they are removed rather than letting the upload fail.
/// </remarks>
public static class YouTubeText
{
    public const int TitleMax = 100;
    public const int DescriptionMax = 5000;
    public const string FallbackTitle = "Pharmacy update";

    public static string Title(string? caption)
    {
        var first = (caption ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        var clean = Collapse(Strip(first));
        if (clean.Length == 0) return FallbackTitle;
        return clean.Length <= TitleMax ? clean : clean[..(TitleMax - 1)].TrimEnd() + "…";
    }

    public static string Description(string? caption)
    {
        var body = Strip(caption ?? "").Trim();
        // Short vertical videos are Shorts either way; the tag helps them be
        // shown in the Shorts shelf.
        if (!body.Contains("#Shorts", StringComparison.OrdinalIgnoreCase))
            body = body.Length == 0 ? "#Shorts" : body + "\n\n#Shorts";
        return body.Length <= DescriptionMax ? body : body[..DescriptionMax];
    }

    private static string Strip(string s) => s.Replace("<", "").Replace(">", "");

    private static string Collapse(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Uploads a video to a YouTube channel.
/// </summary>
/// <remarks>
/// <para>
/// A resumable upload in two calls: one to open an upload session with the
/// title, description and privacy (nothing exists on YouTube yet), one to send
/// the file (which creates the video). The classification follows that split:
/// </para>
/// <list type="bullet">
/// <item>anything failing before or at the first call is certain: nothing
/// was posted — retried automatically if transient;</item>
/// <item>a timeout, dropped connection or server error on the second call is
/// <b>Uncertain</b> — the video may exist — and is never retried
/// automatically;</item>
/// <item>a refused sign-in marks the connection for reconnecting.</item>
/// </list>
/// <para>
/// <b>Projects not yet audited by Google</b> have every upload locked to
/// Private, whatever is asked for. The upload still succeeds; the log says
/// when YouTube returned a different privacy than requested.
/// </para>
/// </remarks>
public sealed class YouTubePublisher(
    IHttpClientFactory httpFactory,
    IOptions<GoogleOptions> options,
    ILogger<YouTubePublisher> log) : ISocialPublisher
{
    private readonly GoogleOptions _options = options.Value;

    /// <summary>Well above a 15–60 second video; a guard against fetching something absurd.</summary>
    private const long MaxBytes = 512L * 1024 * 1024;

    /// <summary>The whole publish, end to end. Below the worker's default 300-second lease.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(270);

    public SocialPlatform Platform => SocialPlatform.YouTubeChannel;

    public bool IsConfigured => _options.IsYouTubeConfigured;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        if (!request.IsVideo)
            return Fail("YouTube only takes videos.");

        var http = httpFactory.CreateClient(SocialHttpClients.YouTube);

        // One budget for the whole publish, inside the worker's five-minute
        // lease (see the YouTube client's registration). Running out counts as
        // a timeout — before the file is sent, retried; while sending, Uncertain.
        // `ct` alone still means the host is stopping.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var within = budget.Token;

        // 1. Sign in. Nothing posted yet.
        string accessToken;
        try
        {
            accessToken = await GoogleTokens.AccessTokenAsync(http, _options, request.AccessToken, within);
        }
        catch (GoogleTokens.RefreshFailed ex)
        {
            return new PublishOutcome(false, null, null, ex.Message, ex.IsAuthFailure, ex.IsRetryable);
        }

        // 2. Fetch our own file. Nothing posted yet.
        byte[] video;
        try
        {
            using var media = await http.GetAsync(request.MediaUrl, HttpCompletionOption.ResponseHeadersRead, within);
            if (!media.IsSuccessStatusCode)
                return Retry($"Could not read the video file ({(int)media.StatusCode}). Trying again automatically.");
            if (media.Content.Headers.ContentLength is > MaxBytes)
                return Fail("This video file is too large to upload.");
            video = await media.Content.ReadAsByteArrayAsync(within);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Retry("Could not read the video file. Trying again automatically.");
        }

        if (video.Length == 0) return Fail("The video file is empty.");

        // 3. Open the upload session. Nothing posted yet.
        var privacy = _options.YouTubePrivacy is "public" or "unlisted" or "private" ? _options.YouTubePrivacy : "public";
        var metadata = JsonSerializer.Serialize(new
        {
            snippet = new
            {
                title = YouTubeText.Title(request.Caption),
                description = YouTubeText.Description(request.Caption),
                categoryId = string.IsNullOrWhiteSpace(_options.YouTubeCategoryId) ? "22" : _options.YouTubeCategoryId,
            },
            status = new
            {
                privacyStatus = privacy,
                // Required by YouTube for every upload. A pharmacy's health
                // marketing is not made for children.
                selfDeclaredMadeForKids = false,
            },
        });

        Uri uploadUrl;
        try
        {
            using var init = new HttpRequestMessage(HttpMethod.Post,
                "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status")
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
            };
            init.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            init.Headers.Add("X-Upload-Content-Type", "video/mp4");
            init.Headers.Add("X-Upload-Content-Length", video.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var response = await http.SendAsync(init, within);
            if (!response.IsSuccessStatusCode || response.Headers.Location is null)
                return Refused(response.StatusCode, await response.Content.ReadAsStringAsync(within), beforeUpload: true);
            uploadUrl = response.Headers.Location;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Only an upload session, never a video: safe to try again.
            return Retry("Could not reach YouTube. Trying again automatically.");
        }

        // 4. Send the file. This is the call that creates the video.
        try
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
            {
                Content = new ByteArrayContent(video),
            };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(put, within);
            var body = await response.Content.ReadAsStringAsync(within);

            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var id = json.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id))
                    return Unknown("YouTube accepted the upload but did not return a video id.");

                if (json.RootElement.TryGetProperty("status", out var st)
                    && st.TryGetProperty("privacyStatus", out var ps)
                    && ps.GetString() is { } got && got != privacy)
                    log.LogWarning(
                        "YouTube made video {Video} {Got} instead of {Asked}. Projects Google has not audited " +
                        "are limited to private uploads.", id, got, privacy);

                return new PublishOutcome(true, id, $"https://youtu.be/{id}", null);
            }

            // 308 is YouTube's "resume incomplete": it did not get the whole
            // file, so no video was made. Safe to start again.
            if ((int)response.StatusCode == 308)
                return Retry("YouTube did not receive the whole video. Trying again automatically.");

            // A server error after the bytes went may still have made a video.
            if ((int)response.StatusCode >= 500)
                return Unknown($"YouTube answered {(int)response.StatusCode} while receiving the video.");

            return Refused(response.StatusCode, body, beforeUpload: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "YouTube upload was interrupted for channel {Channel}", request.ExternalAccountId);
            return Unknown("The upload was interrupted and may have gone through.");
        }
    }

    /// <summary>What YouTube's refusal means, in the pharmacy's words.</summary>
    internal static PublishOutcome Refused(HttpStatusCode status, string body, bool beforeUpload)
    {
        var reason = YouTubeErrors.Reason(body);
        var message = YouTubeErrors.Message(body);

        if (status == HttpStatusCode.Unauthorized || reason is "authError" or "unauthorized")
            return new PublishOutcome(false, null, null,
                "YouTube did not accept this channel's sign-in. Reconnect it in Social & Ads.",
                IsAuthFailure: true);

        switch (reason)
        {
            case "quotaExceeded":
            case "dailyLimitExceeded":
                // Resets at midnight Pacific; retrying in minutes only burns attempts.
                return Fail("YouTube's daily upload allowance for this app is used up. It resets overnight (US Pacific time); retry tomorrow.");
            case "uploadLimitExceeded":
                return Fail("This channel has reached YouTube's daily upload limit. Retry tomorrow.");
            case "rateLimitExceeded":
            case "userRateLimitExceeded":
                return Retry("YouTube asked us to slow down. Trying again automatically.");
            case "youtubeSignupRequired":
                return new PublishOutcome(false, null, null,
                    "This Google account has no YouTube channel any more.", IsAuthFailure: true);
            case "forbidden":
            case "insufficientPermissions":
                return new PublishOutcome(false, null, null,
                    "Permission to upload to this channel was not given. Reconnect it and allow uploads.",
                    IsAuthFailure: true);
        }

        if (beforeUpload && (int)status >= 500)
            return Retry("YouTube is having problems. Trying again automatically.");

        return Fail(message is { Length: > 0 }
            ? $"YouTube refused the video: {message}"
            : $"YouTube refused the video ({(int)status}).");
    }

    private static PublishOutcome Fail(string error) => new(false, null, null, error);
    private static PublishOutcome Retry(string error) => new(false, null, null, error, IsRetryable: true);
    private static PublishOutcome Unknown(string error) => new(false, null, null, error, OutcomeUnknown: true);
}

/// <summary>The daily health check for a YouTube connection: sign in and read the channel. Posts nothing.</summary>
public sealed class YouTubeProbe(
    IHttpClientFactory httpFactory,
    IOptions<GoogleOptions> options,
    ILogger<YouTubeProbe> log) : ISocialConnectionProbe
{
    private readonly GoogleOptions _options = options.Value;

    public SocialPlatform Platform => SocialPlatform.YouTubeChannel;

    public bool IsConfigured => _options.IsYouTubeConfigured;

    public async Task<(ProbeResult Result, string? Detail)> ProbeAsync(
        string externalAccountId, string accessToken, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(SocialHttpClients.Google);
        string token;
        try
        {
            token = await GoogleTokens.AccessTokenAsync(http, _options, accessToken, ct);
        }
        catch (GoogleTokens.RefreshFailed ex)
        {
            return (ex.IsAuthFailure ? ProbeResult.Unauthorized : ProbeResult.Inconclusive, ex.Message);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.googleapis.com/youtube/v3/channels?part=id&id={Uri.EscapeDataString(externalAccountId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
                return (ProbeResult.Unauthorized, "YouTube did not accept the sign-in.");
            if (!response.IsSuccessStatusCode)
                return (ProbeResult.Inconclusive, YouTubeErrors.Message(body) ?? $"YouTube answered {(int)response.StatusCode}.");

            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var found = json.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0;
            return found
                ? (ProbeResult.Healthy, null)
                : (ProbeResult.Unauthorized, "The YouTube channel could not be found.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogInformation("YouTube health check could not reach YouTube; will look again tomorrow");
            return (ProbeResult.Inconclusive, "Could not reach YouTube.");
        }
    }
}
