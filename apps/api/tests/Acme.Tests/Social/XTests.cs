using System.Net;
using System.Text;
using Acme.Application.Social;
using Acme.Domain.Common;
using Acme.Domain.Social;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// X: the 280 rule counted X's way, connecting with PKCE, the rotating
/// credential, the chunked media upload, and how each failure of a post is
/// classified — "try again", "stop", "reconnect" or "it may already be up".
/// </summary>
public class XTests
{
    private static readonly XOptions Settings = new() { ClientId = "x-client", ClientSecret = "x-secret" };

    private const string TokenUrl = "https://api.x.com/2/oauth2/token";
    private const string RevokeUrl = "https://api.x.com/2/oauth2/revoke";
    private const string MeUrl = "https://api.x.com/2/users/me?user.fields=profile_image_url";
    private const string InitUrl = "https://api.x.com/2/media/upload/initialize";
    private const string AppendUrl = "https://api.x.com/2/media/upload/1880028106020515840/append";
    private const string FinalizeUrl = "https://api.x.com/2/media/upload/1880028106020515840/finalize";
    private const string StatusUrl = "https://api.x.com/2/media/upload?command=STATUS&media_id=1880028106020515840";
    private const string PostsUrl = "https://api.x.com/2/tweets";
    private const string ImageUrl = "https://blob.test/posters/p1.jpg";
    private const string VideoUrl = "https://blob.test/videos/v1.mp4";

    private static readonly byte[] Png = Enumerable.Repeat((byte)0, 2048)
        .Select((b, i) => i < 8 ? new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }[i] : b).ToArray();

    private static string Fresh(string access = "acc-1", string refresh = "ref-1") =>
        new XCredential(access, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), refresh).Serialize();

    private static string Lapsed(string refresh = "ref-1") =>
        new XCredential("acc-old", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), refresh).Serialize();

    private static PublishRequest Poster(string? credential = null, string caption = "Flu jabs now on #FluJab", Func<RenewedCredential, Task>? save = null) => new(
        SocialPlatform.XAccount, "x-user-1", credential ?? Fresh(), caption, ImageUrl, IsVideo: false, SaveRenewal: save);

    private static PublishRequest Video(string? credential = null) => new(
        SocialPlatform.XAccount, "x-user-1", credential ?? Fresh(), "Our flu clinic", VideoUrl, IsVideo: true);

    private static XPublisher Publisher(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(Settings), NullLogger<XPublisher>.Instance)
        {
            ProcessingDelay = _ => TimeSpan.Zero,
            ProcessingLimit = TimeSpan.FromSeconds(5),
        };

    /// <summary>The happy path, with any one step swapped out.</summary>
    private static ScriptedHandler Script(
        Func<HttpRequestMessage, HttpResponseMessage>? token = null,
        byte[]? file = null,
        Func<HttpRequestMessage, HttpResponseMessage>? init = null,
        Func<HttpRequestMessage, HttpResponseMessage>? finalize = null,
        Func<int, HttpResponseMessage>? status = null,
        Func<HttpRequestMessage, HttpResponseMessage>? post = null)
    {
        var polls = 0;
        return new ScriptedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == TokenUrl)
                return (token ?? (_ => Json(HttpStatusCode.OK,
                    """{"token_type":"bearer","expires_in":7200,"access_token":"acc-2","scope":"tweet.read tweet.write users.read media.write offline.access","refresh_token":"ref-2"}""")))(req);
            if (url is ImageUrl or VideoUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file ?? Png) };
            if (url == InitUrl)
                return (init ?? (_ => Json(HttpStatusCode.OK, """{"data":{"id":"1880028106020515840","expires_after_secs":86400}}""")))(req);
            if (url == AppendUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            if (url == FinalizeUrl)
                return (finalize ?? (_ => Json(HttpStatusCode.OK, """{"data":{"id":"1880028106020515840","size":2048}}""")))(req);
            if (url == StatusUrl)
                return (status ?? (_ => Json(HttpStatusCode.OK,
                    """{"data":{"id":"1880028106020515840","processing_info":{"state":"succeeded"}}}""")))(++polls);
            if (url == PostsUrl)
                return (post ?? (_ => Json(HttpStatusCode.Created, """{"data":{"id":"1445880548472328192","text":"Flu jabs"}}""")))(req);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    /* ---------------- the 280 rule ---------------- */

    // The same cases as apps/web/src/lib/x-text.test.ts: the two must agree.
    [Theory]
    [InlineData("Flu jabs now available", 22)]
    [InlineData("", 0)]
    [InlineData("café", 4)]
    [InlineData("café", 4)]
    [InlineData("https://example.com", 23)]
    [InlineData("Book: https://example.com/a/very/long/path?with=query", 29)]
    [InlineData("Visit pharmacy.co.uk today", 35)]
    [InlineData("www.nhs.uk", 23)]
    [InlineData("me@x.co", 7)]
    [InlineData("💊", 2)]
    [InlineData("👩‍⚕️", 2)]
    [InlineData("👍🏽", 2)]
    [InlineData("❤️", 2)]
    [InlineData("薬局", 4)]
    [InlineData("“hi” — ok", 9)]
    public void Text_is_counted_the_way_x_counts_it(string text, int expected)
    {
        Assert.Equal(expected, XText.WeightedLength(text));
    }

    [Fact]
    public void The_limit_is_280_counted_not_characters()
    {
        Assert.True(XText.Fits(new string('a', 280)));
        Assert.False(XText.Fits(new string('a', 281)));
        Assert.True(XText.Fits(string.Concat(Enumerable.Repeat("💊", 140))));
        Assert.False(XText.Fits(string.Concat(Enumerable.Repeat("💊", 141))));
        Assert.True(XText.Fits(null));
    }

    [Fact]
    public void A_post_to_x_with_a_caption_too_long_for_x_is_refused_when_composing()
    {
        var ex = Assert.Throws<DomainException>(() => ScheduledPost.Create(
            "owner", PublishSourceKind.Poster, "p1", "Poster", new string('a', 300), null,
            new[] { ("c1", SocialPlatform.FacebookPage), ("c2", SocialPlatform.XAccount) }));
        Assert.Contains("too long for X", ex.Message);

        // The same caption without X is fine: Facebook's limit is far higher.
        ScheduledPost.Create("owner", PublishSourceKind.Poster, "p1", "Poster", new string('a', 300), null,
            new[] { ("c1", SocialPlatform.FacebookPage) });
    }

    [Fact]
    public void X_gets_the_jpeg_version_of_a_poster_when_there_is_one()
    {
        var withJpeg = new ResolvedMedia("https://b/p.png", false, "P", InstagramUrl: "https://b/p.jpg");
        var pngOnly = new ResolvedMedia("https://b/p.png", false, "P");
        var video = new ResolvedMedia("https://b/v.mp4", true, "V");

        Assert.Equal("https://b/p.jpg", withJpeg.UrlFor(SocialPlatform.XAccount));
        Assert.Equal("https://b/p.png", pngOnly.UrlFor(SocialPlatform.XAccount));
        Assert.Equal("https://b/v.mp4", video.UrlFor(SocialPlatform.XAccount));
        // Unchanged for the others.
        Assert.Null(pngOnly.UrlFor(SocialPlatform.InstagramBusiness));
        Assert.Equal("https://b/p.png", withJpeg.UrlFor(SocialPlatform.FacebookPage));
    }

    [Fact]
    public void X_takes_posters_and_videos()
    {
        Assert.True(SocialPlatform.XAccount.IsPublishTarget());
        Assert.True(SocialPlatform.XAccount.SupportsImages());
        Assert.True(SocialPlatform.XAccount.SupportsVideo());
        Assert.Equal("X", SocialPlatform.XAccount.DisplayName());
        Assert.Equal(8, (int)SocialPlatform.XAccount);              // persisted: never renumber
        Assert.Equal(5, (int)SocialOAuthProviderKind.X);
    }

    /* ---------------- posting ---------------- */

    [Fact]
    public async Task A_poster_is_uploaded_then_posted_with_its_caption_and_linked()
    {
        var http = Script();

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("1445880548472328192", outcome.ExternalPostId);
        Assert.Equal("https://x.com/i/web/status/1445880548472328192", outcome.Permalink);
        Assert.Null(outcome.RenewedCredential);   // the access token was still good

        var init = http.Requests.Single(r => r.Uri == InitUrl);
        Assert.Contains("\"media_type\":\"image/png\"", init.Body);
        Assert.Contains("\"media_category\":\"tweet_image\"", init.Body);
        Assert.Contains("\"total_bytes\":2048", init.Body);
        Assert.Equal("Bearer acc-1", init.Authorization);

        var append = Assert.Single(http.Requests, r => r.Uri == AppendUrl);
        Assert.Contains("name=\"segment_index\"", append.Body);
        Assert.Contains("name=\"media\"", append.Body);

        var post = http.Requests.Single(r => r.Uri == PostsUrl);
        Assert.Contains("\"text\":\"Flu jabs now on #FluJab\"", post.Body);
        Assert.Contains("\"media_ids\":[\"1880028106020515840\"]", post.Body);
        Assert.DoesNotContain(http.Requests, r => r.Uri == StatusUrl);   // an image is ready at finalize
        Assert.DoesNotContain(http.Requests, r => r.Uri == TokenUrl);
    }

    [Fact]
    public async Task A_video_is_posted_once_x_has_processed_it()
    {
        var http = Script(
            file: new byte[4096],
            finalize: _ => Json(HttpStatusCode.OK, """{"data":{"id":"1880028106020515840","processing_info":{"state":"pending","check_after_secs":1}}}"""),
            status: n => n == 1
                ? Json(HttpStatusCode.OK, """{"data":{"processing_info":{"state":"in_progress","check_after_secs":1,"progress_percent":40}}}""")
                : Json(HttpStatusCode.OK, """{"data":{"processing_info":{"state":"succeeded","progress_percent":100}}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("\"media_category\":\"tweet_video\"", http.Requests.Single(r => r.Uri == InitUrl).Body);
        Assert.Contains("\"media_type\":\"video/mp4\"", http.Requests.Single(r => r.Uri == InitUrl).Body);
        Assert.Equal(2, http.Requests.Count(r => r.Uri == StatusUrl));
        Assert.Single(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task A_large_video_is_sent_in_segments_of_at_most_five_megabytes()
    {
        var size = XPublisher.SegmentSize * 2 + 1000;
        var http = Script(file: new byte[size]);

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.Success, outcome.Error);
        var appends = http.Requests.Where(r => r.Uri == AppendUrl).ToList();
        Assert.Equal(3, appends.Count);
        Assert.All(appends, a => Assert.InRange(a.Length ?? 0, 1, 5L * 1024 * 1024 + 2048));   // segment plus form framing
        for (var i = 0; i < 3; i++)
            Assert.Matches($"name=\"segment_index\"[^\n]*\r\n(?:[^\r\n]+\r\n)*\r\n{i}\r\n", appends[i].Body);
    }

    [Fact]
    public async Task A_lapsed_access_token_is_refreshed_and_the_rotated_credential_saved_before_uploading()
    {
        var http = Script();
        var saved = new List<RenewedCredential>();
        var uploadsWhenSaved = -1;

        var outcome = await Publisher(http).PublishAsync(Poster(Lapsed(), save: r =>
        {
            saved.Add(r);
            uploadsWhenSaved = http.Requests.Count(x => x.Uri == InitUrl);
            return Task.CompletedTask;
        }), default);

        Assert.True(outcome.Success, outcome.Error);
        var renewed = XCredential.Parse(Assert.Single(saved).Credential)!;
        Assert.Equal("acc-2", renewed.AccessToken);
        Assert.Equal("ref-2", renewed.RefreshToken);   // X rotates it; the old one is dead
        Assert.Equal(0, uploadsWhenSaved);              // saved at once, not after the upload
        Assert.Equal(saved[0], outcome.RenewedCredential);

        var refresh = http.Requests.Single(r => r.Uri == TokenUrl);
        Assert.Contains("grant_type=refresh_token", refresh.Body);
        Assert.Contains("refresh_token=ref-1", refresh.Body);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-client:x-secret")), refresh.Authorization);
        Assert.DoesNotContain("x-secret", refresh.Body);
        Assert.Equal("Bearer acc-2", http.Requests.Single(r => r.Uri == InitUrl).Authorization);
    }

    [Fact]
    public async Task A_refresh_x_refuses_asks_for_reconnecting_and_uploads_nothing()
    {
        var http = Script(token: _ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_request","error_description":"Value passed for the token was invalid."}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(Lapsed()), default);

        Assert.False(outcome.Success);
        Assert.True(outcome.IsAuthFailure);
        Assert.False(outcome.IsRetryable);
        Assert.DoesNotContain(http.Requests, r => r.Uri == InitUrl);
    }

    [Fact]
    public async Task X_sign_in_being_down_is_retried_not_a_reconnect()
    {
        var http = Script(token: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var outcome = await Publisher(http).PublishAsync(Poster(Lapsed()), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.IsAuthFailure);
    }

    [Fact]
    public async Task A_server_error_creating_the_post_is_uncertain_never_retried()
    {
        var http = Script(post: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.Success);
        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_dropped_connection_creating_the_post_is_uncertain()
    {
        var http = Script(post: _ => throw new HttpRequestException("reset"));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_success_without_an_id_is_uncertain()
    {
        var http = Script(post: _ => Json(HttpStatusCode.Created, """{"data":{}}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.Success);
        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_rate_limit_on_creating_the_post_is_retried()
    {
        var http = Script(post: _ => Json(HttpStatusCode.TooManyRequests, """{"title":"Too Many Requests","detail":"Too Many Requests","status":429}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_duplicate_post_is_refused_with_what_to_change()
    {
        var http = Script(post: _ => Json(HttpStatusCode.Forbidden,
            """{"detail":"You are not allowed to create a Tweet with duplicate content.","type":"about:blank","title":"Forbidden","status":403}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.IsRetryable);
        Assert.False(outcome.IsAuthFailure);
        Assert.Contains("Change the caption", outcome.Error);
    }

    [Fact]
    public async Task Running_out_of_api_credits_says_so_and_is_not_a_reconnect()
    {
        var http = Script(post: _ => Json(HttpStatusCode.PaymentRequired,
            """{"title":"CreditsDepleted","detail":"Your enrolled account does not have any credits to fulfill this request.","status":402}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.IsAuthFailure);
        Assert.False(outcome.IsRetryable);
        Assert.Contains("credits", outcome.Error);
    }

    [Fact]
    public async Task An_unauthorised_post_asks_for_reconnecting()
    {
        var http = Script(post: _ => Json(HttpStatusCode.Unauthorized, """{"title":"Unauthorized","detail":"Unauthorized","status":401}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsAuthFailure);
    }

    [Fact]
    public async Task An_upload_x_will_not_allow_asks_for_reconnecting_and_posts_nothing()
    {
        // Typically a grant from before media.write was asked for.
        var http = Script(init: _ => Json(HttpStatusCode.Forbidden, """{"title":"Forbidden","detail":"Forbidden","status":403}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsAuthFailure);
        Assert.DoesNotContain(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task An_app_x_has_not_enrolled_is_an_admin_problem_not_a_reconnect()
    {
        // Every X connection would otherwise be marked for reconnecting at once.
        var http = Script(init: _ => Json(HttpStatusCode.Forbidden,
            """{"client_id":"1","detail":"When authenticating requests to the Twitter API v2 endpoints, you must use keys and tokens from a Twitter developer App that is attached to a Project.","registration_url":"https://developer.x.com/","title":"Client Forbidden","required_enrollment":"Appropriate Level of API Access","reason":"client-not-enrolled","type":"https://api.twitter.com/2/problems/client-forbidden"}"""));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.IsAuthFailure);
        Assert.False(outcome.IsRetryable);
        Assert.Contains("Project", outcome.Error);
        Assert.DoesNotContain(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task An_upload_failing_on_xs_side_is_retried_and_posts_nothing()
    {
        var http = Script(init: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
        Assert.DoesNotContain(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task A_video_x_cannot_process_fails_with_xs_reason_and_posts_nothing()
    {
        var http = Script(
            file: new byte[4096],
            finalize: _ => Json(HttpStatusCode.OK, """{"data":{"id":"1880028106020515840","processing_info":{"state":"pending","check_after_secs":1}}}"""),
            status: _ => Json(HttpStatusCode.OK, """{"data":{"processing_info":{"state":"failed","error":{"code":1,"name":"InvalidMedia","message":"Unsupported video format"}}}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.False(outcome.Success);
        Assert.False(outcome.IsRetryable);
        Assert.Contains("Unsupported video format", outcome.Error);
        Assert.DoesNotContain(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task A_video_still_processing_at_the_limit_is_retried_later_not_posted()
    {
        var http = Script(
            file: new byte[4096],
            finalize: _ => Json(HttpStatusCode.OK, """{"data":{"id":"1880028106020515840","processing_info":{"state":"pending","check_after_secs":1}}}"""),
            status: _ => Json(HttpStatusCode.OK, """{"data":{"processing_info":{"state":"in_progress","check_after_secs":1}}}"""));
        var publisher = new XPublisher(new SingleClientFactory(http), Options.Create(Settings), NullLogger<XPublisher>.Instance)
        {
            ProcessingDelay = _ => TimeSpan.FromMilliseconds(10),
            ProcessingLimit = TimeSpan.FromMilliseconds(100),
        };

        var outcome = await publisher.PublishAsync(Video(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
        Assert.DoesNotContain(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task An_image_over_five_megabytes_is_refused_before_uploading()
    {
        var big = new byte[(int)XPublisher.MaxImageBytes + 1];
        Png.AsSpan(0, 8).CopyTo(big);
        var http = Script(file: big);

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.Success);
        Assert.Contains("5 MB", outcome.Error);
        Assert.DoesNotContain(http.Requests, r => r.Uri == InitUrl);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_refused()
    {
        var http = Script(file: Encoding.ASCII.GetBytes("<html>not an image</html>"));

        var outcome = await Publisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.Success);
        Assert.DoesNotContain(http.Requests, r => r.Uri == InitUrl);
    }

    [Fact]
    public async Task A_caption_too_long_for_x_is_refused_without_calling_x()
    {
        var http = Script();

        var outcome = await Publisher(http).PublishAsync(Poster(caption: new string('a', 281)), default);

        Assert.False(outcome.Success);
        Assert.False(outcome.IsRetryable);
        Assert.Contains("too long", outcome.Error);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_post_with_no_caption_sends_the_media_alone()
    {
        var http = Script();

        var outcome = await Publisher(http).PublishAsync(Poster(caption: "  "), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.DoesNotContain("\"text\"", http.Requests.Single(r => r.Uri == PostsUrl).Body);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { 1, 2, 3 }, null)]
    public void The_image_type_is_read_from_the_file_itself(byte[] head, string? expected)
    {
        Assert.Equal(expected, XPublisher.SniffImageType(head));
    }

    [Fact]
    public void The_credential_round_trips_and_never_prints_its_tokens()
    {
        var parsed = XCredential.Parse(Fresh("secret-access", "secret-refresh"))!;

        Assert.Equal("secret-access", parsed.AccessToken);
        Assert.Equal("secret-refresh", parsed.RefreshToken);
        Assert.True(parsed.AccessValidAt(DateTime.UtcNow));
        Assert.False(XCredential.Parse(Lapsed())!.AccessValidAt(DateTime.UtcNow));
        Assert.Null(XCredential.Parse("not json"));
        Assert.Null(XCredential.Parse("[]"));
        Assert.DoesNotContain("secret", parsed.ToString());
    }

    /* ---------------- connecting ---------------- */

    private static XOAuthProvider Provider(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(Settings), NullLogger<XOAuthProvider>.Instance);

    [Fact]
    public void The_pkce_challenge_follows_rfc_7636()
    {
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            XOAuthProvider.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void The_verifier_is_fixed_per_flow_and_cannot_be_derived_without_the_secret()
    {
        var v = XOAuthProvider.Verifier("x-secret", "state-1");

        Assert.Equal(v, XOAuthProvider.Verifier("x-secret", "state-1"));
        Assert.NotEqual(v, XOAuthProvider.Verifier("x-secret", "state-2"));
        Assert.NotEqual(v, XOAuthProvider.Verifier("other-secret", "state-1"));
        Assert.InRange(v.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", v);
    }

    [Fact]
    public void The_consent_asks_for_posting_with_media_and_lasting_access_using_pkce()
    {
        var url = Provider(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
            .BuildAuthorizationUrl("state-1", "https://app/social/callback/x");

        Assert.StartsWith("https://x.com/i/oauth2/authorize?response_type=code&client_id=x-client", url);
        Assert.Contains("scope=" + Uri.EscapeDataString("tweet.read tweet.write users.read media.write offline.access"), url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://app/social/callback/x"), url);
        Assert.Contains("state=state-1", url);
        Assert.Contains("code_challenge=" + XOAuthProvider.Challenge(XOAuthProvider.Verifier("x-secret", "state-1")), url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.DoesNotContain("x-secret", url);
        Assert.DoesNotContain(XOAuthProvider.Verifier("x-secret", "state-1"), url);
    }

    private static ScriptedHandler ConnectScript(string? token = null, Func<HttpResponseMessage>? me = null) =>
        new(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, token ??
                """{"token_type":"bearer","expires_in":7200,"access_token":"acc-1","scope":"tweet.read tweet.write users.read media.write offline.access","refresh_token":"ref-1"}""")
            : me?.Invoke() ?? Json(HttpStatusCode.OK,
                """{"data":{"id":"2244994945","name":"High St Pharmacy","username":"highstpharmacy","profile_image_url":"https://pbs.twimg.com/a.jpg"}}"""));

    [Fact]
    public async Task Connecting_redeems_the_code_with_the_flows_verifier_and_lists_the_account()
    {
        var http = ConnectScript();

        var account = Assert.Single(await Provider(http).ExchangeCodeAsync("code-1", "https://app/social/callback/x", "state-1", default));

        Assert.Equal("2244994945", account.ExternalAccountId);
        Assert.Equal("High St Pharmacy (@highstpharmacy)", account.DisplayName);
        Assert.Equal(SocialPlatform.XAccount, account.Platform);
        Assert.Equal("2244994945", account.AuthorizedByUserId);
        Assert.Equal("https://pbs.twimg.com/a.jpg", account.AvatarUrl);
        Assert.Null(account.RefreshToken);   // a second copy would go stale at the first rotation
        Assert.Null(account.ExpiresAtUtc);
        var credential = XCredential.Parse(account.AccessToken)!;
        Assert.Equal("acc-1", credential.AccessToken);
        Assert.Equal("ref-1", credential.RefreshToken);

        var token = http.Requests.Single(r => r.Uri == TokenUrl);
        Assert.Contains("grant_type=authorization_code", token.Body);
        Assert.Contains("code=code-1", token.Body);
        Assert.Contains("code_verifier=" + XOAuthProvider.Verifier("x-secret", "state-1"), token.Body);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-client:x-secret")), token.Authorization);
        Assert.DoesNotContain("x-secret", token.Body);
        Assert.Equal("Bearer acc-1", http.Requests.Single(r => r.Uri == MeUrl).Authorization);
    }

    [Fact]
    public async Task The_exchange_without_the_state_cannot_be_used()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(ConnectScript()).ExchangeCodeAsync("code", "https://app/cb", default));
    }

    [Fact]
    public async Task Connecting_without_permission_to_upload_media_is_refused_plainly()
    {
        var http = ConnectScript(token: """{"access_token":"a","refresh_token":"r","expires_in":7200,"scope":"tweet.read tweet.write users.read offline.access"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", "s", default));
        Assert.Contains("Permission to post", ex.Message);
    }

    [Fact]
    public async Task Connecting_without_lasting_access_is_refused()
    {
        var http = ConnectScript(token: """{"access_token":"a","expires_in":7200,"scope":"tweet.read tweet.write users.read media.write"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", "s", default));
        Assert.Contains("refresh token", ex.Message);
    }

    [Fact]
    public async Task Wrong_app_credentials_are_named()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.Unauthorized,
            """{"error":"unauthorized_client","error_description":"Missing valid authorization header"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", "s", default));
        Assert.Contains("Social__X__ClientId", ex.Message);
    }

    [Fact]
    public async Task Xs_own_refusal_is_passed_on()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_request","error_description":"Value passed for the authorization code was invalid."}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", "s", default));
        Assert.Contains("authorization code was invalid", ex.Message);
    }

    [Fact]
    public async Task Disconnecting_revokes_the_refresh_token_which_ends_the_grant()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"revoked":true}"""));

        await Provider(http).RevokeAsync(Fresh("acc-1", "ref-9"), default);

        var revoke = Assert.Single(http.Requests);
        Assert.Equal(RevokeUrl, revoke.Uri);
        Assert.Contains("token=ref-9", revoke.Body);
        Assert.Contains("token_type_hint=refresh_token", revoke.Body);
        Assert.StartsWith("Basic ", revoke.Authorization);
    }

    /* ---------------- the daily check ---------------- */

    private static XProbe Probe(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(Settings), NullLogger<XProbe>.Instance);

    [Fact]
    public async Task The_daily_check_refreshes_and_hands_back_the_rotated_credential()
    {
        var http = Script();

        var (result, _, renewed) = await Probe(http).ProbeAndRenewAsync("x-user-1", Lapsed(), default);

        Assert.Equal(ProbeResult.Healthy, result);
        Assert.Equal("ref-2", XCredential.Parse(renewed!.Credential)!.RefreshToken);
        // No billed read: the accepted refresh is the proof.
        Assert.DoesNotContain(http.Requests, r => r.Uri == MeUrl);
    }

    [Fact]
    public async Task The_daily_check_marks_a_withdrawn_grant_for_reconnecting()
    {
        var http = Script(token: _ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_request","error_description":"Value passed for the token was invalid."}"""));

        var (result, _, renewed) = await Probe(http).ProbeAndRenewAsync("x-user-1", Lapsed(), default);

        Assert.Equal(ProbeResult.Unauthorized, result);
        Assert.Null(renewed);
    }

    [Fact]
    public async Task The_daily_check_shrugs_off_x_being_down()
    {
        var http = Script(token: _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var (result, _, _) = await Probe(http).ProbeAndRenewAsync("x-user-1", Lapsed(), default);

        Assert.Equal(ProbeResult.Inconclusive, result);
    }

    [Fact]
    public async Task The_daily_check_with_a_fresh_token_calls_nothing()
    {
        var http = Script();

        var (result, _, renewed) = await Probe(http).ProbeAndRenewAsync("x-user-1", Fresh(), default);

        Assert.Equal(ProbeResult.Healthy, result);
        Assert.Null(renewed);
        Assert.Empty(http.Requests);
    }

    /* ---------------- plumbing ---------------- */

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Uri, string Body, string? Authorization, long? Length);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentLength));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
