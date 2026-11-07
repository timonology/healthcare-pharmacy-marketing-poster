using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Application.Messaging;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The account emails, and the flows they carry.
/// </summary>
/// <remarks>
/// <para>
/// Written against the running host with a capturing sender, because the thing
/// that keeps going wrong with email in this codebase is never the template —
/// it is whether the trigger fires at all, and whether the message that leaves
/// is the one the code appears to build. Two emails shipped with a hardcoded
/// sender the provider refuses; nothing in the application ever saw an error.
/// </para>
/// <para>
/// So these tests pull the link out of the captured email and use it. A test
/// that asserts "a reset email was sent" proves less than it looks like: the
/// link inside it can still be wrong, expired on arrival, or point at another
/// environment.
/// </para>
/// </remarks>
public sealed class AccountEmailTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Password = "IntegrationTest-123!";

    private async Task<(HttpClient Client, string Email, string Id)> RegisterAsync(string label)
    {
        Api.Email.Clear();
        return await Api.RegisterPharmacyAsync(label);
    }

    /// <summary>Pulls the first URL containing <paramref name="path"/> out of a message.</summary>
    private static string LinkFrom(EmailMessage message, string path)
    {
        // Read from the plain-text part: the HTML carries the same URL twice
        // (button and visible copy) and HTML-encodes it, so the text part is
        // both simpler and closer to what somebody pastes into a browser.
        var source = message.TextBody ?? message.HtmlBody;
        var match = Regex.Match(source, $@"https?://[^\s]*{Regex.Escape(path)}[^\s]*");

        match.Success.Should().BeTrue(
            "the email must contain a {0} link. Body was:\n{1}", path, source);

        return match.Value;
    }

    private static string TokenFrom(string link) =>
        Uri.UnescapeDataString(Regex.Match(link, @"token=([^&\s]+)").Groups[1].Value);

    private static void Bearer(HttpClient client, string? token) =>
        client.DefaultRequestHeaders.Authorization =
            token is null ? null : new AuthenticationHeaderValue("Bearer", token);

    /* ── registration ────────────────────────────────────────────────── */

    [Fact]
    public async Task Registering_sends_a_welcome_email()
    {
        RequireDatabase();

        var (_, email, _) = await RegisterAsync("welcome");

        Api.Email.Sent.Should().Contain(
            m => m.To.Contains(email) && m.Subject.Contains("Welcome"),
            "signing up should be acknowledged. If this is empty the trigger never " +
            "fired; if the send failed it is logged and swallowed on purpose");
    }

    [Fact]
    public async Task Registering_also_sends_a_verification_email()
    {
        RequireDatabase();

        var (_, email, _) = await RegisterAsync("verify-send");

        Api.Email.Sent.Should().Contain(
            m => m.To.Contains(email) && m.Subject.Contains("Confirm"),
            "verification is recorded from day one, even though it is not enforced yet");
    }

    [Fact]
    public async Task No_account_email_names_its_own_sender()
    {
        // The regression that made invitations undeliverable. A non-empty
        // FromAddress overrides the configured, provider-verified one, and the
        // rejection happens at the provider where no test can see it.
        RequireDatabase();

        await RegisterAsync("sender");

        Api.Email.Sent.Should().NotBeEmpty("registration sends mail");
        Api.Email.Sent.Should().OnlyContain(
            m => string.IsNullOrEmpty(m.FromAddress),
            "every message must inherit the configured sender");
    }

    [Fact]
    public async Task Every_account_email_has_a_text_part()
    {
        // HTML-only mail scores worse with spam filters, and these are the
        // messages that must not be filtered — a reset that lands in spam is
        // an account nobody can recover.
        RequireDatabase();

        await RegisterAsync("textpart");

        Api.Email.Sent.Should().OnlyContain(
            m => !string.IsNullOrWhiteSpace(m.TextBody),
            "an HTML-only message reads as blank when images and markup are stripped");
    }

    /* ── email verification ──────────────────────────────────────────── */

    [Fact]
    public async Task The_verification_link_works()
    {
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("verify-use");

        var message = Api.Email.Sent.First(m => m.Subject.Contains("Confirm"));
        var token = TokenFrom(LinkFrom(message, "/verify-email"));

        using var res = await client.PostAsJsonAsync("/api/auth/verify-email", new { token });
        ((int)res.StatusCode).Should().BeInRange(200, 299,
            "the emailed token must be accepted: {0}", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_verification_link_cannot_be_used_twice()
    {
        RequireDatabase();

        var (client, _, _) = await RegisterAsync("verify-twice");
        var token = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Confirm")), "/verify-email"));

        using var first = await client.PostAsJsonAsync("/api/auth/verify-email", new { token });
        ((int)first.StatusCode).Should().BeInRange(200, 299);

        using var second = await client.PostAsJsonAsync("/api/auth/verify-email", new { token });
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a single-use link must not keep working");
    }

    /* ── password reset ──────────────────────────────────────────────── */

    [Fact]
    public async Task Requesting_a_reset_sends_a_link_that_actually_resets()
    {
        // The whole flow, end to end: ask, read the email, follow the link,
        // sign in with the new password. Every step in between has been wrong
        // at some point in some codebase.
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("reset");

        Api.Email.Clear();
        using var ask = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        ((int)ask.StatusCode).Should().BeInRange(200, 299);

        Api.Email.Sent.Should().ContainSingle(
            m => m.To.Contains(email) && m.Subject.Contains("Reset"),
            "asking for a reset should send exactly one email to that address");

        var token = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Reset")), "/reset-password"));

        const string NewPassword = "BrandNewPassword-456!";

        using var reset = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token, password = NewPassword });
        ((int)reset.StatusCode).Should().BeInRange(200, 299,
            "the reset must succeed: {0}", await reset.Content.ReadAsStringAsync());

        // The new password works.
        var fresh = Api.NewClient();
        using var login = await fresh.PostAsJsonAsync(
            "/api/auth/login", new { email, password = NewPassword });
        login.StatusCode.Should().Be(HttpStatusCode.OK, "the new password must be usable");

        // And the old one does not.
        using var old = await fresh.PostAsJsonAsync(
            "/api/auth/login", new { email, password = Password });
        old.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the old password must stop working");
    }

    [Fact]
    public async Task Resetting_signs_the_account_out_everywhere()
    {
        // The reason to reset is usually that somebody else has the password.
        // If existing refresh tokens survive, that person keeps access for up
        // to thirty days and the reset achieves nothing.
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("reset-sessions");

        // A second, independent session — the "intruder".
        var other = Api.NewClient();
        using var otherLogin = await other.PostAsJsonAsync(
            "/api/auth/login", new { email, password = Password });
        var otherBody = await otherLogin.Content.ReadFromJsonAsync<JsonElement>(Json);
        var otherRefresh = otherBody.GetProperty("refreshToken").GetString()!;

        Api.Email.Clear();
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var token = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Reset")), "/reset-password"));

        using var reset = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token, password = "AnotherPassword-789!" });
        ((int)reset.StatusCode).Should().BeInRange(200, 299);

        using var stillAlive = await other.PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = otherRefresh });
        stillAlive.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "every session must end when the password is reset, including one held " +
            "by whoever prompted the reset");
    }

    [Fact]
    public async Task A_completed_reset_notifies_the_account()
    {
        // The only signal the real owner gets if an intruder did the resetting.
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("reset-notice");

        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var token = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Reset")), "/reset-password"));

        Api.Email.Clear();
        await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token, password = "YetAnother-321!" });

        Api.Email.Sent.Should().Contain(
            m => m.To.Contains(email) && m.Subject.Contains("changed"),
            "a password change must be announced to the address on the account");
    }

    [Fact]
    public async Task A_reset_link_cannot_be_used_twice()
    {
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("reset-twice");

        Api.Email.Clear();
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var token = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Reset")), "/reset-password"));

        using var first = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token, password = "FirstChange-111!" });
        ((int)first.StatusCode).Should().BeInRange(200, 299);

        using var second = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token, password = "SecondChange-222!" });
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a spent link must not still be live in an inbox");
    }

    [Fact]
    public async Task Requesting_a_second_reset_kills_the_first_link()
    {
        // Otherwise asking twice leaves two working links, and the older email
        // — the one more likely to have been forwarded — still opens the door.
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("reset-supersede");

        Api.Email.Clear();
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var firstToken = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Reset")), "/reset-password"));

        Api.Email.Clear();
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var secondToken = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Reset")), "/reset-password"));

        firstToken.Should().NotBe(secondToken, "a new request must issue a new token");

        using var stale = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token = firstToken, password = "StaleAttempt-333!" });
        stale.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the superseded link must stop working");

        using var current = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token = secondToken, password = "CurrentOne-444!" });
        ((int)current.StatusCode).Should().BeInRange(200, 299,
            "the newest link must still work");
    }

    [Fact]
    public async Task An_unknown_address_is_answered_the_same_way_and_sends_nothing()
    {
        // Account enumeration. An endpoint that distinguishes "sent" from "no
        // such account" sorts a breach dump into customers and non-customers.
        RequireDatabase();

        var (client, realEmail, _) = await RegisterAsync("enumeration");

        Api.Email.Clear();
        using var known = await client.PostAsJsonAsync(
            "/api/auth/forgot-password", new { email = realEmail });
        var knownBody = await known.Content.ReadAsStringAsync();

        Api.Email.Clear();
        using var unknown = await client.PostAsJsonAsync(
            "/api/auth/forgot-password",
            new { email = $"nobody-{Guid.NewGuid():N}@example.test" });
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        unknown.StatusCode.Should().Be(known.StatusCode,
            "the status code must not reveal whether the address exists");
        unknownBody.Should().Be(knownBody,
            "the body must not reveal whether the address exists");

        Api.Email.Sent.Should().BeEmpty(
            "nothing should be sent to an address with no account");
    }

    [Fact]
    public async Task A_rubbish_reset_token_is_refused()
    {
        RequireDatabase();

        var client = Api.NewClient();
        using var res = await client.PostAsJsonAsync(
            "/api/auth/reset-password",
            new { token = "not-a-token-anyone-ever-issued", password = "Whatever-555!" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_reset_will_not_accept_a_weak_password()
    {
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("reset-weak");

        Api.Email.Clear();
        await client.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        var token = TokenFrom(LinkFrom(
            Api.Email.Sent.First(m => m.Subject.Contains("Reset")), "/reset-password"));

        using var res = await client.PostAsJsonAsync(
            "/api/auth/reset-password", new { token, password = "short" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the reset path must enforce the same minimum as registration");
    }

    [Fact]
    public async Task Resending_verification_requires_a_session()
    {
        // Anonymous, this would be a way to make the product send mail to any
        // address on request.
        RequireDatabase();

        var client = Api.NewClient();
        Bearer(client, null);

        using var res = await client.PostAsync("/api/auth/resend-verification", null);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /* ── reported from production: "reset emails are not arriving" ───── */

    [Fact]
    public async Task An_invited_pharmacist_who_asks_for_a_reset_gets_their_activation_link()
    {
        // Imported and invited accounts have no password yet, so "forgot my
        // password" is what they try. It used to send nothing — silently,
        // because the account cannot sign in — which from outside is exactly
        // "email is broken".
        RequireDatabase();

        var admin = await Api.SignInSuperAdminAsync();
        var email = $"invited-reset-{Guid.NewGuid().ToString("N")[..8]}@example.test";
        using (var invited = await admin.PostAsJsonAsync("/api/admin/invitations", new
        {
            fullName = "Invited Reset", email, pharmacyName = "Invited Reset Pharmacy",
        }))
        {
            invited.IsSuccessStatusCode.Should().BeTrue(await invited.Content.ReadAsStringAsync());
        }

        Api.Email.Clear();
        var anon = Api.NewClient();
        using var ask = await anon.PostAsJsonAsync("/api/auth/forgot-password", new { email });
        ask.StatusCode.Should().Be(HttpStatusCode.Accepted, "the answer is the same as for anybody");

        var sent = Api.Email.Sent.Where(m => m.To.Contains(email)).ToList();
        sent.Should().ContainSingle("one email, to the invited address");
        sent[0].Subject.Should().Contain("Activate", "an account with no password needs its activation link, not a reset");

        // And the link in it opens the account.
        var token = TokenFrom(LinkFrom(sent[0], "/activate"));
        using var preview = await anon.GetAsync($"/api/invitations/preview?token={Uri.EscapeDataString(token)}");
        preview.StatusCode.Should().Be(HttpStatusCode.OK, "the reissued link must be live");
    }

    [Fact]
    public async Task The_account_can_see_whether_its_reset_email_was_accepted()
    {
        // What the smoke test reads after requesting a reset for its own
        // account: did the provider take the email, and where does its link go?
        RequireDatabase();

        var (client, email, _) = await RegisterAsync("reset-status");

        using (var none = await client.GetAsync("/api/auth/account-email/latest?purpose=PasswordReset"))
            none.StatusCode.Should().Be(HttpStatusCode.NoContent, "no reset has been asked for yet");

        using (var ask = await client.PostAsJsonAsync("/api/auth/forgot-password", new { email }))
            ask.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var status = await client.GetFromJsonAsync<JsonElement>(
            "/api/auth/account-email/latest?purpose=PasswordReset", Json);
        status.GetProperty("outcome").GetString().Should().Be("accepted");
        status.GetProperty("linkHost").GetString().Should().NotBeNullOrWhiteSpace();
        status.GetProperty("requestedAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task Email_status_is_only_for_a_signed_in_account()
    {
        RequireDatabase();

        using var res = await Api.NewClient().GetAsync("/api/auth/account-email/latest");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "anonymous, this would say whether an address had asked for a reset");
    }
}
