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
/// How the Meta publishers classify what Graph says — the decision between
/// "retry", "stop" and "it may already be live" — and that tokens stay out of URLs.
/// </summary>
public class MetaPublisherTests
{
    private static readonly MetaOptions Options = new()
    {
        AppId = "app", AppSecret = "app-secret", GraphVersion = "v26.0",
    };

    private static PublishRequest Request(SocialPlatform platform) => new(
        platform, "acct-1", "page-token", "Book your flu jab", "https://blob/poster.jpg", IsVideo: false);

    /* ---------------- Facebook: the one call publishes ---------------- */

    [Fact]
    public async Task Facebook_success_returns_the_post_id()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"id":"photo-1","post_id":"page_1"}"""));

        var outcome = await Facebook(http).PublishAsync(Request(SocialPlatform.FacebookPage), default);

        Assert.True(outcome.Success);
        Assert.Equal("page_1", outcome.ExternalPostId);
    }

    [Fact]
    public async Task A_rate_limit_is_retryable_because_nothing_was_posted()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Too many calls","code":4}}"""));

        var outcome = await Facebook(http).PublishAsync(Request(SocialPlatform.FacebookPage), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_server_fault_on_the_publishing_call_is_an_unknown_outcome()
    {
        var http = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>Bad gateway</html>"),
        });

        var outcome = await Facebook(http).PublishAsync(Request(SocialPlatform.FacebookPage), default);

        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task A_timeout_on_the_publishing_call_is_an_unknown_outcome()
    {
        var http = new ScriptedHandler(_ => throw new TaskCanceledException("HttpClient.Timeout"));

        var outcome = await Facebook(http).PublishAsync(Request(SocialPlatform.FacebookPage), default);

        Assert.True(outcome.OutcomeUnknown);
        Assert.False(outcome.IsRetryable);
    }

    [Fact]
    public async Task A_connection_never_made_is_safe_to_retry()
    {
        var http = new ScriptedHandler(_ =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        var outcome = await Facebook(http).PublishAsync(Request(SocialPlatform.FacebookPage), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task Accepted_with_no_post_id_is_an_unknown_outcome()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}"));

        var outcome = await Facebook(http).PublishAsync(Request(SocialPlatform.FacebookPage), default);

        Assert.False(outcome.Success);
        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task The_token_goes_in_the_body_with_an_app_secret_proof_never_the_url()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"post_id":"p"}"""));

        await Facebook(http).PublishAsync(Request(SocialPlatform.FacebookPage), default);

        var sent = http.Requests.Single();
        Assert.DoesNotContain("page-token", sent.Uri);
        Assert.Contains("access_token=page-token", sent.Body);
        Assert.Contains($"appsecret_proof={MetaAppSecretProof.For("page-token", "app-secret")}", sent.Body);
        Assert.Contains("/v26.0/", sent.Uri);
    }

    /* ---------------- Instagram: only step 3 publishes ---------------- */

    [Fact]
    public async Task Instagram_failures_before_media_publish_are_retryable()
    {
        var http = new ScriptedHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/media")
            ? Json(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"Temporary","code":2}}""")
            : throw new InvalidOperationException("should not get further"));

        var outcome = await Instagram(http).PublishAsync(Request(SocialPlatform.InstagramBusiness), default);

        Assert.True(outcome.IsRetryable);
        Assert.False(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task Instagram_media_publish_failing_with_a_fault_is_an_unknown_outcome()
    {
        var http = new ScriptedHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/media")) return Json(HttpStatusCode.OK, """{"id":"container-1"}""");
            if (path.EndsWith("/container-1")) return Json(HttpStatusCode.OK, """{"status_code":"FINISHED"}""");
            return Json(HttpStatusCode.InternalServerError, """{"error":{"message":"Unknown","code":1}}""");
        });

        var outcome = await Instagram(http).PublishAsync(Request(SocialPlatform.InstagramBusiness), default);

        Assert.True(outcome.OutcomeUnknown);
    }

    [Fact]
    public async Task Instagram_status_polling_sends_the_token_as_a_header()
    {
        var http = new ScriptedHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/media")) return Json(HttpStatusCode.OK, """{"id":"container-1"}""");
            if (path.EndsWith("/container-1")) return Json(HttpStatusCode.OK, """{"status_code":"FINISHED"}""");
            return Json(HttpStatusCode.OK, """{"id":"media-1"}""");
        });

        var outcome = await Instagram(http).PublishAsync(Request(SocialPlatform.InstagramBusiness), default);

        Assert.True(outcome.Success);
        var poll = http.Requests.Single(r => r.Uri.Contains("container-1"));
        Assert.DoesNotContain("page-token", poll.Uri);
        Assert.Equal("Bearer page-token", poll.Authorization);
    }

    [Fact]
    public async Task Instagram_gets_a_view_link_when_meta_gives_one()
    {
        var http = new ScriptedHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/media")) return Json(HttpStatusCode.OK, """{"id":"container-1"}""");
            if (path.EndsWith("/container-1")) return Json(HttpStatusCode.OK, """{"status_code":"FINISHED"}""");
            if (path.EndsWith("/media_publish")) return Json(HttpStatusCode.OK, """{"id":"media-1"}""");
            return Json(HttpStatusCode.OK, """{"permalink":"https://www.instagram.com/p/abc/"}""");
        });

        var outcome = await Instagram(http).PublishAsync(Request(SocialPlatform.InstagramBusiness), default);

        Assert.True(outcome.Success);
        Assert.Equal("https://www.instagram.com/p/abc/", outcome.Permalink);
    }

    [Fact]
    public async Task A_failed_permalink_lookup_never_turns_a_published_post_into_a_failure()
    {
        // A failure here would be retried, and a retry is a second post.
        var http = new ScriptedHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/media")) return Json(HttpStatusCode.OK, """{"id":"container-1"}""");
            if (path.EndsWith("/container-1")) return Json(HttpStatusCode.OK, """{"status_code":"FINISHED"}""");
            if (path.EndsWith("/media_publish")) return Json(HttpStatusCode.OK, """{"id":"media-1"}""");
            throw new HttpRequestException("permalink lookup failed");
        });

        var outcome = await Instagram(http).PublishAsync(Request(SocialPlatform.InstagramBusiness), default);

        Assert.True(outcome.Success);
        Assert.Equal("media-1", outcome.ExternalPostId);
        Assert.Null(outcome.Permalink);
    }

    /* ---------------- health check ---------------- */

    [Fact]
    public async Task A_revoked_token_is_reported_as_unauthorized()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.BadRequest,
            """{"error":{"message":"Session has expired","code":190}}"""));

        var (result, _) = await Probe(http).ProbeAsync("page-1", "page-token", default);

        Assert.Equal(ProbeResult.Unauthorized, result);
    }

    [Fact]
    public async Task A_meta_outage_is_inconclusive_not_a_reason_to_reconnect()
    {
        var http = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("down"),
        });

        var (result, _) = await Probe(http).ProbeAsync("page-1", "page-token", default);

        Assert.Equal(ProbeResult.Inconclusive, result);
    }

    [Fact]
    public async Task A_healthy_page_is_healthy_and_the_token_stays_out_of_the_url()
    {
        var http = new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"id":"page-1"}"""));

        var (result, _) = await Probe(http).ProbeAsync("page-1", "page-token", default);

        Assert.Equal(ProbeResult.Healthy, result);
        Assert.DoesNotContain("page-token", http.Requests.Single().Uri);
    }

    private static FacebookPageProbe Probe(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<FacebookPageProbe>.Instance);

    /* ---------------- classification table ---------------- */

    [Theory]
    [InlineData(190, 400, false, false)]   // token revoked: reconnect, do not retry
    [InlineData(4, 400, true, false)]      // rate limit: retry, nothing posted
    [InlineData(368, 400, false, false)]   // policy block: retrying can extend it
    [InlineData(2, 503, true, true)]       // server fault: may have happened
    [InlineData(100, 400, false, false)]   // bad parameter: will never work
    public void Graph_errors_are_classified(int code, int status, bool retryable, bool mayHaveSucceeded)
    {
        var ex = MetaPublisherBase.Classify("msg", code, 0, status);

        Assert.Equal(retryable, ex.IsRetryable);
        Assert.Equal(mayHaveSucceeded, ex.MayHaveSucceeded);
        Assert.Equal(code is 190, ex.IsAuthFailure);
    }

    /* ================================================================ */

    private static FacebookPagePublisher Facebook(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<FacebookPagePublisher>.Instance);

    private static InstagramPublisher Instagram(ScriptedHandler http) =>
        new(new SingleClientFactory(http), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<InstagramPublisher>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Uri, string Body, string? Authorization);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Sent(request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            return respond(request);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
