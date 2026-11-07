using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acme.Application.Campaigns;
using Acme.Application.Messaging;
using FluentAssertions;
using Xunit;

namespace Acme.IntegrationTests;

/// <summary>
/// The email a campaign actually sends, inspected as the recipient would get it.
/// </summary>
/// <remarks>
/// <para>
/// <c>CampaignEmailTests</c> in the unit suite covers <c>CampaignEmail.BuildHtml</c>
/// — given a poster and some videos, what markup comes out. It cannot cover the
/// part that broke: whether the running application reaches that builder with a
/// poster at all, and whether the image survives the trip from the rasterizer,
/// through the blob store, onto the <see cref="EmailMessage"/>.
/// </para>
/// <para>
/// Every one of those seams has failed before. The poster used to leave as a PDF
/// attachment; the change to an inline image touched the service, both senders
/// and the message record, and a builder test would have passed throughout.
/// </para>
/// </remarks>
public sealed class CampaignEmailDeliveryTests(ApiFactory api) : IntegrationTest(api)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Gmail truncates a message body past roughly 102KB.</summary>
    private const int GmailClipLimitBytes = 102 * 1024;

    /// <summary>
    /// Registers a paid pharmacy, gives it a poster, and sends one campaign.
    /// </summary>
    private async Task<EmailMessage> SendCampaignAsync(string label, string recipient)
    {
        var (client, _, userId) = await Api.RegisterPharmacyAsync(label);

        // Campaigns are a paid feature; without this the send is a 403 and the
        // captured mailbox is simply empty.
        await Api.UpgradeToProAsync(userId);

        var posterId = await PublishedPosterAsync(client, label);

        Api.Email.Clear();

        using var res = await client.PostAsJsonAsync("/api/campaigns", new
        {
            posterId,
            name = $"Campaign {label}",
            channel = "Email",
            recipients = new[] { recipient },
        });

        res.IsSuccessStatusCode.Should().BeTrue(
            "the campaign must be accepted: {0}", await res.Content.ReadAsStringAsync());

        // Creating a campaign no longer sends it. The request queues it and
        // returns; a background worker claims it and sends. That is what makes a
        // thousand-recipient campaign possible — the send used to hold the HTTP
        // request open until the last recipient, and Azure App Service cuts an
        // idle request at 230 seconds.
        //
        // So a test that asserted on the mailbox immediately after the POST was
        // not flaky; it was testing a contract that had changed underneath it.
        await Api.WaitForCampaignDispatchAsync();

        Api.Email.Sent.Should().HaveCount(1, "one campaign to one recipient is one email");
        return Api.Email.Last;
    }

    /* ── who the email says it is from ───────────────────────────────── */

    [Fact]
    public async Task A_pharmacy_with_no_profile_name_sends_under_its_brand_kit_name()
    {
        RequireDatabase();

        // Note what RegisterPharmacyAsync produces: an account with a display
        // name and an empty pharmacy profile — exactly the state of somebody
        // who skipped onboarding. Every other test in this file has been
        // sending patient mail under "Integration <label>", a person's name,
        // without anybody noticing.
        var (client, _, userId) = await Api.RegisterPharmacyAsync("sender-name");
        await Api.UpgradeToProAsync(userId);

        const string PharmacyName = "Brand Kit Pharmacy";
        using (var kit = await client.PutAsJsonAsync("/api/brand-kit", new
        {
            name = "Kit",
            colors = new { primary = "#0F766E", secondary = "#134E4A", accent = "#F59E0B" },
            pharmacy = new
            {
                name = PharmacyName,
                licenseNumber = "FJ123",
                phone = "0161 000 0000",
                address = "1 Test Street, Manchester",
            },
            regulatoryFooter = "",
        }))
        {
            kit.IsSuccessStatusCode.Should().BeTrue(
                "the brand kit must save: {0}", await kit.Content.ReadAsStringAsync());
        }

        var posterId = await PublishedPosterAsync(client, "sender-name");

        Api.Email.Clear();
        using var res = await client.PostAsJsonAsync("/api/campaigns", new
        {
            posterId,
            name = "Sender name campaign",
            channel = "Email",
            recipients = new[] { "sender-name@example.test" },
        });
        res.IsSuccessStatusCode.Should().BeTrue(
            "the campaign must be accepted: {0}", await res.Content.ReadAsStringAsync());

        // Queued, not sent. See ApiFactory.WaitForCampaignDispatchAsync.
        await Api.WaitForCampaignDispatchAsync();

        var mail = Api.Email.Last;

        mail.FromName.Should().Be(PharmacyName,
            "a patient has no idea who the account holder is, and mail from an "
            + "unrecognised personal name is mail that gets reported as spam");

        mail.FromName.Should().NotContain("Integration",
            "the account holder's own display name must never reach a patient");

        mail.Subject.Should().Contain(PharmacyName);
        mail.HtmlBody.Should().Contain(PharmacyName,
            "the body says who it is from, twice — the header and the footer");
        mail.TextBody.Should().Contain(PharmacyName);
    }

    [Fact]
    public async Task The_profile_name_still_wins_over_the_brand_kit()
    {
        RequireDatabase();

        // The brand kit is a fallback, not a new source of truth. Onboarding is
        // still where the pharmacy names itself, and a kit left over from an
        // earlier name must not quietly override it.
        var (client, _, userId) = await Api.RegisterPharmacyAsync("profile-wins");

        // Onboarding first, and with no tier. OnboardRequest carries an
        // optional SubscriptionTier, so onboarding after the upgrade — or
        // passing "Free" here — would put the account back on the Free plan,
        // where campaigns are refused outright and this test would fail for a
        // reason that has nothing to do with names.
        using (var onboard = await client.PostAsJsonAsync("/api/me/onboard", new
        {
            pharmacyName = "Profile Pharmacy",
            address = "2 Test Street, Leeds",
            postCode = "LS1 1AA",
            description = "",
            contactName = "Integration Tester",
            contactPhone = "0113 000 0000",
            sonarFCode = "FJ456",
        }))
        {
            onboard.IsSuccessStatusCode.Should().BeTrue(
                "onboarding must succeed: {0}", await onboard.Content.ReadAsStringAsync());
        }

        await Api.UpgradeToProAsync(userId);

        using (var kit = await client.PutAsJsonAsync("/api/brand-kit", new
        {
            name = "Kit",
            colors = new { primary = "#0F766E", secondary = "#134E4A", accent = "#F59E0B" },
            pharmacy = new
            {
                name = "Stale Kit Pharmacy",
                licenseNumber = "FJ456",
                phone = "0113 000 0000",
                address = "2 Test Street, Leeds",
            },
            regulatoryFooter = "",
        }))
        {
            kit.IsSuccessStatusCode.Should().BeTrue();
        }

        var posterId = await PublishedPosterAsync(client, "profile-wins");

        Api.Email.Clear();
        using var res = await client.PostAsJsonAsync("/api/campaigns", new
        {
            posterId,
            name = "Profile wins campaign",
            channel = "Email",
            recipients = new[] { "profile-wins@example.test" },
        });
        res.IsSuccessStatusCode.Should().BeTrue(
            "the campaign must be accepted: {0}", await res.Content.ReadAsStringAsync());

        await Api.WaitForCampaignDispatchAsync();

        Api.Email.Last.FromName.Should().Be("Profile Pharmacy",
            "the brand kit is only consulted when the profile has no name");
    }

    /// <summary>Creates a poster and publishes it, returning its id.</summary>
    private static async Task<string> PublishedPosterAsync(HttpClient client, string label)
    {
        using var poster = await client.PostAsJsonAsync(
            "/api/posters", new { name = $"Poster for the {label} email" });
        poster.IsSuccessStatusCode.Should().BeTrue(
            "the campaign needs a poster: {0}", await poster.Content.ReadAsStringAsync());

        var id = (await poster.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        using var published = await client.PatchAsJsonAsync(
            $"/api/posters/{id}/status", new { status = "Published" });
        published.IsSuccessStatusCode.Should().BeTrue(
            "the poster must be published before it can be sent: {0}",
            await published.Content.ReadAsStringAsync());

        return id;
    }

    /// <summary>The poster address out of a built email, or a failure saying so.</summary>
    private static string PosterUrlFrom(EmailMessage mail)
    {
        var match = Regex.Match(mail.HtmlBody, @"src=""([^""]*?/api/posters/email-image\?[^""]+)""");
        match.Success.Should().BeTrue(
            "the body must serve the design from a link. Body was:\n{0}",
            Truncate(mail.HtmlBody, 800));

        // The builder writes it into HTML, so `&` arrives as `&amp;`.
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    [Fact]
    public async Task The_poster_is_linked_rather_than_carried_in_the_message()
    {
        // <b>The assertion that decides whether a campaign can be sent at all.</b>
        //
        // Mailjet's Send v3.1 copies an inline attachment into every recipient's
        // personalisation. Measured on a real failure: 977,045 bytes for four
        // recipients, 244 KB each, eighty seconds of uploading, and the provider
        // closing the connection before the request finished. A thousand
        // recipients would have been a quarter of a gigabyte.
        //
        // So an inline image here is not a cosmetic regression. It is the
        // difference between a campaign that goes out and one that times out.
        RequireDatabase();

        var mail = await SendCampaignAsync("linked", "linked-poster@example.test");

        (mail.InlineImages ?? Array.Empty<EmailInlineImage>()).Should().BeEmpty(
            "nothing binary may travel per recipient");

        PosterUrlFrom(mail).Should().Contain("token=",
            "the address has to carry its own authorisation — the reader is a patient "
            + "with no account");
    }

    [Fact]
    public async Task The_link_in_the_email_serves_the_poster_to_someone_with_no_account()
    {
        // End to end, and the part that could not be checked while the image was
        // embedded: the token is signed here, validated in a different project,
        // and read by somebody holding no cookie. A broken signature, a wrong
        // base URL or a route that quietly requires auth all look identical from
        // inside the sender — and identical to the recipient too, as an empty
        // box where the design should be.
        //
        // Fetched by path rather than by the absolute URL in the email: the test
        // host is in-process and has no real address. What that does not cover
        // is Public:ApiBaseUrl being wrong in a deployment, which is a
        // configuration fault rather than a code one.
        RequireDatabase();

        var mail = await SendCampaignAsync("fetch", "fetch-poster@example.test");
        var url = PosterUrlFrom(mail);
        var pathAndQuery = new Uri(url).PathAndQuery;

        // Deliberately anonymous. NewClient carries no Authorization header.
        using var stranger = Api.NewClient();
        using var res = await stranger.GetAsync(pathAndQuery);

        res.IsSuccessStatusCode.Should().BeTrue(
            "a patient opening the email is not signed in: {0} {1}",
            (int)res.StatusCode, await res.Content.ReadAsStringAsync());

        var mime = res.Content.Headers.ContentType?.MediaType;
        mime.Should().BeOneOf("image/jpeg", "image/png");

        var bytes = await res.Content.ReadAsByteArrayAsync();
        bytes.Should().HaveCountGreaterThan(1000, "a rendered A4 poster is not a few bytes");

        // The bytes must be what they are declared as. A truncated or empty blob
        // still returns 200 with the right content type and shows a broken image.
        if (mime == "image/png")
        {
            bytes.Take(8).Should().Equal(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                "declared image/png, so it must start with the PNG signature");
        }
        else
        {
            bytes.Take(3).Should().Equal(
                new byte[] { 0xFF, 0xD8, 0xFF },
                "declared image/jpeg, so it must start with the JPEG SOI marker");
        }
    }

    [Fact]
    public async Task A_tampered_or_missing_token_gets_nothing()
    {
        // The route is anonymous, so the signature is the only thing between a
        // stranger and every rendered poster on the platform.
        RequireDatabase();

        var mail = await SendCampaignAsync("tamper", "tamper-poster@example.test");
        var url = PosterUrlFrom(mail);
        var pathAndQuery = new Uri(url).PathAndQuery;

        using var stranger = Api.NewClient();

        foreach (var (label, target) in new[]
        {
            ("no token", "/api/posters/email-image"),
            ("empty token", "/api/posters/email-image?token="),
            ("a made-up token", "/api/posters/email-image?token=not.a.token"),
            // One character of the signature changed: the payload still names a
            // real blob, and only the signature is wrong. This is the case a
            // naive implementation passes by reading the blob key and never
            // checking.
            ("a token with one character changed", Tamper(pathAndQuery)),
        })
        {
            using var res = await stranger.GetAsync(target);
            res.IsSuccessStatusCode.Should().BeFalse(
                "{0} must not return a poster", label);
        }

        // Changes the FIRST character of the signature, never the last.
        //
        // This used to flip the last character of the URL, and was flaky at
        // exactly 6.25%. An HMAC-SHA256 signature is 32 bytes — 256 bits — and
        // base64url encodes it in 43 characters, which carry 258. The last
        // character therefore holds 4 significant bits and 2 that decode to
        // nothing, so its 64 possible values collapse to 16 distinct byte
        // strings: four different characters all decode to the same signature.
        //
        // The old rule was `last == 'A' ? 'B' : 'A'`. For a token ending in A,
        // B, C or D that produced a different string carrying identical bytes —
        // a valid signature, a 200, and a failure report claiming the tampered
        // token had been accepted. Four values in sixty-four is one run in
        // sixteen: frequent enough to see, rare enough to re-run and forget.
        //
        // Every character except the last carries all six of its bits, so
        // changing the first one always changes the decoded signature. The
        // substitution stays inside the base64url alphabet on purpose: a token
        // that fails to decode would be rejected as malformed and would prove
        // nothing about whether the signature is checked.
        static string Tamper(string pathAndQuery)
        {
            var dot = pathAndQuery.IndexOf('.');
            dot.Should().BeGreaterThan(0,
                "the token is payload.signature, and this helper needs the separator");

            var at = dot + 1;
            at.Should().BeLessThan(pathAndQuery.Length - 1,
                "there must be a signature after the dot with room to change it "
                + "somewhere other than its final character");

            var tampered = pathAndQuery[..at]
                + (pathAndQuery[at] == 'A' ? 'B' : 'A')
                + pathAndQuery[(at + 1)..];

            tampered.Should().NotBe(pathAndQuery, "otherwise nothing was tampered with");
            return tampered;
        }
    }

    [Fact]
    public async Task The_message_stays_small_enough_that_the_audience_does_not_matter()
    {
        // The point of the whole change, as a number.
        //
        // What the provider receives is roughly this, once per recipient. At 244
        // KB — an embedded poster — a thousand recipients is 244 MB and four
        // recipients was enough to time out. Under 32 KB, a thousand recipients
        // is a few tens of megabytes and the send is bounded by rate limits
        // rather than by bandwidth.
        //
        // A ceiling, not a target. A built message is around 3.7 KB, so an
        // ordinary copy change cannot trip this, and re-embedding a poster —
        // 244 KB — trips it immediately.
        //
        // <b>This campaign has no videos, and that matters.</b> Video thumbnails
        // are still base64 data URLs in the HTML, so they are still copied per
        // recipient: five of them would be about 100 KB each time and would fail
        // this assertion. That is the remaining per-recipient cost, it is an
        // order of magnitude smaller than the poster was, and the fix is the
        // same one — serve them from a signed URL.
        RequireDatabase();

        var mail = await SendCampaignAsync("size", "message-size@example.test");

        var perRecipient =
            Encoding.UTF8.GetByteCount(mail.HtmlBody)
            + Encoding.UTF8.GetByteCount(mail.TextBody ?? "")
            + (mail.InlineImages ?? Array.Empty<EmailInlineImage>()).Sum(i => i.Content.Length)
            + mail.Attachments.Sum(a => a.Content.Length);

        perRecipient.Should().BeLessThan(32 * 1024,
            "this message is {0:N0} bytes and the mail API repeats it per recipient. "
            + "This is the figure that decides whether a campaign to a real audience "
            + "fits down the wire", perRecipient);
    }

    [Fact]
    public async Task No_pdf_is_attached_any_more()
    {
        // The behaviour the change was made to remove. An email carrying both an
        // inline design and the old PDF would look right in a test that only
        // checked the image, and would arrive with a redundant paperclip.
        RequireDatabase();

        var mail = await SendCampaignAsync("nopdf", "no-attachment@example.test");

        mail.Attachments.Should().BeEmpty(
            "the poster is in the body now; nothing should remain in the attachment list");
    }

    [Fact]
    public async Task A_text_alternative_is_sent()
    {
        // Mail clients with images off, screen readers, and spam scoring all
        // read this part. An HTML-only campaign scores as spam.
        RequireDatabase();

        var mail = await SendCampaignAsync("text", "text-part@example.test");

        mail.TextBody.Should().NotBeNullOrWhiteSpace(
            "an HTML-only message reads as empty when images are blocked");
    }

    [Fact]
    public async Task The_body_is_small_enough_that_gmail_will_not_clip_it()
    {
        // The reason the poster is a CID attachment rather than a data URL, and
        // the regression that would undo it. Inlining the same PNG as base64 is
        // the obvious "simplification" and pushes a normal poster past Gmail's
        // limit — the message is cut off mid-design, in the client most
        // recipients use, and nothing about the code looks wrong.
        RequireDatabase();

        var mail = await SendCampaignAsync("size", "body-size@example.test");

        Encoding.UTF8.GetByteCount(mail.HtmlBody).Should().BeLessThan(
            GmailClipLimitBytes,
            "Gmail replaces the rest of a body over ~102KB with '[Message clipped]'");

        mail.HtmlBody.Should().NotContain(
            "data:image/png;base64",
            "the poster must travel as a separate MIME part, not inside the HTML");
    }

    [Fact]
    public async Task Re_sending_the_same_design_does_not_rasterise_it_twice()
    {
        // EnsureRenderedAsync claims to be cached by canvas content. Rasterising
        // is the slowest thing a send does, so a broken cache key turns a
        // campaign to a large list into a timeout rather than an error.
        RequireDatabase();

        var (client, _, userId) = await Api.RegisterPharmacyAsync("cache");
        await Api.UpgradeToProAsync(userId);

        using var poster = await client.PostAsJsonAsync(
            "/api/posters", new { name = "Poster sent twice" });
        var posterId = (await poster.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("id").GetString()!;

        // A draft cannot be sent, so publish before either send.
        using var published = await client.PatchAsJsonAsync(
            $"/api/posters/{posterId}/status", new { status = "Published" });
        published.IsSuccessStatusCode.Should().BeTrue();

        async Task SendAsync(string name)
        {
            using var res = await client.PostAsJsonAsync("/api/campaigns", new
            {
                posterId,
                name,
                channel = "Email",
                recipients = new[] { "cache-probe@example.test" },
            });
            res.IsSuccessStatusCode.Should().BeTrue(
                "{0} must be accepted: {1}", name, await res.Content.ReadAsStringAsync());

            // Waiting matters more here than anywhere else in this file.
            // Rendering happens in the dispatch worker now, so without it
            // Api.PosterImages.Count would be read before either send had
            // rendered anything — and the test would compare nought to nought
            // and pass, having stopped checking the cache entirely. A test that
            // fails is a nuisance; one that passes vacuously is a liability.
            await Api.WaitForCampaignDispatchAsync();
        }

        await SendAsync("First send");
        var afterFirst = Api.PosterImages.Count;

        await SendAsync("Second send");

        Api.PosterImages.Count.Should().Be(
            afterFirst,
            "the canvas did not change, so the second send must re-use the stored render");
    }
}
