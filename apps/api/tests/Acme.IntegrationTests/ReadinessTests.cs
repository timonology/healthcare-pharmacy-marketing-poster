using System.Net;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// GET /api/health/ready is closed to anyone without the readiness token.
/// </summary>
/// <remarks>
/// An accepted call checks real providers (Mailjet, Stripe, Meta, Google), so
/// only the refusals are tested here; `pnpm smoke` exercises the report itself,
/// and its self-test proves each verdict is acted on.
/// </remarks>
public sealed class ReadinessTests(ApiFactory api) : IntegrationTest(api)
{
    [Fact]
    public async Task Without_the_token_there_is_no_such_endpoint()
    {
        RequireDatabase();

        using var res = await Api.NewClient().GetAsync("/api/health/ready");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the report maps the infrastructure; it must not exist for a caller without the token");
    }

    [Fact]
    public async Task A_wrong_token_looks_the_same_as_none()
    {
        RequireDatabase();

        var client = Api.NewClient();
        client.DefaultRequestHeaders.Add("X-Readiness-Token", ApiFactory.ReadinessToken + "x");
        using var res = await client.GetAsync("/api/health/ready");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_signed_in_user_is_not_enough()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("readiness");
        using var res = await client.GetAsync("/api/health/ready");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a session is not the readiness token");
    }
}
