using System.Net;
using System.Text;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// YouTube: connecting a channel, and how an upload's failures are classified
/// — "try again", "stop", "reconnect" or "it may already be up".
/// </summary>
public class YouTubeTests
{
    private static readonly GoogleOptions Options = new() { ClientId = "client", ClientSecret = "secret" };

    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string MediaUrl = "https://blob.test/videos/v1.mp4";
    private const string InitUrl = "https://www.googleapis.com/upload/youtube/v3/videos";
    private const string SessionUrl = "https://upload.test/session-1";

    private static PublishRequest Video(string caption = "Flu jabs now on\nWalk in, no appointment.") => new(
        SocialPlatform.YouTubeChannel, "UC123", "refresh-token-1", caption, MediaUrl, IsVideo: true);

    private static YouTubePublisher Publisher(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<YouTubePublisher>.Instance);

    /// <summary>The happy path, with any one step swapped out.</summary>
    private static ScriptedHandler Script(
        Func<HttpRequestMessage, HttpResponseMessage>? token = null,
        Func<HttpRequestMessage, HttpResponseMessage>? init = null,
        Func<HttpRequestMessage, HttpResponseMessage>? upload = null) =>
        new(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.StartsWith(TokenUrl)) return (token ?? (_ => Json(HttpStatusCode.OK, """{"access_token":"access-1","expires_in":3599}""")))(req);
            if (url.StartsWith(MediaUrl)) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) };
            if (url.StartsWith(InitUrl))
                return (init ?? (_ =>
                {
                    var r = new HttpResponseMessage(HttpStatusCode.OK);
                    r.Headers.Location = new Uri(SessionUrl);
                    return r;
                }))(req);
            if (url.StartsWith(SessionUrl))
                return (upload ?? (_ => Json(HttpStatusCode.OK, """{"id":"vid-1","status":{"privacyStatus":"public"}}""")))(req);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    /* ---------------- publishing ---------------- */

    [Fact]
    public async Task A_video_is_uploaded_and_linked()
    {
        var http = Script();

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("vid-1", outcome.ExternalPostId);
        Assert.Equal("https://youtu.be/vid-1", outcome.Permalink);

        // The refresh token goes in the token request body, never a URL.
        Assert.DoesNotContain(http.Requests, r => r.Uri.Contains("refresh-token-1"));
        Assert.Contains(http.Requests, r => r.Uri.StartsWith(TokenUrl) && r.Body.Contains("refresh-token-1"));

        // The upload is made with the fresh access token.
        Assert.All(http.Requests.Where(r => r.Uri.StartsWith(InitUrl) || r.Uri.StartsWith(SessionUrl)),
            r => Assert.Equal("Bearer access-1", r.Authorization));

        var metadata = http.Requests.Single(r => r.Uri.StartsWith(InitUrl)).Body;
        Assert.Contains("\"title\":\"Flu jabs now on\"", metadata);
        Assert.Contains("#Shorts", metadata);
        Assert.Contains("\"selfDeclaredMadeForKids\":false", metadata);
        Assert.Contains("\"privacyStatus\":\"public\"", metadata);
    }

    [Fact]
    public async Task A_poster_is_refused_without_calling_youtube()
    {
        var http = Script();
        var outcome = await Publisher(http).PublishAsync(Video() with { IsVideo = false }, default);

        Assert.False(outcome.Success);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_revoked_sign_in_asks_for_a_reconnect_and_uploads_nothing()
    {
        var http = Script(token: _ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.IsAuthFailure);
        Assert.Contains("Reconnect", outcome.Error);
        Assert.DoesNotContain(http.Requests, r => r.Uri.StartsWith(InitUrl) || r.Uri.StartsWith(SessionUrl));
    }

    [Fact]
    public async Task The_daily_quota_running_out_stops_rather_than_retrying_every_few_minutes()
    {
        var http = Script(init: _ => Json(HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"quota","errors":[{"reason":"quotaExceeded","domain":"youtube.quota"}]}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.False(outcome.Success);
        Assert.False(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
        Assert.Contains("daily upload allowance", outcome.Error);
    }

    [Fact]
    public async Task A_server_fault_before_the_upload_is_safe_to_retry()
    {
        var http = Script(init: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_timeout_while_sending_the_file_may_have_posted_it()
    {
        var http = Script(upload: _ => throw new TaskCanceledException("HttpClient.Timeout"));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.OutcomeUnknown);
        Assert.False(outcome.IsRetryable);
    }

    [Fact]
    public async Task A_server_fault_while_sending_the_file_may_have_posted_it()
    {
        var http = Script(upload: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task An_incomplete_upload_is_retried_because_no_video_was_made()
    {
        var http = Script(upload: _ => new HttpResponseMessage((HttpStatusCode)308));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public void The_whole_publish_fits_inside_the_workers_lease()
    {
        // A publish outliving its lease is reclaimed by another instance and
        // marked Uncertain while this one is still sending.
        Assert.True(YouTubePublisher.Budget < TimeSpan.FromSeconds(new PublishWorkerOptions().LeaseSeconds));
    }

    [Fact]
    public async Task An_unreadable_video_file_is_retried_and_nothing_is_sent_to_youtube()
    {
        var http = new ScriptedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.StartsWith(TokenUrl)) return Json(HttpStatusCode.OK, """{"access_token":"access-1"}""");
            if (url.StartsWith(MediaUrl)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            throw new InvalidOperationException("YouTube must not be called");
        });

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.IsRetryable);
    }

    /* ---------------- title and description ---------------- */

    [Theory]
    [InlineData("Flu jabs now on\nWalk in today", "Flu jabs now on")]
    [InlineData("  <b>Blood   pressure</b> checks ", "bBlood pressure/b checks")]
    [InlineData("", YouTubeText.FallbackTitle)]
    [InlineData("\n\n", YouTubeText.FallbackTitle)]
    public void The_title_is_the_first_line_of_the_caption(string caption, string expected) =>
        Assert.Equal(expected, YouTubeText.Title(caption));

    [Fact]
    public void A_long_title_is_cut_to_youtubes_limit()
    {
        var title = YouTubeText.Title(new string('a', 300));
        Assert.True(title.Length <= YouTubeText.TitleMax);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void The_description_is_tagged_as_a_short_once()
    {
        Assert.EndsWith("#Shorts", YouTubeText.Description("Book now"));
        Assert.Equal("Book now #shorts", YouTubeText.Description("Book now #shorts"));
    }

    /* ---------------- the platform ---------------- */

    [Fact]
    public void YouTube_takes_videos_and_not_posters()
    {
        Assert.True(SocialPlatform.YouTubeChannel.IsPublishTarget());
        Assert.True(SocialPlatform.YouTubeChannel.SupportsVideo());
        Assert.False(SocialPlatform.YouTubeChannel.SupportsImages());
        Assert.True(SocialPlatform.FacebookPage.SupportsImages());
        Assert.Equal("YouTube", SocialPlatform.YouTubeChannel.DisplayName());
    }

    /* ---------------- connecting ---------------- */

    private static YouTubeOAuthProvider Provider(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<YouTubeOAuthProvider>.Instance);

    private const string GrantedBoth =
        "https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly";

    [Fact]
    public async Task Connecting_lists_the_channel_and_keeps_the_refresh_token_as_the_credential()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString().StartsWith(TokenUrl)
            ? Json(HttpStatusCode.OK, $$"""{"access_token":"a","refresh_token":"r","scope":"{{GrantedBoth}}"}""")
            : Json(HttpStatusCode.OK, """{"items":[{"id":"UC123","snippet":{"title":"High St Pharmacy","thumbnails":{"default":{"url":"https://yt/a.jpg"}}}}]}"""));

        var accounts = await Provider(http).ExchangeCodeAsync("code", "https://app/social/callback/youtube", default);

        var channel = Assert.Single(accounts);
        Assert.Equal("UC123", channel.ExternalAccountId);
        Assert.Equal("High St Pharmacy", channel.DisplayName);
        Assert.Equal(SocialPlatform.YouTubeChannel, channel.Platform);
        Assert.Equal("r", channel.AccessToken);   // an access token would lapse in an hour
        Assert.Equal("r", channel.RefreshToken);
        Assert.Null(channel.ExpiresAtUtc);
    }

    [Fact]
    public async Task Connecting_without_the_upload_permission_is_refused_plainly()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","scope":"https://www.googleapis.com/auth/youtube.readonly"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", default));
        Assert.Contains("upload", ex.Message);
    }

    [Fact]
    public async Task An_account_with_no_channel_is_told_to_make_one()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString().StartsWith(TokenUrl)
            ? Json(HttpStatusCode.OK, $$"""{"access_token":"a","refresh_token":"r","scope":"{{GrantedBoth}}"}""")
            : Json(HttpStatusCode.OK, """{"items":[]}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", default));
        Assert.Contains("no YouTube channel", ex.Message);
    }

    [Fact]
    public void The_consent_asks_only_for_youtube()
    {
        var url = Provider(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
            .BuildAuthorizationUrl("state-1", "https://app/social/callback/youtube");

        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/youtube.upload"), url);
        Assert.DoesNotContain("adwords", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("prompt=consent", url);
    }

    [Theory]
    [InlineData("123-abc.apps.googleusercontent.com ", "123-abc.apps.googleusercontent.com")]
    [InlineData("\"123-abc.apps.googleusercontent.com\"", "123-abc.apps.googleusercontent.com")]
    [InlineData(" '123-abc.apps.googleusercontent.com'\n", "123-abc.apps.googleusercontent.com")]
    [InlineData(null, "")]
    public void A_pasted_client_id_is_tidied_before_google_sees_it(string? pasted, string expected)
    {
        // A stray space or quote makes Google answer "The OAuth client was not found".
        Assert.Equal(expected, SocialCredentials.Clean(pasted));
    }

    [Theory]
    [InlineData("http://123-abc.apps.googleusercontent.com", "123-abc.apps.googleusercontent.com")]
    [InlineData("https://123-abc.apps.googleusercontent.com/", "123-abc.apps.googleusercontent.com")]
    [InlineData(" 123-abc.apps.googleusercontent.com", "123-abc.apps.googleusercontent.com")]
    public void A_client_id_pasted_as_an_address_is_put_right(string pasted, string expected) =>
        // The 2 November production failure: "http://…apps.googleusercontent.com".
        Assert.Equal(expected, SocialCredentials.CleanGoogleClientId(pasted));

    /* ---------------- fakes ---------------- */

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Uri, string Body, string? Authorization);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null || request.Content is ByteArrayContent && request.Method == HttpMethod.Put
                ? string.Empty
                : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
