using System.Net;
using System.Text;
using Acme.Application.Common;
using Acme.Application.Social;
using Acme.Domain.Common;
using Acme.Domain.Social;
using Acme.Infrastructure.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acme.Tests.Social;

/// <summary>
/// TikTok: connecting an account, the rotating credential, the share
/// settings TikTok's guidelines require, and how each failure of a Direct Post
/// is classified — "try again", "stop", "reconnect" or "it may already be up".
/// </summary>
public class TikTokTests
{
    private static readonly TikTokOptions Settings = new() { ClientKey = "tt-key", ClientSecret = "tt-secret" };

    private const string TokenUrl = "https://open.tiktokapis.com/v2/oauth/token/";
    private const string CreatorUrl = "https://open.tiktokapis.com/v2/post/publish/creator_info/query/";
    private const string InitUrl = "https://open.tiktokapis.com/v2/post/publish/video/init/";
    private const string StatusUrl = "https://open.tiktokapis.com/v2/post/publish/status/fetch/";
    private const string UserInfoUrl = "https://open.tiktokapis.com/v2/user/info/";
    private const string UploadUrl = "https://open-upload.test/video/?upload_id=1";
    private const string VideoUrl = "https://blob.test/videos/v1.mp4";

    private static readonly TikTokPostOptions Everyone = new("PUBLIC_TO_EVERYONE", AllowComments: true);

    /// <summary>A credential whose access token is good for another day.</summary>
    private static string Fresh(string access = "act-1", string refresh = "rft-1") =>
        new TikTokCredential(access, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(), refresh).Serialize();

    /// <summary>A credential whose access token has lapsed.</summary>
    private static string Lapsed(string refresh = "rft-1") =>
        new TikTokCredential("act-old", DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), refresh).Serialize();

    private static PublishRequest Video(string? credential = null, TikTokPostOptions? options = null) => new(
        SocialPlatform.TikTokAccount, "open-1", credential ?? Fresh(), "Flu jabs now on #FluJab", VideoUrl,
        IsVideo: true, TikTokOptions: options ?? Everyone);

    private static TikTokPublisher Publisher(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(Settings), NullLogger<TikTokPublisher>.Instance)
        {
            StatusPollInterval = TimeSpan.Zero,
            StatusPollLimit = TimeSpan.FromSeconds(2),
        };

    private const string Creator = """
        {"data":{"creator_username":"highstpharmacy","creator_nickname":"High St Pharmacy",
          "privacy_level_options":["PUBLIC_TO_EVERYONE","MUTUAL_FOLLOW_FRIENDS","SELF_ONLY"],
          "comment_disabled":false,"duet_disabled":false,"stitch_disabled":true,"max_video_post_duration_sec":600},
         "error":{"code":"ok","message":""}}
        """;

    /// <summary>The happy path, with any one step swapped out.</summary>
    private static ScriptedHandler Script(
        Func<HttpRequestMessage, HttpResponseMessage>? token = null,
        Func<HttpRequestMessage, HttpResponseMessage>? creator = null,
        Func<HttpRequestMessage, HttpResponseMessage>? init = null,
        Func<HttpRequestMessage, HttpResponseMessage>? upload = null,
        Func<int, HttpResponseMessage>? status = null)
    {
        var polls = 0;
        return new ScriptedHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == TokenUrl)
                return (token ?? (_ => Json(HttpStatusCode.OK,
                    """{"access_token":"act-2","expires_in":86400,"refresh_token":"rft-2","refresh_expires_in":31536000,"open_id":"open-1"}""")))(req);
            if (url == CreatorUrl) return (creator ?? (_ => Json(HttpStatusCode.OK, Creator)))(req);
            if (url == VideoUrl) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
            if (url == InitUrl)
                return (init ?? (_ => Json(HttpStatusCode.OK,
                    $$$"""{"data":{"publish_id":"v_pub_1","upload_url":"{{{UploadUrl}}}"},"error":{"code":"ok"}}""")))(req);
            if (url == UploadUrl) return (upload ?? (_ => new HttpResponseMessage(HttpStatusCode.Created)))(req);
            if (url == StatusUrl)
                return (status ?? (n => n == 1
                    ? Json(HttpStatusCode.OK, """{"data":{"status":"PROCESSING_UPLOAD"},"error":{"code":"ok"}}""")
                    : Json(HttpStatusCode.OK, """{"data":{"status":"PUBLISH_COMPLETE","publicaly_available_post_id":[7311234567890]},"error":{"code":"ok"}}""")))(++polls);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
    }

    /* ---------------- posting ---------------- */

    [Fact]
    public async Task A_video_is_posted_with_the_chosen_settings_and_linked()
    {
        var http = Script();

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("7311234567890", outcome.ExternalPostId);
        Assert.Equal("https://www.tiktok.com/@highstpharmacy/video/7311234567890", outcome.Permalink);
        Assert.Null(outcome.RenewedCredential);   // the access token was still good

        var init = http.Requests.Single(r => r.Uri == InitUrl).Body;
        Assert.Contains("\"privacy_level\":\"PUBLIC_TO_EVERYONE\"", init);
        Assert.Contains("\"disable_comment\":false", init);
        Assert.Contains("\"disable_duet\":true", init);        // not allowed by the pharmacy
        Assert.Contains("\"disable_stitch\":true", init);      // switched off by the creator
        Assert.Contains("\"source\":\"FILE_UPLOAD\"", init);
        Assert.Contains("\"video_size\":4096", init);
        Assert.Contains("\"total_chunk_count\":1", init);
        Assert.Contains("\"title\":\"Flu jabs now on #FluJab\"", init);
        Assert.Equal("Bearer act-1", http.Requests.Single(r => r.Uri == InitUrl).Authorization);

        var put = http.Requests.Single(r => r.Uri == UploadUrl);
        Assert.Equal("bytes 0-4095/4096", put.ContentRange);
    }

    [Fact]
    public async Task A_creator_setting_switched_off_in_tiktok_wins_over_the_choice()
    {
        var http = Script();

        await Publisher(http).PublishAsync(Video(options: Everyone with { AllowStitch = true }), default);

        Assert.Contains("\"disable_stitch\":true", http.Requests.Single(r => r.Uri == InitUrl).Body);
    }

    [Fact]
    public async Task A_lapsed_access_token_is_refreshed_and_the_rotated_credential_handed_back()
    {
        var http = Script();

        var outcome = await Publisher(http).PublishAsync(Video(Lapsed()), default);

        Assert.True(outcome.Success, outcome.Error);
        var renewed = TikTokCredential.Parse(outcome.RenewedCredential?.Credential);
        Assert.NotNull(renewed);
        Assert.Equal("act-2", renewed!.AccessToken);
        Assert.Equal("rft-2", renewed.RefreshToken);   // TikTok rotated it; the old one may be dead
        Assert.InRange(outcome.RenewedCredential!.ExpiresAtUtc!.Value, DateTime.UtcNow.AddDays(364), DateTime.UtcNow.AddDays(366));
        Assert.Contains(http.Requests, r => r.Uri == TokenUrl && r.Body.Contains("refresh_token=rft-1"));
        Assert.Equal("Bearer act-2", http.Requests.Single(r => r.Uri == InitUrl).Authorization);
    }

    [Fact]
    public async Task A_rotated_credential_is_saved_before_anything_is_uploaded()
    {
        // Another worker posting to the same account during a long upload
        // would otherwise refresh with a refresh token TikTok has retired.
        var http = Script();
        RenewedCredential? saved = null;
        var requestsWhenSaved = -1;

        await Publisher(http).PublishAsync(Video(Lapsed()) with
        {
            SaveRenewal = renewed =>
            {
                saved = renewed;
                requestsWhenSaved = http.Requests.Count;
                return Task.CompletedTask;
            },
        }, default);

        Assert.Equal("rft-2", TikTokCredential.Parse(saved?.Credential)!.RefreshToken);
        Assert.Equal(1, requestsWhenSaved);   // only the refresh itself had been sent
    }

    [Fact]
    public async Task A_rotated_credential_is_handed_back_even_when_the_post_then_fails()
    {
        var http = Script(init: _ => Json(HttpStatusCode.OK,
            """{"error":{"code":"spam_risk_too_many_posts","message":"cap"}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(Lapsed()), default);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.RenewedCredential);
    }

    [Fact]
    public async Task An_expired_refresh_token_asks_for_a_reconnect_and_calls_nothing_else()
    {
        var http = Script(token: _ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Refresh token is invalid or expired."}"""));

        var outcome = await Publisher(http).PublishAsync(Video(Lapsed()), default);

        Assert.True(outcome.IsAuthFailure);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task An_audience_tiktok_no_longer_offers_is_refused_before_anything_is_sent()
    {
        var http = Script();

        var outcome = await Publisher(http).PublishAsync(Video(options: new TikTokPostOptions("FOLLOWER_OF_CREATOR")), default);

        Assert.False(outcome.Success);
        Assert.False(outcome.IsRetryable);
        Assert.Contains("audience", outcome.Error);
        Assert.DoesNotContain(http.Requests, r => r.Uri == InitUrl);
    }

    [Theory]
    [InlineData("spam_risk_too_many_posts", "daily posting limit")]
    [InlineData("reached_active_user_cap", "allowance")]
    [InlineData("unaudited_client_can_only_post_to_private_accounts", "private")]
    [InlineData("spam_risk_user_banned_from_posting", "stopped this account")]
    public async Task TikToks_limits_stop_rather_than_retry(string code, string said)
    {
        // TikTok answers these with HTTP 200 and an error code.
        var http = Script(creator: _ => Json(HttpStatusCode.OK, $$$"""{"error":{"code":"{{{code}}}","message":"x"}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.False(outcome.Success);
        Assert.False(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
        Assert.Contains(said, outcome.Error);
        Assert.DoesNotContain(http.Requests, r => r.Uri == InitUrl);
    }

    [Fact]
    public async Task A_refused_token_asks_for_a_reconnect()
    {
        var http = Script(init: _ => Json(HttpStatusCode.Unauthorized,
            """{"error":{"code":"access_token_invalid","message":"expired"}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.IsAuthFailure);
        Assert.DoesNotContain(http.Requests, r => r.Uri == UploadUrl);
    }

    [Fact]
    public async Task Being_rate_limited_is_retried()
    {
        var http = Script(init: _ => Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limit_exceeded"}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
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
    public async Task A_timeout_sending_the_last_chunk_may_have_posted_it()
    {
        var http = Script(upload: _ => throw new TaskCanceledException("HttpClient.Timeout"));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.OutcomeUnknown);
        Assert.False(outcome.IsRetryable);
    }

    [Fact]
    public async Task A_server_fault_on_the_last_chunk_may_have_posted_it()
    {
        var http = Script(upload: _ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task TikTok_failing_the_post_says_why_and_is_not_retried()
    {
        var http = Script(status: _ => Json(HttpStatusCode.OK,
            """{"data":{"status":"FAILED","fail_reason":"video_pull_failed"},"error":{"code":"ok"}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.False(outcome.Success);
        Assert.False(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
        Assert.Contains("video_pull_failed", outcome.Error);
    }

    [Fact]
    public async Task A_post_still_processing_when_time_runs_out_is_uncertain_not_retried()
    {
        var http = Script(status: _ => Json(HttpStatusCode.OK, """{"data":{"status":"PROCESSING_DOWNLOAD"},"error":{"code":"ok"}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(), default);

        Assert.True(outcome.OutcomeUnknown);
        Assert.False(outcome.IsRetryable);
        Assert.Contains("PROCESSING_DOWNLOAD", outcome.Error);
    }

    [Fact]
    public async Task A_private_post_with_no_public_id_is_published_under_its_publish_id()
    {
        var http = Script(status: _ => Json(HttpStatusCode.OK, """{"data":{"status":"PUBLISH_COMPLETE"},"error":{"code":"ok"}}"""));

        var outcome = await Publisher(http).PublishAsync(Video(options: new TikTokPostOptions("SELF_ONLY")), default);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal("v_pub_1", outcome.ExternalPostId);
        Assert.Null(outcome.Permalink);
    }

    [Fact]
    public async Task A_poster_or_a_post_without_settings_is_refused_without_calling_tiktok()
    {
        var http = Script();

        var poster = await Publisher(http).PublishAsync(Video() with { IsVideo = false }, default);
        var unset = await Publisher(http).PublishAsync(Video() with { TikTokOptions = null }, default);

        Assert.False(poster.Success);
        Assert.False(unset.Success);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void The_whole_publish_fits_inside_the_workers_lease() =>
        Assert.True(TikTokPublisher.Budget < TimeSpan.FromSeconds(new PublishWorkerOptions().LeaseSeconds));

    [Theory]
    [InlineData(7L * 1024 * 1024, 7L * 1024 * 1024, 1)]          // under 64 MB: one chunk, the whole file
    [InlineData(64L * 1024 * 1024, 64L * 1024 * 1024, 1)]
    [InlineData(105L * 1024 * 1024, 10L * 1024 * 1024, 10)]      // last chunk takes the 15 MB remainder
    public void Videos_are_cut_into_the_chunks_tiktok_allows(long size, long chunk, int count) =>
        Assert.Equal((chunk, count), TikTokPublisher.Chunking(size));

    [Fact]
    public void A_long_caption_is_cut_to_tiktoks_limit_without_splitting_an_emoji()
    {
        var caption = TikTokPublisher.Caption(new string('a', TikTokPublisher.CaptionMax - 1) + "\U0001F489 more");
        Assert.True(caption.Length <= TikTokPublisher.CaptionMax);
        Assert.False(char.IsHighSurrogate(caption[^1]));
    }

    /* ---------------- the post's settings ---------------- */

    [Fact]
    public void A_tiktok_post_needs_its_settings_chosen()
    {
        var targets = new[] { ("c1", SocialPlatform.TikTokAccount) };

        Assert.Throws<DomainException>(() =>
            ScheduledPost.Create("owner", PublishSourceKind.Video, "v1", "Video", "", null, targets));
        Assert.Throws<DomainException>(() =>
            ScheduledPost.Create("owner", PublishSourceKind.Video, "v1", "Video", "", null, targets,
                tikTokOptions: new TikTokPostOptions("")));

        var post = ScheduledPost.Create("owner", PublishSourceKind.Video, "v1", "Video", "", null, targets,
            tikTokOptions: Everyone);
        Assert.Equal(Everyone, post.TikTokOptions);
    }

    [Fact]
    public void Branded_content_cannot_be_private()
    {
        Assert.NotNull(new TikTokPostOptions("SELF_ONLY", IsPaidPartnership: true).Problem());
        Assert.Null(new TikTokPostOptions("PUBLIC_TO_EVERYONE", IsPaidPartnership: true).Problem());
    }

    [Fact]
    public void Settings_are_not_kept_on_a_post_with_no_tiktok_account()
    {
        var post = ScheduledPost.Create("owner", PublishSourceKind.Video, "v1", "Video", "", null,
            new[] { ("c1", SocialPlatform.FacebookPage) }, tikTokOptions: Everyone);
        Assert.Null(post.TikTokOptions);
    }

    [Fact]
    public void TikTok_takes_videos_and_not_posters()
    {
        Assert.True(SocialPlatform.TikTokAccount.IsPublishTarget());
        Assert.True(SocialPlatform.TikTokAccount.SupportsVideo());
        Assert.False(SocialPlatform.TikTokAccount.SupportsImages());
        Assert.Equal("TikTok", SocialPlatform.TikTokAccount.DisplayName());
    }

    [Fact]
    public void The_credential_round_trips_and_knows_when_it_has_lapsed()
    {
        var parsed = TikTokCredential.Parse(Fresh("secret-access", "secret-refresh"));
        Assert.Equal("secret-access", parsed!.AccessToken);
        Assert.Equal("secret-refresh", parsed.RefreshToken);
        Assert.True(parsed.AccessValidAt(DateTime.UtcNow));
        Assert.False(TikTokCredential.Parse(Lapsed())!.AccessValidAt(DateTime.UtcNow));
        Assert.Null(TikTokCredential.Parse("not json"));
        Assert.DoesNotContain("secret", parsed.ToString());   // no token in logs
    }

    /* ---------------- connecting ---------------- */

    private static TikTokOAuthProvider Provider(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(Settings), NullLogger<TikTokOAuthProvider>.Instance);

    [Fact]
    public void The_consent_asks_for_posting_with_the_client_key()
    {
        var url = Provider(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
            .BuildAuthorizationUrl("state-1", "https://app/social/callback/tiktok");

        Assert.StartsWith("https://www.tiktok.com/v2/auth/authorize/?client_key=tt-key", url);
        Assert.Contains("scope=user.info.basic,video.publish", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://app/social/callback/tiktok"), url);
        Assert.Contains("state=state-1", url);
    }

    [Fact]
    public async Task Connecting_lists_the_account_with_a_credential_that_holds_both_tokens()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"act-1","expires_in":86400,"refresh_token":"rft-1","refresh_expires_in":31536000,"open_id":"open-1","scope":"user.info.basic,video.publish"}""")
            : Json(HttpStatusCode.OK, """{"data":{"user":{"open_id":"open-1","display_name":"High St Pharmacy","avatar_url":"https://p16/a.jpg"}},"error":{"code":"ok"}}"""));

        var account = Assert.Single(await Provider(http).ExchangeCodeAsync("code", "https://app/social/callback/tiktok", default));

        Assert.Equal("open-1", account.ExternalAccountId);
        Assert.Equal("High St Pharmacy", account.DisplayName);
        Assert.Equal(SocialPlatform.TikTokAccount, account.Platform);
        Assert.Equal("open-1", account.AuthorizedByUserId);
        Assert.Null(account.RefreshToken);   // a second copy would go stale at the first rotation
        var credential = TikTokCredential.Parse(account.AccessToken);
        Assert.Equal("act-1", credential!.AccessToken);
        Assert.Equal("rft-1", credential.RefreshToken);
        Assert.InRange(account.ExpiresAtUtc!.Value, DateTime.UtcNow.AddDays(364), DateTime.UtcNow.AddDays(366));
        Assert.Contains(http.Requests, r => r.Uri == TokenUrl && r.Body.Contains("client_key=tt-key") && r.Body.Contains("code=code"));
        Assert.DoesNotContain(http.Requests, r => r.Uri.Contains("tt-secret"));
    }

    [Fact]
    public async Task Connecting_without_permission_to_post_is_refused_plainly()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","open_id":"o","scope":"user.info.basic"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", default));
        Assert.Contains("Permission to post", ex.Message);
    }

    [Fact]
    public async Task TikToks_own_refusal_is_passed_on()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":"invalid_request","error_description":"Redirect_uri is not matched with the uri when requesting code."}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provider(http).ExchangeCodeAsync("code", "https://app/cb", default));
        Assert.Contains("Redirect_uri", ex.Message);
    }

    /* ---------------- the daily check ---------------- */

    private static TikTokProbe Probe(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Options.Create(Settings), NullLogger<TikTokProbe>.Instance);

    [Fact]
    public async Task The_daily_check_refreshes_and_hands_back_the_rotated_credential()
    {
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"act-2","expires_in":86400,"refresh_token":"rft-2","refresh_expires_in":31536000}""")
            : Json(HttpStatusCode.OK, """{"data":{"user":{"open_id":"open-1"}},"error":{"code":"ok"}}"""));

        var (result, _, renewed) = await Probe(http).ProbeAndRenewAsync("open-1", Lapsed(), default);

        Assert.Equal(ProbeResult.Healthy, result);
        Assert.Equal("rft-2", TikTokCredential.Parse(renewed?.Credential)!.RefreshToken);
        Assert.Contains(http.Requests, r => r.Uri.StartsWith(UserInfoUrl) && r.Authorization == "Bearer act-2");
    }

    [Fact]
    public async Task The_daily_check_flags_a_refused_sign_in()
    {
        var refused = new ScriptedHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"code":"access_token_invalid"}}"""));
        var (result, _, _) = await Probe(refused).ProbeAndRenewAsync("open-1", Fresh(), default);
        Assert.Equal(ProbeResult.Unauthorized, result);

        var expired = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        var (lapsed, _, _) = await Probe(expired).ProbeAndRenewAsync("open-1", Lapsed(), default);
        Assert.Equal(ProbeResult.Unauthorized, lapsed);
    }

    [Fact]
    public async Task The_health_check_saves_a_credential_the_probe_rotated()
    {
        var connection = SocialConnection.Create("owner", SocialPlatform.TikTokAccount, "open-1", "TikTok",
            EncryptedSecret.Create(Lapsed(), "n", "t", 1), null, DateTime.UtcNow.AddDays(300));
        var repo = new Repo(connection);
        var http = new ScriptedHandler(req => req.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"act-2","expires_in":86400,"refresh_token":"rft-2","refresh_expires_in":31536000}""")
            : Json(HttpStatusCode.OK, """{"data":{"user":{"open_id":"open-1"}},"error":{"code":"ok"}}"""));

        var service = new ConnectionHealthService(repo, new OAuthFlowTests.PlainCipher(),
            new ISocialConnectionProbe[] { Probe(http) }, NullLogger<ConnectionHealthService>.Instance);
        await service.CheckDueAsync(5, default);

        Assert.Equal("rft-2", TikTokCredential.Parse(repo.Renewed)!.RefreshToken);
    }

    [Fact]
    public async Task A_refresh_refused_because_a_publish_rotated_it_first_does_not_ask_for_reconnecting()
    {
        // The daily check read the credential, then a publish refreshed it
        // (retiring the old refresh token) before the check's own refresh.
        var connection = SocialConnection.Create("owner", SocialPlatform.TikTokAccount, "open-1", "TikTok",
            EncryptedSecret.Create(Lapsed(), "n", "t", 1), null, DateTime.UtcNow.AddDays(300));
        var repo = new Repo(connection);
        var http = new ScriptedHandler(_ =>
        {
            connection.RenewCredential(EncryptedSecret.Create(Fresh("act-9", "rft-9"), "n", "t", 1), null);
            return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Refresh token is invalid"}""");
        });

        var service = new ConnectionHealthService(repo, new OAuthFlowTests.PlainCipher(),
            new ISocialConnectionProbe[] { Probe(http) }, NullLogger<ConnectionHealthService>.Instance);
        await service.CheckDueAsync(5, default);

        Assert.Empty(repo.HealthRecorded);   // inconclusive: looked at again tomorrow
    }

    [Fact]
    public async Task A_refresh_refused_with_nobody_else_rotating_does_ask_for_reconnecting()
    {
        var connection = SocialConnection.Create("owner", SocialPlatform.TikTokAccount, "open-1", "TikTok",
            EncryptedSecret.Create(Lapsed(), "n", "t", 1), null, DateTime.UtcNow.AddDays(300));
        var repo = new Repo(connection);
        var http = new ScriptedHandler(_ =>
            Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Refresh token is invalid"}"""));

        var service = new ConnectionHealthService(repo, new OAuthFlowTests.PlainCipher(),
            new ISocialConnectionProbe[] { Probe(http) }, NullLogger<ConnectionHealthService>.Instance);
        await service.CheckDueAsync(5, default);

        Assert.Equal(new[] { false }, repo.HealthRecorded);
    }

    /* ---------------- the share screen's account info ---------------- */

    [Fact]
    public async Task The_share_screen_gets_live_creator_info_and_any_rotation_is_saved()
    {
        var connection = SocialConnection.Create("owner", SocialPlatform.TikTokAccount, "open-1", "TikTok",
            EncryptedSecret.Create("stored", "n", "t", 1), null, null);
        var repo = new Repo(connection);
        var info = new TikTokCreatorInfo("highstpharmacy", "High St Pharmacy", null,
            new[] { "SELF_ONLY" }, false, true, true, 600);
        var client = new FixedCreatorInfo(new TikTokCreatorInfoResult(info, null, Renewed: new RenewedCredential("rotated", null)));

        var service = new TikTokCreatorService(repo, new OAuthFlowTests.PlainCipher(), client,
            NullLogger<TikTokCreatorService>.Instance);

        var mine = await service.GetAsync("owner", connection.Id, default);
        var theirs = await service.GetAsync("someone-else", connection.Id, default);

        Assert.Equal(ResultKind.Success, mine.Kind);
        Assert.Equal("High St Pharmacy", mine.Value!.Nickname);
        Assert.Equal("stored", client.Seen);
        Assert.Equal("rotated", repo.Renewed);
        Assert.Equal(ResultKind.NotFound, theirs.Kind);
    }

    private sealed class FixedCreatorInfo(TikTokCreatorInfoResult answer) : ITikTokCreatorInfoClient
    {
        public string? Seen { get; private set; }

        public Task<TikTokCreatorInfoResult> GetAsync(string credential, CancellationToken ct)
        {
            Seen = credential;
            return Task.FromResult(answer);
        }
    }

    /* ---------------- fakes ---------------- */

    /// <summary>One connection; records a renewed credential. Written out rather than a proxy, because the method under test has a default interface implementation.</summary>
    private sealed class Repo(SocialConnection connection) : ISocialConnectionRepository
    {
        private bool _claimed;
        public string? Renewed { get; private set; }

        public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct)
        {
            Renewed = credential.CipherText;
            return Task.CompletedTask;
        }

        public Task<SocialConnection?> ClaimForHealthCheckAsync(
            IReadOnlyCollection<SocialPlatform> platforms, DateTime checkedBeforeUtc, DateTime nowUtc, CancellationToken ct)
        {
            var next = _claimed ? null : connection;
            _claimed = true;
            return Task.FromResult(next);
        }

        public List<bool> HealthRecorded { get; } = new();

        public Task RecordHealthAsync(string id, bool healthy, string? error, DateTime nowUtc, CancellationToken ct)
        {
            HealthRecorded.Add(healthy);
            return Task.CompletedTask;
        }

        public Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult<SocialConnection?>(id == connection.Id ? connection : null);
        public Task UpdateAsync(SocialConnection c, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SocialConnection?> FindByExternalAsync(string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAsync(SocialConnection c, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveAsync(string ownerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SocialConnection>> ListByAuthorizerAsync(IReadOnlyCollection<SocialPlatform> platforms, string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> GrantInUseAsync(string fingerprint, string exceptId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Uri, string Body, string? Authorization, string? ContentRange);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var isFile = request.Method == HttpMethod.Put;
            var body = request.Content is null || isFile ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString(),
                request.Content?.Headers.ContentRange?.ToString()));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
