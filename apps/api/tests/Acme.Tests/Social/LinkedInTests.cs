using System.Net;
using System.Text;
using Acme.Application.Social;
using Acme.Domain.Social;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// LinkedIn: connecting a profile or Page, posting a poster or video, and how
/// each failure is classified — "try again", "stop", "reconnect" or "it may
/// already be up".
/// </summary>
public class LinkedInTests
{
    private static readonly LinkedInOptions Profiles = new() { ClientId = "li-client", ClientSecret = "li-secret" };
    private static readonly LinkedInOptions WithPages = new() { ClientId = "li-client", ClientSecret = "li-secret", PagesEnabled = true };

    private const string TokenUrl = "https://www.linkedin.com/oauth/v2/accessToken";
    private const string UserInfoUrl = "https://api.linkedin.com/v2/userinfo";
    private const string ImagesInit = "https://api.linkedin.com/rest/images?action=initializeUpload";
    private const string VideosInit = "https://api.linkedin.com/rest/videos?action=initializeUpload";
    private const string VideosFinalize = "https://api.linkedin.com/rest/videos?action=finalizeUpload";
    private const string PostsUrl = "https://api.linkedin.com/rest/posts";
    private const string AclUrl = "https://api.linkedin.com/rest/organizationAcls";
    private const string ImageUpload = "https://upload.test/image-1";
    private const string PosterUrl = "https://blob.test/posters/p1.png";
    private const string VideoUrl = "https://blob.test/videos/v1.mp4";

    private static PublishRequest Poster(string caption = "Flu jabs now on (walk in)") => new(
        SocialPlatform.LinkedInProfile, "abc123", "li-access-1", caption, PosterUrl, IsVideo: false);

    private static PublishRequest Video() => new(
        SocialPlatform.LinkedInProfile, "abc123", "li-access-1", "Blood pressure checks", VideoUrl, IsVideo: true);

    private static LinkedInProfilePublisher ProfilePublisher(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(Profiles), NullLogger<LinkedInProfilePublisher>.Instance)
        {
            ProcessingWaits = new[] { TimeSpan.Zero, TimeSpan.Zero },
        };

    private static LinkedInPagePublisher PagePublisher(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(WithPages), NullLogger<LinkedInPagePublisher>.Instance)
        {
            ProcessingWaits = new[] { TimeSpan.Zero, TimeSpan.Zero },
        };

    private static HttpResponseMessage Created(string id = "urn:li:share:7001")
    {
        var r = new HttpResponseMessage(HttpStatusCode.Created);
        r.Headers.TryAddWithoutValidation("x-restli-id", id);
        return r;
    }

    /// <summary>The happy path for a poster, with any one step swapped out.</summary>
    private static ScriptedHandler PosterScript(
        Func<HttpRequestMessage, HttpResponseMessage>? init = null,
        Func<HttpRequestMessage, HttpResponseMessage>? post = null) =>
        new(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == PosterUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[512]) };
            if (url == ImagesInit)
                return (init ?? (_ => Json(HttpStatusCode.OK,
                    $$$"""{"value":{"uploadUrl":"{{{ImageUpload}}}","image":"urn:li:image:IMG1"}}""")))(req);
            if (url == ImageUpload) return new HttpResponseMessage(HttpStatusCode.Created);
            if (url == PostsUrl) return (post ?? (_ => Created()))(req);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    /* ---------------- posting a poster ---------------- */

    [Fact]
    public async Task A_poster_is_uploaded_then_posted_and_linked()
    {
        var http = PosterScript();

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("urn:li:share:7001", outcome.ExternalPostId);
        Assert.Equal("https://www.linkedin.com/feed/update/urn:li:share:7001/", outcome.Permalink);

        var init = http.Requests.Single(r => r.Uri == ImagesInit);
        Assert.Contains("\"owner\":\"urn:li:person:abc123\"", init.Body);
        Assert.Equal("Bearer li-access-1", init.Authorization);
        Assert.False(string.IsNullOrEmpty(init.Version));   // LinkedIn refuses unversioned REST calls

        var post = http.Requests.Single(r => r.Uri == PostsUrl);
        Assert.Contains("\"author\":\"urn:li:person:abc123\"", post.Body);
        Assert.Contains("\"id\":\"urn:li:image:IMG1\"", post.Body);
        Assert.Contains("\"visibility\":\"PUBLIC\"", post.Body);
        Assert.Contains("\"lifecycleState\":\"PUBLISHED\"", post.Body);
        // "(walk in)" escaped, or LinkedIn reads it as markup.
        Assert.Contains(@"\\(walk in\\)", post.Body);
    }

    [Fact]
    public async Task A_page_posts_as_the_organisation()
    {
        var http = PosterScript();

        var outcome = await PagePublisher(http).PublishAsync(
            Poster() with { Platform = SocialPlatform.LinkedInPage, ExternalAccountId = "2414183" }, default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains("\"author\":\"urn:li:organization:2414183\"", http.Requests.Single(r => r.Uri == PostsUrl).Body);
        Assert.Contains("\"owner\":\"urn:li:organization:2414183\"", http.Requests.Single(r => r.Uri == ImagesInit).Body);
    }

    [Fact]
    public async Task A_refused_sign_in_asks_for_a_reconnect_and_posts_nothing()
    {
        var http = PosterScript(init: _ => Json(HttpStatusCode.Unauthorized, """{"status":401,"code":"EXPIRED_ACCESS_TOKEN","message":"expired"}"""));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsAuthFailure);
        Assert.Contains("Reconnect", outcome.Error);
        Assert.DoesNotContain(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task Losing_the_page_role_asks_for_a_reconnect()
    {
        var http = PosterScript(post: _ => Json(HttpStatusCode.Forbidden, """{"status":403,"code":"ACCESS_DENIED","message":"Not enough permissions"}"""));

        var outcome = await PagePublisher(http).PublishAsync(
            Poster() with { Platform = SocialPlatform.LinkedInPage, ExternalAccountId = "2414183" }, default);

        Assert.True(outcome.IsAuthFailure);
        Assert.Contains("admin", outcome.Error);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_server_fault_before_the_post_is_safe_to_retry()
    {
        var http = PosterScript(init: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
        Assert.DoesNotContain(http.Requests, r => r.Uri == PostsUrl);
    }

    [Fact]
    public async Task Being_rate_limited_is_retried()
    {
        var http = PosterScript(post: _ => Json(HttpStatusCode.TooManyRequests, """{"status":429,"message":"slow down"}"""));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_timeout_while_creating_the_post_may_have_posted_it()
    {
        var http = PosterScript(post: _ => throw new TaskCanceledException("HttpClient.Timeout"));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.OutcomeUnknown);
        Assert.False(outcome.IsRetryable);
    }

    [Fact]
    public async Task A_server_fault_while_creating_the_post_may_have_posted_it()
    {
        var http = PosterScript(post: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_created_post_without_an_id_is_uncertain_not_published()
    {
        var http = PosterScript(post: _ => new HttpResponseMessage(HttpStatusCode.Created));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.Success);
        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_rejected_post_says_why_and_is_not_retried()
    {
        var http = PosterScript(post: _ => Json(HttpStatusCode.UnprocessableEntity,
            """{"status":422,"code":"UNPROCESSABLE_ENTITY","message":"Content is a duplicate"}"""));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.False(outcome.Success);
        Assert.False(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
        Assert.Contains("duplicate", outcome.Error);
    }

    [Fact]
    public async Task An_unreadable_poster_file_is_retried_and_nothing_is_sent_to_linkedin()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == PosterUrl
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : throw new InvalidOperationException("LinkedIn must not be called"));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster(), default);

        Assert.True(outcome.IsRetryable);
    }

    [Fact]
    public void The_whole_publish_fits_inside_the_workers_lease() =>
        Assert.True(LinkedInPublisherBase.Budget < TimeSpan.FromSeconds(new PublishWorkerOptions().LeaseSeconds));

    [Theory]
    [InlineData("LinkedIn asked us to slow down.", "LinkedIn asked us to slow down. Trying again automatically.")]
    [InlineData("Could not reach YouTube. Trying again automatically.", "Could not reach YouTube. Trying again automatically.")]
    [InlineData("Could not reach Instagram. It will be tried again.", "Could not reach Instagram. It will be tried again.")]
    [InlineData(null, "Temporary problem. Trying again automatically.")]
    public void The_retry_note_says_it_will_try_again_exactly_once(string? error, string expected) =>
        Assert.Equal(expected, PublishService.RetryNote(error));

    /* ---------------- posting a video ---------------- */

    private static ScriptedHandler VideoScript(Func<int, HttpRequestMessage, HttpResponseMessage>? post = null)
    {
        var posts = 0;
        return new ScriptedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == VideoUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
            if (url == VideosInit)
                return Json(HttpStatusCode.OK, """
                    {"value":{"video":"urn:li:video:V1","uploadToken":"tok-1","uploadInstructions":[
                      {"uploadUrl":"https://upload.test/part-0","firstByte":0,"lastByte":5},
                      {"uploadUrl":"https://upload.test/part-1","firstByte":6,"lastByte":9}]}}
                    """);
            if (url.StartsWith("https://upload.test/part-", StringComparison.Ordinal))
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK);
                // Unquoted, as LinkedIn sends it.
                r.Headers.TryAddWithoutValidation("ETag", "/ambry/etag-" + url[^1]);
                return r;
            }
            if (url == VideosFinalize) return new HttpResponseMessage(HttpStatusCode.OK);
            if (url == PostsUrl) return (post ?? ((_, _) => Created("urn:li:ugcPost:900")))(++posts, req);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task A_video_is_sent_in_the_parts_linkedin_asks_for_and_finalised_in_order()
    {
        var http = VideoScript();

        var outcome = await ProfilePublisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("urn:li:ugcPost:900", outcome.ExternalPostId);

        Assert.Contains("\"fileSizeBytes\":10", http.Requests.Single(r => r.Uri == VideosInit).Body);
        Assert.Equal(6, http.Requests.Single(r => r.Uri.EndsWith("part-0", StringComparison.Ordinal)).Length);
        Assert.Equal(4, http.Requests.Single(r => r.Uri.EndsWith("part-1", StringComparison.Ordinal)).Length);

        var finalize = http.Requests.Single(r => r.Uri == VideosFinalize).Body;
        Assert.Contains("\"uploadToken\":\"tok-1\"", finalize);
        Assert.Contains("[\"/ambry/etag-0\",\"/ambry/etag-1\"]", finalize);

        Assert.Contains("\"id\":\"urn:li:video:V1\"", http.Requests.Single(r => r.Uri == PostsUrl).Body);
    }

    [Fact]
    public async Task A_video_still_processing_is_asked_again_and_then_posts()
    {
        var http = VideoScript((n, _) => n == 1
            ? Json(HttpStatusCode.BadRequest, """{"status":400,"code":"MEDIA_ASSET_WAITING_UPLOAD","message":"Media asset is waiting upload"}""")
            : Created("urn:li:ugcPost:901"));

        var outcome = await ProfilePublisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(2, http.Requests.Count(r => r.Uri == PostsUrl));
    }

    [Fact]
    public async Task A_video_that_never_finishes_processing_is_retried_later_not_uncertain()
    {
        var http = VideoScript((_, _) => Json(HttpStatusCode.BadRequest,
            """{"status":400,"message":"Media asset is PROCESSING"}"""));

        var outcome = await ProfilePublisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_video_linkedin_failed_to_process_is_not_waited_for()
    {
        var http = VideoScript((_, _) => Json(HttpStatusCode.BadRequest,
            """{"status":400,"code":"MEDIA_ASSET_PROCESSING_FAILED","message":"Media asset failed processing"}"""));

        var outcome = await ProfilePublisher(http).PublishAsync(Video(), default);

        Assert.False(outcome.IsRetryable);
        Assert.Single(http.Requests, r => r.Uri == PostsUrl);
    }

    /* ---------------- refresh-token credentials ---------------- */

    [Fact]
    public async Task A_refresh_token_credential_is_exchanged_first_and_never_put_in_a_url()
    {
        var inner = PosterScript();
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"fresh-access","expires_in":5183999}""")
            : inner.Respond(req));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster() with { AccessToken = "li-refresh:r-1" }, default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Contains(http.Requests, r => r.Uri == TokenUrl && r.Body.Contains("refresh_token=r-1"));
        Assert.DoesNotContain(http.Requests, r => r.Uri.Contains("r-1"));
        Assert.Equal("Bearer fresh-access", http.Requests.Single(r => r.Uri == PostsUrl).Authorization);
    }

    [Fact]
    public async Task An_expired_refresh_token_asks_for_a_reconnect_and_calls_nothing_else()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_request","error_description":"The provided authorization grant or refresh token is invalid, expired or revoked"}""")
            : throw new InvalidOperationException("nothing else may be called"));

        var outcome = await ProfilePublisher(http).PublishAsync(Poster() with { AccessToken = "li-refresh:r-1" }, default);

        Assert.True(outcome.IsAuthFailure);
        Assert.Single(http.Requests);
    }

    /* ---------------- the text ---------------- */

    [Theory]
    [InlineData("Open (till 9pm)", @"Open \(till 9pm\)")]
    [InlineData("Ask @ the counter", @"Ask \@ the counter")]
    [InlineData("#FluJab season", "#FluJab season")]
    [InlineData("No. # here", @"No. \# here")]
    [InlineData("50% off_today*", @"50% off\_today\*")]
    [InlineData("a\\b", @"a\\b")]
    [InlineData("Book at shop.com/#times", @"Book at shop.com/\#times")]
    [InlineData("Jabs#now", @"Jabs\#now")]
    public void Reserved_characters_are_escaped_and_hashtags_kept(string caption, string expected) =>
        Assert.Equal(expected, LinkedInText.Commentary(caption));

    [Fact]
    public void A_long_caption_is_cut_to_linkedins_limit_without_a_dangling_escape()
    {
        var text = LinkedInText.Commentary(new string('a', 2999) + "(((((");
        Assert.True(text.Length <= LinkedInText.Max);
        Assert.False(text.EndsWith('\\'));
    }

    [Fact]
    public void An_emoji_at_the_limit_is_kept_whole_or_dropped_never_split()
    {
        var text = LinkedInText.Commentary(new string('a', LinkedInText.Max - 1) + "\U0001F489");   // 💉
        Assert.Equal(LinkedInText.Max - 1, text.Length);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }

    /* ---------------- the platform ---------------- */

    [Fact]
    public void LinkedIn_takes_posters_and_videos()
    {
        foreach (var p in new[] { SocialPlatform.LinkedInProfile, SocialPlatform.LinkedInPage })
        {
            Assert.True(p.IsPublishTarget());
            Assert.True(p.SupportsImages());
            Assert.True(p.SupportsVideo());
            Assert.True(p.IsLinkedIn());
        }
        Assert.Equal("LinkedIn Page", SocialPlatform.LinkedInPage.DisplayName());
        Assert.False(new LinkedInOptions { ClientId = "a", ClientSecret = "b", ProfilesEnabled = false }.IsConfigured);
    }

    /* ---------------- connecting ---------------- */

    private static LinkedInOAuthProvider Provider(ScriptedHandler http, LinkedInOptions? options = null) =>
        new(new SingleClientFactory(http), Options.Create(options ?? Profiles), NullLogger<LinkedInOAuthProvider>.Instance);

    [Fact]
    public void The_consent_asks_for_profile_posting_only_until_pages_are_switched_on()
    {
        var plain = Provider(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
            .BuildAuthorizationUrl("state-1", "https://app/social/callback/linkedin");
        Assert.StartsWith("https://www.linkedin.com/oauth/v2/authorization?", plain);
        Assert.Contains("scope=openid%20profile%20w_member_social", plain);
        Assert.DoesNotContain("organization", plain);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://app/social/callback/linkedin"), plain);

        var pages = Provider(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)), WithPages)
            .BuildAuthorizationUrl("state-1", "https://app/social/callback/linkedin");
        Assert.Contains("w_organization_social", pages);
        Assert.Contains("rw_organization_admin", pages);
    }

    [Fact]
    public async Task Connecting_lists_the_profile_with_its_sixty_day_expiry()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"li-a","expires_in":5184000,"scope":"openid,profile,w_member_social"}""")
            : Json(HttpStatusCode.OK, """{"sub":"abc123","name":"Priya Shah","picture":"https://media.licdn/p.jpg"}"""));

        var accounts = await Provider(http).ExchangeCodeAsync("code", "https://app/social/callback/linkedin", default);

        var profile = Assert.Single(accounts);
        Assert.Equal(SocialPlatform.LinkedInProfile, profile.Platform);
        Assert.Equal("abc123", profile.ExternalAccountId);
        Assert.Equal("Priya Shah", profile.DisplayName);
        Assert.Equal("li-a", profile.AccessToken);
        Assert.Equal("li-a", profile.RefreshToken);   // the sign-in's fingerprint, not a second secret
        Assert.Contains("w_member_social", profile.Scopes);
        Assert.InRange(profile.ExpiresAtUtc!.Value, DateTime.UtcNow.AddDays(59), DateTime.UtcNow.AddDays(61));

        // The code is exchanged in the body, with the secret, never in a URL.
        Assert.DoesNotContain(http.Requests, r => r.Uri.Contains("li-secret") || r.Uri.Contains("code="));
    }

    [Fact]
    public async Task A_refresh_token_becomes_the_credential_and_its_expiry_the_connections()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"li-a","expires_in":5184000,"refresh_token":"li-r","refresh_token_expires_in":31536000,"scope":"openid,profile,w_member_social"}""")
            : Json(HttpStatusCode.OK, """{"sub":"abc123","name":"Priya Shah"}"""));

        var profile = Assert.Single(await Provider(http).ExchangeCodeAsync("code", "https://app/cb", default));

        Assert.Equal("li-refresh:li-r", profile.AccessToken);
        Assert.InRange(profile.ExpiresAtUtc!.Value, DateTime.UtcNow.AddDays(364), DateTime.UtcNow.AddDays(366));
    }

    [Fact]
    public async Task With_pages_on_the_pages_the_person_can_post_as_are_listed_by_name()
    {
        var http = new ScriptedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == TokenUrl)
                return Json(HttpStatusCode.OK, """{"access_token":"li-a","expires_in":5184000,"scope":"openid,profile,w_member_social,w_organization_social,rw_organization_admin"}""");
            if (url == UserInfoUrl) return Json(HttpStatusCode.OK, """{"sub":"abc123","name":"Priya Shah"}""");
            if (url.StartsWith(AclUrl, StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, """
                    {"elements":[
                      {"role":"ADMINISTRATOR","state":"APPROVED","organizationTarget":"urn:li:organization:111"},
                      {"role":"CONTENT_ADMINISTRATOR","state":"APPROVED","organization":"urn:li:organization:222"},
                      {"role":"ANALYST","state":"APPROVED","organizationTarget":"urn:li:organization:333"}]}
                    """);
            if (url.EndsWith("/organizations/111", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"localizedName":"High St Pharmacy"}""");
            if (url.EndsWith("/organizations/222", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, """{"localizedName":"Riverside Chemist"}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var accounts = await Provider(http, WithPages).ExchangeCodeAsync("code", "https://app/cb", default);

        Assert.Equal(3, accounts.Count);
        Assert.Contains(accounts, a => a.Platform == SocialPlatform.LinkedInProfile);
        var pages = accounts.Where(a => a.Platform == SocialPlatform.LinkedInPage).ToList();
        Assert.Equal(new[] { "111", "222" }, pages.Select(p => p.ExternalAccountId));   // an analyst cannot post
        Assert.Equal(new[] { "High St Pharmacy", "Riverside Chemist" }, pages.Select(p => p.DisplayName));
        Assert.All(pages, p => Assert.Equal("li-a", p.AccessToken));
    }

    [Fact]
    public async Task Connecting_without_permission_to_post_is_refused_plainly()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"li-a","scope":"openid,profile"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", default));
        Assert.Contains("Permission to post", ex.Message);
    }

    [Fact]
    public async Task LinkedIns_own_refusal_is_passed_on()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_redirect_uri","error_description":"Unable to retrieve access token: appid/redirect uri/code verifier does not match authorization code."}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", default));
        Assert.Contains("redirect uri", ex.Message);
    }

    /* ---------------- the daily check ---------------- */

    [Fact]
    public async Task The_profile_check_reads_the_profile_and_flags_a_refused_sign_in()
    {
        var healthy = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"sub":"abc123"}"""));
        var refused = new ScriptedHandler(_ => Json(HttpStatusCode.Unauthorized, """{"status":401}"""));

        var probe = (ScriptedHandler h) => new LinkedInProfileProbe(new SingleClientFactory(h), Options.Create(Profiles),
            NullLogger<LinkedInProfileProbe>.Instance);

        Assert.Equal(ProbeResult.Healthy, (await probe(healthy).ProbeAsync("abc123", "li-a", default)).Result);
        Assert.Equal(ProbeResult.Unauthorized, (await probe(refused).ProbeAsync("abc123", "li-a", default)).Result);
    }

    [Fact]
    public async Task The_page_check_flags_a_page_the_person_no_longer_manages()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK,
            """{"elements":[{"role":"ADMINISTRATOR","state":"APPROVED","organizationTarget":"urn:li:organization:111"}]}"""));
        var probe = new LinkedInPageProbe(new SingleClientFactory(http), Options.Create(WithPages), NullLogger<LinkedInPageProbe>.Instance);

        Assert.Equal(ProbeResult.Healthy, (await probe.ProbeAsync("111", "li-a", default)).Result);
        var gone = await probe.ProbeAsync("222", "li-a", default);
        Assert.Equal(ProbeResult.Unauthorized, gone.Result);
        Assert.Contains("no longer an admin", gone.Detail);
    }

    [Fact]
    public async Task An_outage_during_the_check_changes_nothing()
    {
        var http = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var probe = new LinkedInPageProbe(new SingleClientFactory(http), Options.Create(WithPages), NullLogger<LinkedInPageProbe>.Instance);

        Assert.Equal(ProbeResult.Inconclusive, (await probe.ProbeAsync("111", "li-a", default)).Result);
    }

    /* ---------------- fakes ---------------- */

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Uri, string Body, string? Authorization, string? Version, long? Length);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request) => respond(request);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // File bytes are not text; their length is what matters.
            var isFile = request.Method == HttpMethod.Put;
            var body = request.Content is null || isFile ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var version = request.Headers.TryGetValues("LinkedIn-Version", out var v) ? v.FirstOrDefault() : null;
            Requests.Add(new Sent(request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString(),
                version, request.Content?.Headers.ContentLength));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
