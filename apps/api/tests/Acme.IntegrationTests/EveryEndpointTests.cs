using System.Net;
using System.Reflection;
using Acme.WebApi;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;
using Xunit.Abstractions;

namespace Acme.IntegrationTests;

/// <summary>
/// Every endpoint in the API, actually called.
/// </summary>
/// <remarks>
/// <para>
/// Enumerated by reflection, not by hand. A hand-written list covers what
/// somebody remembered; this covers what exists, so an endpoint added next
/// month is covered the day it is written.
/// </para>
/// <para>
/// The authorisation matrix in the unit suite checks the same surface by
/// reading attributes. This one issues real HTTP requests, and the two answer
/// different questions: the matrix asks whether the attribute is present, this
/// asks whether the pipeline honours it. A policy registered under the wrong
/// name, or middleware ordered wrongly, passes the first and fails this.
/// </para>
/// </remarks>
public sealed class EveryEndpointTests(ApiFactory api, ITestOutputHelper output)
    : IntegrationTest(api)
{
    private sealed record Endpoint(string Method, string Route, string Name, bool Anonymous);

    /// <summary>Every routable action, with its concrete URL.</summary>
    private static IEnumerable<Endpoint> Endpoints()
    {
        var assembly = typeof(AuthPolicies).Assembly;

        foreach (var controller in assembly.GetTypes()
                     .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template;
            if (prefix is null) continue;

            var controllerAnon = controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null;

            foreach (var action in controller.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var attr in action.GetCustomAttributes()
                             .OfType<IActionHttpMethodProvider>())
                {
                    var verb = attr.HttpMethods.First();
                    var template = (attr as IRouteTemplateProvider)?.Template ?? "";

                    // Route parameters get a value that is syntactically valid and
                    // certainly absent. A 404 for "no such record" is a fine
                    // outcome; what matters is that it is not a 200.
                    var url = $"/{prefix}/{template}"
                        .Replace("//", "/")
                        .TrimEnd('/');

                    url = System.Text.RegularExpressions.Regex.Replace(
                        url, @"\{[^}]+\}", "000000000000000000000000");

                    yield return new Endpoint(
                        verb,
                        url,
                        $"{controller.Name}.{action.Name}",
                        controllerAnon
                        || action.GetCustomAttribute<AllowAnonymousAttribute>() is not null);
                }
            }
        }
    }

    [Fact]
    public void Reflection_finds_the_whole_api()
    {
        // Everything below iterates this. An empty sequence would make every
        // assertion in the file vacuously true.
        Endpoints().Should().HaveCountGreaterThan(100);
    }

    [Fact]
    public async Task No_protected_endpoint_answers_an_anonymous_caller()
    {
        RequireDatabase();

        var client = Api.NewClient();
        var leaked = new List<string>();

        foreach (var ep in Endpoints().Where(e => !e.Anonymous))
        {
            using var req = new HttpRequestMessage(new HttpMethod(ep.Method), ep.Route);

            // Writes need a body the model binder will accept; an empty object
            // is enough, because a 400 is still not a 200 and this test is
            // about the gate, not the payload.
            if (ep.Method is "POST" or "PUT" or "PATCH")
                req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var res = await client.SendAsync(req);

            // 401 is the correct answer. 403, 404, 405 and 400 all mean the
            // request did not do anything either. 2xx from an anonymous caller
            // is a hole.
            if ((int)res.StatusCode is >= 200 and < 300)
                leaked.Add($"{ep.Method} {ep.Route} ({ep.Name}) -> {(int)res.StatusCode}");
        }

        foreach (var l in leaked) output.WriteLine(l);

        leaked.Should().BeEmpty(
            "these answered a caller with no credentials at all. Either they need " +
            "[Authorize], or they are genuinely public and belong on the reviewed " +
            "list in AuthorizationMatrixTests");
    }

    [Fact]
    public async Task No_admin_endpoint_answers_an_ordinary_pharmacy()
    {
        RequireDatabase();

        var (client, _, _) = await Api.RegisterPharmacyAsync("outsider");
        var leaked = new List<string>();

        foreach (var ep in Endpoints()
                     .Where(e => !e.Anonymous)
                     .Where(e => e.Route.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)))
        {
            using var req = new HttpRequestMessage(new HttpMethod(ep.Method), ep.Route);
            if (ep.Method is "POST" or "PUT" or "PATCH")
                req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var res = await client.SendAsync(req);

            if ((int)res.StatusCode is >= 200 and < 300)
                leaked.Add($"{ep.Method} {ep.Route} ({ep.Name}) -> {(int)res.StatusCode}");
        }

        foreach (var l in leaked) output.WriteLine(l);

        leaked.Should().BeEmpty(
            "a signed-in pharmacy reached the admin console. This is the worst " +
            "failure in the system: admin endpoints see every pharmacy's data");
    }

    [Fact]
    public async Task Anonymous_endpoints_really_are_reachable()
    {
        // The mirror image, and not a formality. Marking something
        // [AllowAnonymous] while the pipeline still rejects it produces a login
        // page nobody can reach, or a public gallery that 401s — a failure that
        // the "nothing leaks" test above is structurally incapable of noticing.
        //
        // <b>An anonymous endpoint that carries its own credential answers 400
        // when that credential is absent, not 401.</b> The display kiosk does
        // this. The distinction is worth having on its own merits — 401 means
        // "your credential is no good", and a screen acts on that by forgetting
        // its token and demanding a visit from an installer, so a request that
        // simply never carried one must not say it — and it is also what keeps
        // this assertion meaningful, because the assertion cannot tell a
        // handler saying no from the pipeline saying no.
        //
        // <b>The other one is passing by luck.</b>
        // /api/videos/{id}/public-download returns Forbidden for a missing
        // token, which this test would count as blocked — it survives only
        // because the fixture id below does not exist, so the handler answers
        // 404 before it ever looks at the token. Give that route a real id and
        // this test starts failing on an endpoint that was always fine.
        RequireDatabase();

        var client = Api.NewClient();
        var blocked = new List<string>();

        foreach (var ep in Endpoints().Where(e => e.Anonymous && e.Method == "GET"))
        {
            using var res = await client.GetAsync(ep.Route);
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                blocked.Add($"GET {ep.Route} ({ep.Name}) -> {(int)res.StatusCode}");
        }

        foreach (var b in blocked) output.WriteLine(b);

        blocked.Should().BeEmpty(
            "these are declared public but the pipeline refuses them");
    }
}
