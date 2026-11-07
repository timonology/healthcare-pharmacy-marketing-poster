using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// Sessions: how they are obtained, rotated, and ended.
/// </summary>
/// <remarks>
/// <para>
/// None of this was covered. The suite had 157 tests and not one of them
/// asserted that signing out ends a session — the part of the system where a
/// silent failure means somebody keeps access after they were meant to lose it.
/// </para>
/// <para>
/// Written against HTTP rather than against <c>AuthService</c> directly,
/// because the interesting behaviour is spread across the service, the token
/// repository and the authorisation pipeline. A service-level test would have
/// passed while the endpoint was unprotected.
/// </para>
/// </remarks>
public sealed class AuthLifecycleTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Session(string Access, string Refresh);

    /// <summary>Registers an account and returns a clean, unauthenticated client plus its tokens.</summary>
    private async Task<(HttpClient Client, Session Session, string Email)> NewSessionAsync(string label)
    {
        var email = $"auth-{label}-{Guid.NewGuid().ToString("N")[..8]}@example.test";
        const string password = "IntegrationTest-123!";

        var client = Api.NewClient();
        using var res = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            displayName = $"Auth {label}",
        });

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not register ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");

        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (
            client,
            new Session(
                body.GetProperty("accessToken").GetString()!,
                body.GetProperty("refreshToken").GetString()!),
            email);
    }

    private async Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });

    private static void Bearer(HttpClient client, string? accessToken) =>
        client.DefaultRequestHeaders.Authorization = accessToken is null
            ? null
            : new AuthenticationHeaderValue("Bearer", accessToken);

    /* ── obtaining one ───────────────────────────────────────────────── */

    [Fact]
    public async Task Registering_returns_a_session_that_works()
    {
        RequireDatabase();

        var (client, session, _) = await NewSessionAsync("basic");
        Bearer(client, session.Access);

        using var me = await client.GetAsync("/api/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refresh_issues_a_working_access_token()
    {
        RequireDatabase();

        var (client, session, _) = await NewSessionAsync("refresh");

        using var refreshed = await RefreshAsync(client, session.Refresh);
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await refreshed.Content.ReadFromJsonAsync<JsonElement>(Json);
        var newAccess = body.GetProperty("accessToken").GetString();

        Bearer(client, newAccess);
        using var me = await client.GetAsync("/api/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK, "the refreshed access token must be usable");
    }

    /* ── rotation ────────────────────────────────────────────────────── */

    [Fact]
    public async Task A_refresh_token_cannot_be_used_twice()
    {
        // Rotation is the whole security value of refresh tokens. Without it a
        // token copied from storage stays valid for its full 30-day life.
        RequireDatabase();

        var (client, session, _) = await NewSessionAsync("rotate");

        using var first = await RefreshAsync(client, session.Refresh);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        using var replay = await RefreshAsync(client, session.Refresh);
        replay.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the original token was rotated and must not be accepted again");
    }

    [Fact]
    public async Task Replaying_a_rotated_token_kills_the_session_that_replaced_it()
    {
        // Breach response, and the reason this endpoint is worth testing rather
        // than reasoning about. A replay means two parties hold tokens from the
        // same chain and the server cannot tell which is the legitimate one —
        // so AuthService revokes the whole family. The honest outcome is that
        // the real user is signed out too.
        RequireDatabase();

        var (client, session, _) = await NewSessionAsync("replay");

        using var rotated = await RefreshAsync(client, session.Refresh);
        var body = await rotated.Content.ReadFromJsonAsync<JsonElement>(Json);
        var liveRefresh = body.GetProperty("refreshToken").GetString()!;

        // The attacker replays the stolen, already-rotated token.
        using var replay = await RefreshAsync(client, session.Refresh);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The good token must now be dead as well.
        using var afterBreach = await RefreshAsync(client, liveRefresh);
        afterBreach.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "detecting a replay must revoke every token in the chain, not just the replayed one");
    }

    [Fact]
    public async Task An_unknown_refresh_token_is_refused()
    {
        RequireDatabase();

        var client = Api.NewClient();
        using var res = await RefreshAsync(client, "not-a-token-anyone-ever-issued");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /* ── ending one ──────────────────────────────────────────────────── */

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token()
    {
        // The one that matters most: without it, "log out" clears the browser
        // and leaves a credential that still mints access tokens for 30 days.
        RequireDatabase();

        var (client, session, _) = await NewSessionAsync("logout");
        Bearer(client, session.Access);

        using var logout = await client.PostAsync("/api/auth/logout", null);
        ((int)logout.StatusCode).Should().BeInRange(200, 299);

        Bearer(client, null);
        using var afterLogout = await RefreshAsync(client, session.Refresh);
        afterLogout.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "the refresh token must not survive a sign-out");
    }

    [Fact]
    public async Task Signing_out_requires_a_session()
    {
        // Guards a specific regression. Marking the whole controller
        // [AllowAnonymous] to document that login is public also unprotected
        // this endpoint, because [AllowAnonymous] overrides a method-level
        // [Authorize]. Nothing looked different — the action returned 401 from
        // its own body — so only a test at this level can tell the difference
        // between "the pipeline refused" and "the method happened to".
        RequireDatabase();

        var client = Api.NewClient();
        using var res = await client.PostAsync("/api/auth/logout", null);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signing_out_does_not_end_anybody_elses_session()
    {
        // RevokeAllForUserAsync takes a user id. A scoping mistake there would
        // sign out the entire platform, and would look like a flaky logout.
        RequireDatabase();

        var (mine, mySession, _) = await NewSessionAsync("mine");
        var (theirs, theirSession, _) = await NewSessionAsync("theirs");

        Bearer(mine, mySession.Access);
        using var logout = await mine.PostAsync("/api/auth/logout", null);
        ((int)logout.StatusCode).Should().BeInRange(200, 299);

        using var stillGood = await RefreshAsync(theirs, theirSession.Refresh);
        stillGood.StatusCode.Should().Be(
            HttpStatusCode.OK, "one account signing out must not touch another's session");
    }

    /* ── the access token itself ─────────────────────────────────────── */

    [Fact]
    public async Task A_token_this_host_did_not_sign_is_refused()
    {
        // The signature is the only thing separating a claim from an assertion.
        RequireDatabase();

        var (client, session, _) = await NewSessionAsync("forged");

        // Mutate the FIRST character of the signature, not the last.
        //
        // The last one does not reliably change anything. An HMAC-SHA256
        // signature is 32 bytes, which base64url-encodes to 43 characters —
        // 258 bits of alphabet for 256 bits of data. The final character
        // therefore carries only 4 meaningful bits, and the low 2 are padding
        // a decoder ignores. 'A' and 'B' differ only in those padding bits, so
        // swapping one for the other decodes to byte-identical bytes and the
        // token stays perfectly valid.
        //
        // This test did exactly that, and passed for weeks by luck: it only
        // failed when the signature happened to end in 'A', which is 1 of the
        // 16 possible final characters — measured at 5.9% of tokens. When it
        // did fail it reported a forged token being accepted, which reads like
        // a serious auth hole and was nothing of the kind.
        //
        // Every one of the first 42 characters encodes 6 bits of real data, so
        // changing index 0 always changes the signature.
        var parts = session.Access.Split('.');
        parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
        Bearer(client, string.Join('.', parts));

        using var me = await client.GetAsync("/api/me");
        me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unsigned_token_claiming_alg_none_is_refused()
    {
        // The classic JWT attack: present a token whose header says no signature
        // is required. A validator that honours `alg` from the token instead of
        // from its own configuration accepts anything the attacker writes.
        //
        // {"alg":"none","typ":"JWT"} . {"sub":"...","role":"SuperAdmin"} . (empty)
        RequireDatabase();

        const string header = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0";
        const string payload = "eyJzdWIiOiJhdHRhY2tlciIsInJvbGUiOiJTdXBlckFkbWluIn0";

        var client = Api.NewClient();
        Bearer(client, $"{header}.{payload}.");

        using var res = await client.GetAsync("/api/me");
        res.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an unsigned token must never be accepted, whatever its header claims");
    }

    [Fact]
    public async Task Rubbish_in_the_authorization_header_is_refused_not_crashed()
    {
        // Malformed credentials should be a 4xx, never a 500. A stack trace here
        // would be an unauthenticated caller reaching an unhandled exception.
        RequireDatabase();

        // No empty string: AuthenticationHeaderValue rejects an empty parameter
        // before the request is ever made, so that case tests the test.
        foreach (var value in new[] { "not-a-jwt", "a.b.c", "Bearer Bearer x" })
        {
            var client = Api.NewClient();
            Bearer(client, value);

            using var res = await client.GetAsync("/api/me");
            ((int)res.StatusCode).Should().BeInRange(
                400, 499, $"'{value}' should be refused, not produce a server error");
        }
    }
}
