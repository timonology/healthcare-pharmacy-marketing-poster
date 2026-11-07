using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Application.Messaging;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The invitation email — the one that was silently undeliverable.
/// </summary>
/// <remarks>
/// <para>
/// This path had no test. The only invitation test in the suite parsed a CSV,
/// so nothing ever looked at the message <c>InvitationService</c> builds. That
/// is how it shipped naming <c>no-reply@pharmacyposter.app</c> as its sender,
/// overriding the configured and provider-verified address, and being rejected
/// by Mailjet — in a way no exception, log line or test could see, because the
/// refusal happened at the provider.
/// </para>
/// <para>
/// The account was still created and the token still issued on every attempt.
/// From inside the product an invitation looked sent; from the pharmacist's
/// side nothing ever arrived.
/// </para>
/// </remarks>
public sealed class InvitationEmailTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Invites a pharmacist as SuperAdmin and returns the captured email.</summary>
    private async Task<(EmailMessage Message, string Email)> InviteAsync(string label)
    {
        var admin = await Api.SignInSuperAdminAsync();
        var email = $"invite-{label}-{Guid.NewGuid().ToString("N")[..8]}@example.test";

        Api.Email.Clear();

        using var res = await admin.PostAsJsonAsync("/api/admin/invitations", new
        {
            fullName = $"Invited {label}",
            email,
            pharmacyName = "Invited Pharmacy",
        });

        res.IsSuccessStatusCode.Should().BeTrue(
            "the invitation must be created: {0}", await res.Content.ReadAsStringAsync());

        Api.Email.Sent.Should().ContainSingle(
            m => m.To.Contains(email),
            "inviting a pharmacist must email them — this is the entire point of the " +
            "feature, and the part that was broken");

        return (Api.Email.Sent.First(m => m.To.Contains(email)), email);
    }

    [Fact]
    public async Task Inviting_a_pharmacist_emails_them()
    {
        RequireDatabase();

        var (message, email) = await InviteAsync("basic");

        message.To.Should().Contain(email);
        message.Subject.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_invitation_does_not_name_its_own_sender()
    {
        // The regression itself. A non-empty FromAddress overrides the
        // configured, verified one and the provider refuses the message.
        RequireDatabase();

        var (message, _) = await InviteAsync("sender");

        message.FromAddress.Should().BeNullOrEmpty(
            "the message must inherit the configured sender. A literal address here " +
            "is rejected by Mailjet as an unverified domain, and nothing inside the " +
            "application ever sees the refusal");

        message.HtmlBody.Should().NotContain(
            "pharmacyposter.app",
            "this domain is not verified with the provider");
    }

    [Fact]
    public async Task The_invitation_carries_a_working_activation_link()
    {
        // "An email was sent" proves less than it looks like. The link inside
        // can point at the wrong environment, arrive already expired, or be
        // missing its token entirely.
        RequireDatabase();

        var (message, _) = await InviteAsync("link");

        var body = message.TextBody ?? message.HtmlBody;
        var link = Regex.Match(body, @"https?://[^\s]*/activate[^\s]*");

        link.Success.Should().BeTrue(
            "the email must contain an activation link. Body was:\n{0}", body);

        var token = Uri.UnescapeDataString(
            Regex.Match(link.Value, @"token=([^&\s]+)").Groups[1].Value);

        token.Should().NotBeNullOrWhiteSpace("the link must carry a token");

        // The token has to be redeemable, not merely present.
        var anon = Api.NewClient();
        using var preview = await anon.GetAsync($"/api/invitations/preview?token={Uri.EscapeDataString(token)}");

        preview.IsSuccessStatusCode.Should().BeTrue(
            "the emailed token must be accepted by the activation endpoint: {0}",
            await preview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_invitation_has_a_text_part()
    {
        // Pharmacy mail is often read through locked-down clients that strip
        // HTML. An activation email that renders blank is an account nobody
        // can claim.
        RequireDatabase();

        var (message, _) = await InviteAsync("textpart");

        message.TextBody.Should().NotBeNullOrWhiteSpace();
        message.TextBody.Should().Contain("/activate",
            "the link must survive in the plain-text part, not only in the HTML");
    }

    [Fact]
    public async Task Resending_supersedes_the_previous_link()
    {
        // Resending is how a SuperAdmin rescues an invitation that never
        // arrived. If the old link stayed live alongside the new one, revoking
        // access by resending would not revoke anything.
        RequireDatabase();

        var admin = await Api.SignInSuperAdminAsync();
        var email = $"invite-resend-{Guid.NewGuid().ToString("N")[..8]}@example.test";

        Api.Email.Clear();
        using var created = await admin.PostAsJsonAsync("/api/admin/invitations", new
        {
            fullName = "Resend Target",
            email,
            pharmacyName = "Resend Pharmacy",
        });
        created.IsSuccessStatusCode.Should().BeTrue();

        var firstToken = TokenFrom(Api.Email.Sent.First(m => m.To.Contains(email)));

        // Find the account so it can be told to resend.
        using var lookup = await admin.GetAsync(
            $"/api/admin/users?search={Uri.EscapeDataString(email)}&take=1");
        var page = await lookup.Content.ReadFromJsonAsync<JsonElement>(Json);
        var items = page.TryGetProperty("items", out var i) ? i : page;
        var userId = items[0].GetProperty("id").GetString()!;

        Api.Email.Clear();
        using var resent = await admin.PostAsync($"/api/admin/invitations/{userId}/resend", null);
        resent.IsSuccessStatusCode.Should().BeTrue(
            "resending must work: {0}", await resent.Content.ReadAsStringAsync());

        Api.Email.Sent.Should().ContainSingle(
            m => m.To.Contains(email), "resending must send another email");

        var secondToken = TokenFrom(Api.Email.Sent.First(m => m.To.Contains(email)));
        secondToken.Should().NotBe(firstToken, "a resend must issue a new token");

        var anon = Api.NewClient();

        using var stale = await anon.GetAsync(
            $"/api/invitations/preview?token={Uri.EscapeDataString(firstToken)}");
        stale.IsSuccessStatusCode.Should().BeFalse(
            "the superseded link must stop working, or resending revokes nothing");

        using var current = await anon.GetAsync(
            $"/api/invitations/preview?token={Uri.EscapeDataString(secondToken)}");
        current.IsSuccessStatusCode.Should().BeTrue("the newest link must work");
    }

    private static string TokenFrom(EmailMessage message)
    {
        var body = message.TextBody ?? message.HtmlBody;
        var link = Regex.Match(body, @"https?://[^\s]*/activate[^\s]*");
        link.Success.Should().BeTrue("the email must contain an activation link");
        return Uri.UnescapeDataString(
            Regex.Match(link.Value, @"token=([^&\s]+)").Groups[1].Value);
    }
}
