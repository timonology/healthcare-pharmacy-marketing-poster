using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Acme.Application.Social;
using Acme.Domain.Social;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Shared Graph API plumbing for the Meta publishers.
/// </summary>
/// <remarks>
/// The error classification lives here because it is the part that decides
/// whether a failure is retried, and getting that wrong is expensive in both
/// directions: retrying a rejected caption burns rate limit for nothing, while
/// not retrying a transient blip means a pharmacy's post silently never goes
/// out.
/// </remarks>
public abstract class MetaPublisherBase(
    IHttpClientFactory httpFactory,
    IOptions<MetaOptions> options,
    ILogger log)
{
    protected readonly MetaOptions Options = options.Value;
    protected readonly ILogger Log = log;

    public bool IsConfigured => Options.IsConfigured;

    protected HttpClient Client() => httpFactory.CreateClient(SocialHttpClients.Meta);

    protected string Graph(string path) =>
        $"https://graph.facebook.com/{Options.GraphVersion}/{path}";

    /// <summary>
    /// POSTs form-encoded content with the token in the body, and returns the
    /// parsed response.
    /// </summary>
    /// <remarks>
    /// The token goes in the body, never the URL: URLs end up in proxy logs,
    /// traces and exception messages; bodies do not.
    /// </remarks>
    protected async Task<JsonDocument> PostAsync(
        HttpClient http, string url, string accessToken, Dictionary<string, string> form, CancellationToken ct)
    {
        form["access_token"] = accessToken;
        form["appsecret_proof"] = MetaAppSecretProof.For(accessToken, Options.AppSecret);

        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(url, content, ct);
        return await ReadAsync(response, ct);
    }

    /// <summary>GET with the token in the Authorization header rather than the query.</summary>
    protected async Task<JsonDocument> GetAsync(
        HttpClient http, string url, string accessToken, CancellationToken ct)
    {
        var separator = url.Contains('?') ? '&' : '?';
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{url}{separator}appsecret_proof={MetaAppSecretProof.For(accessToken, Options.AppSecret)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await http.SendAsync(request, ct);
        return await ReadAsync(response, ct);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            // A gateway page rather than Graph's answer: the request may or
            // may not have been carried out behind it.
            throw new MetaApiException(
                $"Meta returned a non-JSON response ({status}).",
                isAuthFailure: false,
                isRetryable: status >= 500)
            {
                MayHaveSucceeded = status >= 500,
            };
        }

        if (json.RootElement.TryGetProperty("error", out var error))
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32()
                : 0;
            var subCode = error.TryGetProperty("error_subcode", out var sc) && sc.ValueKind == JsonValueKind.Number
                ? sc.GetInt32()
                : 0;

            json.Dispose();
            throw Classify(message, code, subCode, status);
        }

        if (!response.IsSuccessStatusCode)
        {
            json.Dispose();
            throw new MetaApiException(
                $"Meta returned {status}.",
                isAuthFailure: status == 401,
                isRetryable: status >= 500)
            {
                MayHaveSucceeded = status >= 500,
            };
        }

        return json;
    }

    /// <summary>
    /// Turns a Graph error into "reconnect", "try again", "this will never
    /// work", or "it may have worked".
    /// </summary>
    /// <remarks>
    /// The codes are Meta's documented ones:
    ///   190, 102, 463, 467 — token invalid, expired or revoked. Only re-consent fixes it.
    ///   4, 17, 32, 341     — rate limits. Refused before anything happened; retry later.
    ///   1, 2               — unknown / temporary Graph fault. Retryable, but on a
    ///                        publishing call the post may exist anyway.
    ///   368                — temporarily blocked for policy violations. Not
    ///                        retried: the block lasts hours to days, and retrying
    ///                        into it can extend it.
    /// Everything else is content the platform will not accept, and is not
    /// retried — repeating a rejected caption produces the same rejection.
    /// </remarks>
    public static MetaApiException Classify(string? message, int code, int subCode, int httpStatus)
    {
        var auth = code is 190 or 102 or 463 or 467;
        var rateLimited = code is 4 or 17 or 32 or 341;
        var serverFault = code is 1 or 2 || httpStatus >= 500;
        var retryable = !auth && (rateLimited || serverFault);

        var text = message ?? "Meta rejected the post.";

        if (auth)
            text = "The connection to this account is no longer valid. Reconnect it and try again.";
        else if (code == 368)
            text = "Meta has temporarily blocked this account from posting. " + (message ?? string.Empty);

        return new MetaApiException(text.Trim(), auth, retryable)
        {
            Code = code,
            SubCode = subCode,
            MayHaveSucceeded = serverFault && !rateLimited && !auth,
        };
    }

    /// <summary>
    /// The outcome for a failure on the call that actually publishes.
    /// </summary>
    /// <remarks>
    /// Different from a failure on a preparatory call, because by then the
    /// request may have been carried out. A timeout, a dropped connection or a
    /// server fault there means "unknown", which stops automatic retries — a
    /// duplicate post cannot be taken back, a late one can be retried by hand.
    /// </remarks>
    protected static PublishOutcome FailedWhilePublishing(Exception ex, string platformName, CancellationToken ct) => ex switch
    {
        MetaApiException { MayHaveSucceeded: false } meta =>
            new PublishOutcome(false, null, null, meta.Message, meta.IsAuthFailure, meta.IsRetryable),
        MetaApiException meta =>
            new PublishOutcome(false, null, null, meta.Message, OutcomeUnknown: true),
        // The connection was never made, so nothing was sent: the one network
        // failure on a publishing call that is safe to retry.
        HttpRequestException
        {
            HttpRequestError: HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError
                or HttpRequestError.SecureConnectionError,
        } =>
            new PublishOutcome(false, null, null,
                $"Could not reach {platformName}. It will be tried again.", IsRetryable: true),
        OperationCanceledException when ct.IsCancellationRequested =>
            new PublishOutcome(false, null, null,
                $"Stopped while waiting for {platformName} to answer.", OutcomeUnknown: true),
        OperationCanceledException =>
            new PublishOutcome(false, null, null,
                $"{platformName} did not answer in time.", OutcomeUnknown: true),
        _ =>
            new PublishOutcome(false, null, null,
                $"The connection to {platformName} failed mid-request.", OutcomeUnknown: true),
    };

    /// <summary>
    /// The outcome for a failure before anything was published. Safe to retry.
    /// </summary>
    protected static PublishOutcome FailedBeforePublishing(Exception ex, string platformName) => ex switch
    {
        MetaApiException meta =>
            new PublishOutcome(false, null, null, meta.Message, meta.IsAuthFailure, meta.IsRetryable),
        _ =>
            new PublishOutcome(false, null, null,
                $"Could not reach {platformName}. It will be tried again.", IsRetryable: true),
    };
}

/// <summary>
/// HMAC-SHA256 of the access token keyed by the app secret.
/// </summary>
/// <remarks>
/// Meta's proof that a call comes from the app's own server, not from someone
/// holding a leaked token. Sent on every call so the app can have "Require App
/// Secret" switched on in its Meta settings, after which a stolen Page token
/// is useless on its own.
/// </remarks>
public static class MetaAppSecretProof
{
    public static string For(string accessToken, string appSecret)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(appSecret),
            Encoding.UTF8.GetBytes(accessToken));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class MetaApiException(string message, bool isAuthFailure, bool isRetryable)
    : Exception(message)
{
    public bool IsAuthFailure { get; } = isAuthFailure;
    public bool IsRetryable { get; } = isRetryable;
    public int Code { get; init; }
    public int SubCode { get; init; }

    /// <summary>
    /// True when Meta may have carried out the request despite the error: a
    /// server fault, as opposed to a refusal.
    /// </summary>
    public bool MayHaveSucceeded { get; init; }
}

/* ------------------------------------------------------------------ *
 * Facebook Page
 * ------------------------------------------------------------------ */

/// <summary>
/// Posts a photo or video to a Facebook Page.
/// </summary>
/// <remarks>
/// Single call in both cases, because Meta fetches the media from the URL we
/// supply rather than accepting an upload. That is also why the media URL has
/// to be reachable from the public internet — a localhost or private-network
/// URL fails with a message about the image being invalid, which sends you
/// looking at the image.
/// </remarks>
public sealed class FacebookPagePublisher(
    IHttpClientFactory httpFactory,
    IOptions<MetaOptions> options,
    ILogger<FacebookPagePublisher> log)
    : MetaPublisherBase(httpFactory, options, log), ISocialPublisher
{
    public SocialPlatform Platform => SocialPlatform.FacebookPage;

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var url = request.IsVideo
            ? Graph($"{request.ExternalAccountId}/videos")
            : Graph($"{request.ExternalAccountId}/photos");

        var form = new Dictionary<string, string>
        {
            // Videos take "description", photos take "caption". Sending the
            // wrong key is accepted silently and publishes an untitled post.
            [request.IsVideo ? "description" : "caption"] = request.Caption ?? string.Empty,
            [request.IsVideo ? "file_url" : "url"] = request.MediaUrl,
            ["published"] = "true",
        };

        // One call, and it is the one that publishes — so any failure that is
        // not a clear refusal leaves the outcome unknown.
        JsonDocument json;
        try
        {
            json = await PostAsync(Client(), url, request.AccessToken, form, ct);
        }
        catch (Exception ex) when (ex is MetaApiException or HttpRequestException
                                       or OperationCanceledException or IOException)
        {
            return FailedWhilePublishing(ex, "Facebook", ct);
        }

        using (json)
        {
            // Photos return both an object id and a post_id; the post_id is the
            // one that resolves to a viewable post.
            var postId =
                json.RootElement.TryGetProperty("post_id", out var p) ? p.GetString() :
                json.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;

            if (string.IsNullOrWhiteSpace(postId))
                return new PublishOutcome(false, null, null,
                    "Facebook accepted the request but returned no post id.", OutcomeUnknown: true);

            return new PublishOutcome(true, postId, $"https://www.facebook.com/{postId}", null);
        }
    }
}

/* ------------------------------------------------------------------ *
 * Instagram
 * ------------------------------------------------------------------ */

/// <summary>
/// Publishes to an Instagram Business account.
/// </summary>
/// <remarks>
/// <para>
/// Three steps, not one: create a media container, wait for Instagram to finish
/// ingesting it, then publish. The wait is not optional — publishing a
/// container still in IN_PROGRESS fails, and for video it routinely takes tens
/// of seconds.
/// </para>
/// <para>
/// This is why the worker lease is five minutes rather than thirty seconds,
/// and why the worker renews it before each account. If it lapses anyway,
/// the instance that reclaims the post marks this account uncertain rather
/// than starting a second container — see
/// <c>ScheduledPost.ResolveInterruptedAttempts</c>.
/// </para>
/// <para>
/// Only step 3 publishes. Failures before it are retryable; failures during
/// it are "may have gone out" and wait for a person.
/// </para>
/// </remarks>
public sealed class InstagramPublisher(
    IHttpClientFactory httpFactory,
    IOptions<MetaOptions> options,
    ILogger<InstagramPublisher> log)
    : MetaPublisherBase(httpFactory, options, log), ISocialPublisher
{
    public SocialPlatform Platform => SocialPlatform.InstagramBusiness;

    /// <summary>Attempts while waiting for ingestion, at 5s apart — about two minutes.</summary>
    private const int MaxStatusPolls = 24;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public async Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct)
    {
        var http = Client();
        string containerId;

        // Steps 1 and 2 publish nothing: an unpublished container is discarded
        // by Instagram. Any failure here is safe to retry.
        try
        {
            // 1. Container.
            var createForm = new Dictionary<string, string>
            {
                ["caption"] = request.Caption ?? string.Empty,
            };

            if (request.IsVideo)
            {
                // REELS is the only video container type Meta still accepts for
                // new publishes; the old VIDEO type was retired.
                createForm["media_type"] = "REELS";
                createForm["video_url"] = request.MediaUrl;
            }
            else
            {
                // Must be a JPEG no taller than 4:5 — the media resolver hands
                // Instagram its own variant of a poster for exactly this.
                createForm["image_url"] = request.MediaUrl;
            }

            using (var created = await PostAsync(
                http, Graph($"{request.ExternalAccountId}/media"), request.AccessToken, createForm, ct))
            {
                containerId = created.RootElement.TryGetProperty("id", out var idEl)
                    ? idEl.GetString() ?? string.Empty
                    : string.Empty;
            }

            if (string.IsNullOrWhiteSpace(containerId))
                return new PublishOutcome(false, null, null,
                    "Instagram did not return a media container.", IsRetryable: true);

            // 2. Wait for ingestion. Images are usually FINISHED immediately;
            //    video is not.
            var ready = await WaitForContainerAsync(http, containerId, request.AccessToken, ct);
            if (ready is { } notReady)
                return notReady;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down before publishing. Nothing went out.
            return new PublishOutcome(false, null, null,
                "Stopped before publishing to Instagram. It will be tried again.", IsRetryable: true);
        }
        catch (Exception ex) when (ex is MetaApiException or HttpRequestException
                                       or OperationCanceledException or IOException)
        {
            return FailedBeforePublishing(ex, "Instagram");
        }

        // 3. Publish. From here a failure may mean it went out.
        JsonDocument published;
        try
        {
            published = await PostAsync(
                http, Graph($"{request.ExternalAccountId}/media_publish"), request.AccessToken,
                new Dictionary<string, string> { ["creation_id"] = containerId }, ct);
        }
        catch (Exception ex) when (ex is MetaApiException or HttpRequestException
                                       or OperationCanceledException or IOException)
        {
            return FailedWhilePublishing(ex, "Instagram", ct);
        }

        using (published)
        {
            var mediaId = published.RootElement.TryGetProperty("id", out var pubId)
                ? pubId.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(mediaId))
                return new PublishOutcome(false, null, null,
                    "Instagram accepted the post but returned no media id.", OutcomeUnknown: true);

            return new PublishOutcome(true, mediaId, await PermalinkAsync(http, mediaId!, request.AccessToken), null);
        }
    }

    /// <summary>
    /// The post's public link, so the pharmacy gets a "View" button as they do
    /// for Facebook.
    /// </summary>
    /// <remarks>
    /// Best effort, and deliberately not cancellable by the caller: the post
    /// is already live, and nothing about fetching its link may turn a success
    /// into a failure — a failure here would be retried, and a retry is a
    /// second post. Short timeout; null on any problem.
    /// </remarks>
    private async Task<string?> PermalinkAsync(HttpClient http, string mediaId, string accessToken)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var json = await GetAsync(http, $"{Graph(mediaId)}?fields=permalink", accessToken, cts.Token);
            return json.RootElement.TryGetProperty("permalink", out var link)
                   && link.GetString() is { } url
                   && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttps
                ? url
                : null;
        }
        catch (Exception ex)
        {
            Log.LogInformation(ex, "Published Instagram media {Media}, but could not read its permalink", mediaId);
            return null;
        }
    }

    /// <summary>
    /// Polls the container until Instagram has finished ingesting the media.
    /// </summary>
    /// <returns>Null when ready, or the outcome to report when it is not.</returns>
    private async Task<PublishOutcome?> WaitForContainerAsync(
        HttpClient http, string containerId, string accessToken, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxStatusPolls; attempt++)
        {
            using var status = await GetAsync(
                http, $"{Graph(containerId)}?fields=status_code,status", accessToken, ct);

            var code = status.RootElement.TryGetProperty("status_code", out var codeEl)
                ? codeEl.GetString()
                : null;

            switch (code)
            {
                case "FINISHED":
                    return null;

                case "ERROR":
                    // Instagram's own explanation, which is usually specific —
                    // aspect ratio, duration, codec. Passed through verbatim
                    // because the pharmacy can act on it.
                    var detail = status.RootElement.TryGetProperty("status", out var s)
                        ? s.GetString()
                        : null;
                    return new PublishOutcome(false, null, null,
                        detail ?? "Instagram could not process this media.", IsRetryable: false);

                case "EXPIRED":
                    return new PublishOutcome(false, null, null,
                        "The media container expired before it could be published.", IsRetryable: true);
            }

            await Task.Delay(PollInterval, ct);
        }

        Log.LogWarning("Instagram container {Container} never reached FINISHED", containerId);

        // Retryable: the media may still finish ingesting. A fresh attempt
        // creates a new container rather than reusing this one, which is the
        // safe direction — an unpublished container is discarded by Instagram.
        return new PublishOutcome(false, null, null,
            "Instagram is still processing this media. It will be retried.", IsRetryable: true);
    }
}

/* ------------------------------------------------------------------ *
 * Health check
 * ------------------------------------------------------------------ */

/// <summary>
/// Reads the account's own id with the stored token — the cheapest call that
/// proves the token is still accepted and the account still reachable.
/// Publishes nothing.
/// </summary>
public abstract class MetaConnectionProbe(
    IHttpClientFactory httpFactory,
    IOptions<MetaOptions> options,
    ILogger log)
    : MetaPublisherBase(httpFactory, options, log), ISocialConnectionProbe
{
    public abstract SocialPlatform Platform { get; }

    public async Task<(ProbeResult Result, string? Detail)> ProbeAsync(
        string externalAccountId, string accessToken, CancellationToken ct)
    {
        try
        {
            using var json = await GetAsync(Client(), $"{Graph(externalAccountId)}?fields=id", accessToken, ct);
            return (ProbeResult.Healthy, null);
        }
        catch (MetaApiException ex) when (ex.IsAuthFailure)
        {
            return (ProbeResult.Unauthorized, ex.Message);
        }
        catch (MetaApiException ex) when (ex.Code == 100 && ex.SubCode == 33)
        {
            // "Object does not exist, or cannot be loaded due to missing
            // permissions": the Page was deleted, or this login lost its role.
            return (ProbeResult.Unauthorized,
                "This account can no longer be reached with the access we were given. Reconnect it.");
        }
        catch (Exception ex) when (ex is MetaApiException or HttpRequestException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return (ProbeResult.Inconclusive, ex.Message);
        }
    }
}

public sealed class FacebookPageProbe(
    IHttpClientFactory httpFactory, IOptions<MetaOptions> options, ILogger<FacebookPageProbe> log)
    : MetaConnectionProbe(httpFactory, options, log)
{
    public override SocialPlatform Platform => SocialPlatform.FacebookPage;
}

public sealed class InstagramProbe(
    IHttpClientFactory httpFactory, IOptions<MetaOptions> options, ILogger<InstagramProbe> log)
    : MetaConnectionProbe(httpFactory, options, log)
{
    public override SocialPlatform Platform => SocialPlatform.InstagramBusiness;
}
