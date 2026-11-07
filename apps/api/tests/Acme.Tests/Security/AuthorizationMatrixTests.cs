using System.Reflection;
using Acme.WebApi;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Acme.Tests.Security;

/// <summary>
/// Every endpoint in the API, checked against the access rules it must obey.
/// </summary>
/// <remarks>
/// <para>
/// This test enumerates the controllers by reflection rather than listing them,
/// which is the entire point: a new endpoint is covered the moment it is
/// written. Every other kind of test has to be remembered. Forgetting to add an
/// <c>[Authorize]</c> produces a working endpoint that ships open, and nothing
/// about it looks wrong in review — the code compiles, the feature works, and
/// the only symptom is that it also works for people who should not have it.
/// </para>
/// <para>
/// Needs no host, no HTTP and no database, so it runs in the fast suite and
/// costs nothing to keep.
/// </para>
/// </remarks>
public class AuthorizationMatrixTests
{
    private static readonly Assembly WebApi = typeof(AuthPolicies).Assembly;

    /// <summary>
    /// Endpoints that are deliberately reachable without signing in.
    /// </summary>
    /// <remarks>
    /// An explicit list, deliberately. Anonymous access is a decision, and this
    /// makes taking it require an edit here with a reason attached — rather
    /// than an <c>[AllowAnonymous]</c> that nobody reviews. Adding an endpoint
    /// to this list is the visible, arguable step it should be.
    /// </remarks>
    private static readonly Dictionary<string, string> AnonymousByDesign = new()
    {
        ["AuthController.Register"]     = "creating an account is how you get a credential",
        ["AuthController.Login"]        = "signing in cannot require being signed in",
        ["AuthController.Refresh"]      = "exchanges a refresh token, not an access token",

        // The recovery flows. Each authenticates by proving control of the
        // mailbox — a 256-bit single-use token from a link — rather than by a
        // session, because the whole point is that the person cannot sign in.
        ["AuthController.ForgotPassword"] = "you cannot be signed in to recover a password you have lost",
        ["AuthController.ResetPassword"]  = "the emailed token is the credential",
        ["AuthController.VerifyEmail"]    = "same — the link proves the address receives mail",

        // ResendVerification is deliberately absent: it keeps [Authorize],
        // because the identity comes from the session rather than from a
        // submitted address. An anonymous version would be a way to have the
        // product send mail to any address on request.

        // Logout is deliberately absent: it keeps [Authorize]. Ending a session
        // is for the holder of one, and the "stale entries" test below turns
        // that into something the compiler-adjacent suite enforces rather than
        // a comment nobody re-reads.

        ["PublicInvitationsController.Preview"] = "the invitee has no account yet; the token is the credential",
        ["PublicInvitationsController.Accept"]  = "same — this is the request that creates the password",

        ["TemplatesController.List"] = "the gallery and landing page are public",
        ["TemplatesController.Get"]  = "a single public template",
        ["AssetsController.Get"]     = "template images, served to the public gallery",

        ["SubscriptionController.Plans"] = "pricing is public",

        // Security review R2-06. The person unsubscribing has no account; the
        // signed link is the credential, and all it can do is stop mail to its
        // own address. GET only describes — scanners fetch every link.
        ["UnsubscribeController.Preview"]     = "the email recipient has no account; the signed link is the credential",
        ["UnsubscribeController.Unsubscribe"] = "same — and mail clients POST here for one-click unsubscribe (RFC 8058)",

        ["BillingController.Webhook"] = "Stripe calls this; it authenticates by signature, not by session",

        // The smoke test and deploy pipelines have no user session. Authenticated
        // by a shared token (Readiness:Token, X-Readiness-Token), 404 without it
        // or when none is configured; returns verdicts and setting names only.
        ["ReadinessController.Get"] = "authenticated by the readiness token, not a session",

        // Meta's platform callbacks, required for Facebook Login App Review.
        // Meta has no session with us; the signed_request — HMAC-SHA256 under
        // the app secret — is the credential, and a valid one can only revoke
        // or delete the connections its own signer granted.
        ["MetaCallbacksController.Deauthorize"]  = "Meta calls this; it authenticates by signed_request, not by session",
        ["MetaCallbacksController.DataDeletion"] = "same — and must answer with a status URL Meta shows the person",

        // Two assets, two lifetimes, one endpoint. The MP4 is signed for 48 hours
        // — it is the valuable thing and is meant to lapse. The still it is
        // fronted by is signed for the same 180 days as a campaign poster,
        // because an email opened next month with a broken image is a worse
        // outcome than a six-month-old thumbnail of an advert the pharmacy
        // chose to send. The signature binds which of the two it opens, so a
        // long-lived thumbnail token cannot be used to fetch the video.
        ["VideosController.PublicDownload"] = "share links carry their own signed token",

        // The poster in a campaign email. The reader is a patient with no
        // account, and the image is fetched by their mail provider's proxy —
        // Gmail pulls it from Google's servers — so there is no session to
        // present and nothing to present it to.
        //
        // The signature is the whole of the authorisation, and it names one
        // blob: there is no poster id in the route, so nothing to enumerate and
        // no unsigned parameter for the endpoint to have to distrust. Content
        // addressing means a token cannot be made to serve a different or later
        // render than the one it was signed for.
        //
        // What it exposes if a link leaks: one already-published poster, for six
        // months. That is the same exposure as forwarding the email, which is a
        // thing recipients do.
        ["PostersController.EmailImage"] = "the patient has no account; the signed token names one blob",

        ["DiagnosticsController.GetJwt"]    = "Development-only; returns 404 otherwise",
        ["DiagnosticsController.GetConfig"] = "non-secret config values only",

        // In-pharmacy display screens. A television on a wall has no account and
        // must not hold one: a session cookie on a device in a public room, that
        // nobody signs out of and that is powered on for years, is a worse
        // credential than the alternative below.
        //
        // Each request instead carries a random per-screen token whose SHA-256
        // is all that is stored, scoped to one screen — not to the pharmacy's
        // poster library and not to the account. Revoking it from the Screens
        // page stops the device on its next poll.
        //
        // Claim is the anonymous write, and the one worth arguing about. It
        // exchanges a six-character pairing code for that token. The code is the
        // credential: it is single-use, expires after four hours, is drawn from
        // an alphabet with no lookalike characters, and is read off the owner's
        // own Screens page — so presenting one is evidence of physical access to
        // a signed-in session. What it can change is bounded to pairing one
        // screen the owner just created.
        //
        // Noted rather than claimed: this endpoint is **not rate limited**,
        // because nothing in this application is. The guess space is 23^6 ≈ 148
        // million and a code lives four hours, so sustained guessing is not free
        // — but it is not impossible either, and what it would win is one
        // screen's loop of already-published posters. That is a proportionate
        // risk today and would stop being one if screens ever carried anything
        // that is not already on public display.
        ["DisplayKioskController.Claim"]   = "the device has no account; the one-time pairing code is the credential",
        ["DisplayKioskController.Content"] = "the screen token is the credential, and reaches only that screen's playlist",
        ["DisplayKioskController.Slide"]   = "same token; refuses any poster not on this screen's own playlist",
    };

    /// <summary>Controller actions, as (controller, action, method info).</summary>
    private static IEnumerable<(Type Controller, MethodInfo Action, string Name)> Actions()
    {
        foreach (var controller in WebApi.GetTypes()
                     .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
                     .Where(t => !t.IsAbstract))
        {
            foreach (var action in controller.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (action.IsSpecialName) continue;
                if (!action.GetCustomAttributes()
                        .Any(a => a.GetType().Name.StartsWith("Http", StringComparison.Ordinal)))
                    continue;

                yield return (controller, action, $"{controller.Name}.{action.Name}");
            }
        }
    }

    private static bool IsAnonymous(Type controller, MethodInfo action) =>
        action.GetCustomAttribute<AllowAnonymousAttribute>() is not null
        || controller.GetCustomAttribute<AllowAnonymousAttribute>() is not null;

    private static IEnumerable<AuthorizeAttribute> AuthorizeAttributes(
        Type controller, MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(controller.GetCustomAttributes<AuthorizeAttribute>());

    [Fact]
    public void There_are_endpoints_to_check()
    {
        // Guards the whole file. Every assertion below iterates a collection,
        // and an empty collection passes all of them — a reflection test that
        // silently finds nothing is worse than no test, because it reports
        // success.
        Actions().Should().HaveCountGreaterThan(100,
            "the API has well over a hundred actions; finding fewer means the " +
            "reflection is not seeing the controllers at all");
    }

    [Fact]
    public void Every_endpoint_is_either_authorized_or_deliberately_anonymous()
    {
        var open = Actions()
            .Where(a => IsAnonymous(a.Controller, a.Action))
            .Where(a => !AnonymousByDesign.ContainsKey(a.Name))
            .Select(a => a.Name)
            .ToList();

        open.Should().BeEmpty(
            "these endpoints are reachable without signing in and are not on the " +
            "reviewed list. If that is intended, add each to AnonymousByDesign " +
            "with the reason; if not, this is an access-control hole");
    }

    [Fact]
    public void Every_endpoint_that_is_not_anonymous_requires_authentication()
    {
        var unguarded = Actions()
            .Where(a => !IsAnonymous(a.Controller, a.Action))
            .Where(a => !AuthorizeAttributes(a.Controller, a.Action).Any())
            .Select(a => a.Name)
            .ToList();

        unguarded.Should().BeEmpty(
            "these have neither [Authorize] nor [AllowAnonymous], so they are " +
            "open by omission rather than by decision");
    }

    [Fact]
    public void Everything_under_api_admin_requires_an_admin_policy()
    {
        // The admin area is the one place where a missing policy is not merely
        // a leak of one pharmacy's data but of every pharmacy's.
        var wrong = new List<string>();

        foreach (var (controller, action, name) in Actions())
        {
            var route = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? "";
            if (!route.StartsWith("api/admin", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsAnonymous(controller, action)) { wrong.Add($"{name} (anonymous!)"); continue; }

            var policies = AuthorizeAttributes(controller, action)
                .Select(a => a.Policy)
                .ToList();

            if (!policies.Contains(AuthPolicies.AdminArea)
                && !policies.Contains(AuthPolicies.SuperAdminOnly))
                wrong.Add($"{name} (policies: {string.Join(", ", policies.Select(p => p ?? "<none>"))})");
        }

        wrong.Should().BeEmpty(
            $"every action under /api/admin must require {AuthPolicies.AdminArea} or " +
            $"{AuthPolicies.SuperAdminOnly}; a bare [Authorize] there lets any signed-in " +
            "pharmacy reach the admin console");
    }

    [Fact]
    public void Nothing_anonymous_changes_state_without_a_documented_reason()
    {
        // A GET that leaks is bad. An anonymous POST/PUT/DELETE is worse: it
        // lets a stranger change something. Each one here is on the reviewed
        // list, and this test states why that list has to be short.
        var writes = Actions()
            .Where(a => IsAnonymous(a.Controller, a.Action))
            .Where(a => a.Action.GetCustomAttributes().Any(x =>
                x is HttpPostAttribute or HttpPutAttribute
                  or HttpPatchAttribute or HttpDeleteAttribute))
            .Select(a => a.Name)
            .ToList();

        writes.Should().OnlyContain(
            name => AnonymousByDesign.ContainsKey(name),
            "an anonymous write is a stranger changing state. The ones that exist " +
            "authenticate by something other than a session — a signed webhook, a " +
            "one-time invitation token — and each is listed with that reason");
    }

    [Fact]
    public void The_reviewed_anonymous_list_has_no_stale_entries()
    {
        // The inverse of the first test. An entry left behind after an endpoint
        // is deleted or locked down quietly widens what the list permits, and
        // the next endpoint to take that name inherits the exemption.
        var actual = Actions()
            .Where(a => IsAnonymous(a.Controller, a.Action))
            .Select(a => a.Name)
            .ToHashSet();

        var stale = AnonymousByDesign.Keys.Where(k => !actual.Contains(k)).ToList();

        stale.Should().BeEmpty(
            "these are listed as intentionally anonymous but no longer are — " +
            "remove them, so the list keeps meaning what it says");
    }
}
