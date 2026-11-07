using System.Text;
using Acme.Application.Auth;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Acme.Application.Messaging;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Domain.Patients;
using Acme.Domain.Users;
using Acme.Infrastructure.Imaging;
using Acme.Infrastructure.Security;
using Acme.WebApi;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Acme.Tests.Security;

/// <summary>
/// The fixes from the follow-up security review
/// (docs/SECURITY_REVIEW_2026-10-03_FOLLOWUP.pdf), one region per finding.
/// </summary>
public class SecurityFollowupFixTests
{
    // ── R2-05 (a): the canvas is judged as the renderer reads it ────────────

    [Theory]
    [InlineData("""{"shapes":[{"BlobKey":"http://169.254.169.254/x"}]}""")]
    [InlineData("""{"shapes":[{"BLOBKEY":"brand/logos/other/logo.png"}]}""")]
    [InlineData("""{"shapes":[{"blob\u004bey":"http://10.0.0.1/x.png"}]}""")]   // a JSON escape, still "blobKey"
    [InlineData("""{"shapes":[{"blobKey":"/api/brand-kit/logo","BlobKey":"http://10.0.0.1/"}]}""")]
    [InlineData("""{"pages":[{"layers":[{"blobKey":"http://10.0.0.1/deep.png"}]}]}""")]
    [InlineData("""{"shapes":[{"blobKey":{"url":"http://10.0.0.1/"}}]}""")]
    public void Every_spelling_and_place_of_an_image_source_is_judged(string canvas) =>
        CanvasImageSources.WhyRefused(canvas).Should().NotBeNull();

    [Fact]
    public void A_property_named_twice_is_refused_rather_than_guessed() =>
        CanvasImageSources.WhyRefused("""{"shapes":[{"blobKey":"/api/brand-kit/logo","blobKey":"http://10.0.0.1/"}]}""")
            .Should().NotBeNull();

    [Fact]
    public void Rendering_blanks_a_disallowed_source_whatever_its_case()
    {
        var stripped = CanvasImageSources.StripDisallowed(
            """{"shapes":[{"BlobKey":"http://169.254.169.254/x"},{"blobKey":"/api/brand-kit/logo"}]}""");

        stripped.Should().NotContain("169.254").And.Contain("/api/brand-kit/logo");
        CanvasImageSources.WhyRefused(stripped).Should().BeNull();
    }

    [Fact]
    public void An_allowed_canvas_is_rendered_exactly_as_stored()
    {
        const string canvas = """{"shapes":[{"blobKey":"/api/brand-kit/logo"}]}""";
        CanvasImageSources.StripDisallowed(canvas).Should().BeSameAs(canvas);
    }

    // ── R2-05 (b): no nested or escaped references in an SVG ────────────────

    [Theory]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="data:image/svg+xml;base64,PHN2Zz48aW1hZ2UgaHJlZj0iaHR0cDovLzEwLjAuMC4xLyIvPjwvc3ZnPg=="/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect style="fill:u\72l(http://10.0.0.1/a)"/></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><style>@\69mport "http://10.0.0.1/x.css";</style></svg>""")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><image href="&#104;ttp://10.0.0.1/"/></svg>""")]
    public void An_svg_that_could_reach_outside_itself_is_refused(string svg) =>
        SvgRasterizer.IsSelfContained(SvgRasterizer.WithoutDoctype(svg)).Should().BeFalse();

    [Fact]
    public void An_svg_with_an_inline_raster_picture_is_allowed() =>
        SvgRasterizer.IsSelfContained(
            """<svg xmlns="http://www.w3.org/2000/svg"><image href="data:image/png;base64,iVBORw0KGgo="/></svg>""")
            .Should().BeTrue();

    [Fact]
    public void An_svg_logo_wrapping_an_embedded_png_still_rasterises()
    {
        // Turning off the SVG library's external loading (R2-05) must not
        // also drop embedded pictures: logos exported from design tools are
        // often an SVG around a PNG, and would otherwise upload as a blank.
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAIAAAAmkwkpAAAAEElEQVR4nGP8z4AATAxEcQAz0QEHOoQ+uAAAAABJRU5ErkJggg==";
        var svg = $$"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="40" height="40"><image xlink:href="data:image/png;base64,{{png}}" width="40" height="40"/></svg>""";

        var bytes = SvgRasterizer.ToPng(Encoding.UTF8.GetBytes(svg));

        bytes.Should().NotBeNull();
        using var bitmap = SkiaSharp.SKBitmap.Decode(bytes!);
        var centre = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        centre.Red.Should().BeGreaterThan(200, "the embedded picture must be drawn");
        centre.Alpha.Should().BeGreaterThan(200);
    }

    // ── R2-12: canvas size ───────────────────────────────────────────────────

    [Fact]
    public void A_canvas_over_the_byte_limit_is_refused_with_a_reason()
    {
        var limits = new CanvasLimitsOptions { MaxCanvasBytes = 100 };
        CanvasLimits.WhyTooLarge("{\"shapes\":[],\"pad\":\"" + new string('x', 200) + "\"}", limits)
            .Should().Contain("too large");
    }

    [Fact]
    public void A_canvas_with_too_many_shapes_is_refused()
    {
        var shapes = string.Join(",", Enumerable.Repeat("{}", 6));
        CanvasLimits.WhyTooLarge($$"""{"Shapes":[{{shapes}}]}""", new CanvasLimitsOptions { MaxShapes = 5 })
            .Should().Contain("6 items");
    }

    [Fact]
    public void A_normal_canvas_fits() =>
        CanvasLimits.WhyTooLarge("""{"shapes":[{},{}]}""", new CanvasLimitsOptions()).Should().BeNull();

    // ── R2-03: the client address the web app vouches for ───────────────────

    private static readonly byte[] IpKey = Encoding.UTF8.GetBytes("test-client-ip-key-0123456789abcdef");

    [Fact]
    public void The_signature_matches_the_web_apps()
    {
        // The same vector is asserted in apps/web/src/lib/server/forwarding.test.ts:
        // if the two sides ever disagree, every signed address is ignored.
        SignedClientIpMiddleware.Sign(IpKey, "203.0.113.7", 29_500_000)
            .Should().Be("Lx2LqHyIx0sny-WwI4erc2aKJCW-Pp-wqyral1WdUbw");
    }

    [Fact]
    public void A_signature_is_good_for_this_minute_and_the_last_only()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(29_500_000L * 60 + 30);
        SignedClientIpMiddleware.IsValid(IpKey, "203.0.113.7",
            SignedClientIpMiddleware.Sign(IpKey, "203.0.113.7", 29_500_000), now).Should().BeTrue();
        SignedClientIpMiddleware.IsValid(IpKey, "203.0.113.7",
            SignedClientIpMiddleware.Sign(IpKey, "203.0.113.7", 29_499_999), now).Should().BeTrue();
        SignedClientIpMiddleware.IsValid(IpKey, "203.0.113.7",
            SignedClientIpMiddleware.Sign(IpKey, "203.0.113.7", 29_499_998), now).Should().BeFalse("two minutes old");
    }

    [Fact]
    public void A_signature_does_not_carry_over_to_another_address_or_key()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(29_500_000L * 60);
        var sig = SignedClientIpMiddleware.Sign(IpKey, "203.0.113.7", 29_500_000);
        SignedClientIpMiddleware.IsValid(IpKey, "203.0.113.8", sig, now).Should().BeFalse();
        SignedClientIpMiddleware.IsValid(Encoding.UTF8.GetBytes("another-key"), "203.0.113.7", sig, now).Should().BeFalse();
    }

    // ── R2-08: a stranger cannot lock the owner out ─────────────────────────

    [Fact]
    public void Failures_from_one_address_do_not_lock_the_owner_at_theirs()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new MemoryLoginThrottle(cache);

        for (var i = 0; i < ILoginThrottle.MaxFailuresPerAddress; i++)
            throttle.RecordFailure("owner@x.com", "198.51.100.66");

        throttle.IsLocked("owner@x.com", "198.51.100.66").Should().BeTrue();
        throttle.IsLocked("owner@x.com", "203.0.113.10").Should().BeFalse();
    }

    [Fact]
    public void The_account_wide_lock_spares_addresses_the_owner_has_signed_in_from()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new MemoryLoginThrottle(cache);
        throttle.RecordSuccess("owner@x.com", "203.0.113.10");

        // A distributed run: a few failures from each of many addresses.
        for (var i = 0; i < ILoginThrottle.MaxFailuresPerAccount; i++)
            throttle.RecordFailure("owner@x.com", $"198.51.100.{i % 200}");

        throttle.IsLocked("owner@x.com", "192.0.2.99").Should().BeTrue("a new address is paused");
        throttle.IsLocked("owner@x.com", "203.0.113.10").Should().BeFalse("the owner's own address is not");
    }

    [Fact]
    public void A_password_reset_clears_every_lock_on_the_account()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var throttle = new MemoryLoginThrottle(cache);
        for (var i = 0; i < ILoginThrottle.MaxFailuresPerAccount; i++)
            throttle.RecordFailure("owner@x.com", "198.51.100.66");

        throttle.Clear("owner@x.com");

        throttle.IsLocked("owner@x.com", "198.51.100.66").Should().BeFalse();
        throttle.IsLocked("owner@x.com", "192.0.2.99").Should().BeFalse();
    }

    // ── R2-09: ending sessions ───────────────────────────────────────────────

    [Fact]
    public void Deactivating_an_account_ends_its_sessions_to_the_second()
    {
        var user = User.Register(Email.Create("pharmacy@example.test"), "Pharmacy", PasswordHash.FromHash("x"));
        user.SessionsValidAfterUtc.Should().BeNull();

        user.Deactivate();

        user.SessionsValidAfterUtc.Should().NotBeNull();
        (user.SessionsValidAfterUtc!.Value.Ticks % TimeSpan.TicksPerSecond).Should().Be(0,
            "token times are whole seconds, so a token issued in the same second after this must pass");
    }

    // ── R2-13: link keys separate from the JWT secret ───────────────────────

    [Fact]
    public void Link_keys_are_derived_per_purpose_by_hkdf()
    {
        // Pinned with an independent HKDF (RFC 5869, SHA-256, empty salt).
        Convert.ToHexString(LinkSigningKeys.Derive("link-secret-for-tests-0123456789abcdef", "poster-image"))
            .ToLowerInvariant()
            .Should().Be("e803282e9f2e99e02b8c55d74a871d740ab769c22874ee9c659854288891c1fa");
    }

    // Tested on LinkSigningKeys directly, not through PosterImageToken: that
    // class holds its key in static state other test classes configure in
    // parallel, and reconfiguring it here would make them flaky.
    [Fact]
    public void A_link_signed_before_the_change_still_opens_and_a_new_one_survives_rotation()
    {
        const string jwt = "old-jwt-secret-0123456789abcdef-0123456789";
        const string linkSecret = "new-link-secret-0123456789abcdef-012345678";
        var payload = Encoding.UTF8.GetBytes("posters/o/p.png|1893456000");

        // Before R2-13: HMAC under the JWT secret's raw bytes.
        var oldSig = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(jwt), payload);

        var (signing, accepted) = LinkSigningKeys.For("poster-image", linkSecret, legacySecret: jwt);
        LinkSigningKeys.Verify(accepted, payload, oldSig).Should().BeTrue("links already in inboxes keep working");

        var newSig = System.Security.Cryptography.HMACSHA256.HashData(signing, payload);
        var (_, afterRotation) = LinkSigningKeys.For("poster-image", linkSecret, legacySecret: "rotated-jwt-0123456789abcdef-0123456789");
        LinkSigningKeys.Verify(afterRotation, payload, newSig).Should().BeTrue(
            "rotating the JWT secret must not break links signed with the link key");

        var (_, otherPurpose) = LinkSigningKeys.For("video-download", linkSecret, legacySecret: null);
        LinkSigningKeys.Verify(otherPurpose, payload, newSig).Should().BeFalse("each link type has its own key");
    }

    // ── R2-06: unsubscribe ───────────────────────────────────────────────────

    [Fact]
    public void An_unsubscribe_token_names_its_pharmacy_and_address_and_cannot_be_altered()
    {
        UnsubscribeToken.Configure("link-secret-for-tests-0123456789abcdef");
        var token = UnsubscribeToken.Sign("owner-1", " Jane.Doe@Example.com ");

        UnsubscribeToken.TryRead(token, out var owner, out var email).Should().BeTrue();
        owner.Should().Be("owner-1");
        email.Should().Be("jane.doe@example.com");

        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes("owner-2|jane.doe@example.com"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + token[token.IndexOf('.')..];
        UnsubscribeToken.TryRead(forged, out _, out _).Should().BeFalse();
        UnsubscribeToken.TryRead("not-a-token", out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("jane.doe@example.com", "j•••e@example.com")]
    [InlineData("jo@example.com", "j•••@example.com")]
    public void The_unsubscribe_page_never_shows_the_whole_address(string email, string masked) =>
        UnsubscribeService.Mask(email).Should().Be(masked);

    [Fact]
    public void Each_recipient_gets_their_own_link_in_the_body()
    {
        var html = CampaignEmail.BuildHtml("Flu", "Flu poster", "Riverside & Co", null,
            Array.Empty<VideoEmailLink>(), UnsubscribeLinks.Placeholder);
        var message = new EmailMessage("", "Riverside", new[] { "a@x.com", "b@x.com" }, "Flu", html, "x " + UnsubscribeLinks.Placeholder,
            Array.Empty<EmailAttachment>(),
            Unsubscribe: new Dictionary<string, UnsubscribeLinks>
            {
                ["a@x.com"] = new("https://web/unsubscribe?token=A&x=1", "https://api/api/unsubscribe?token=A"),
                ["b@x.com"] = new("https://web/unsubscribe?token=B", "https://api/api/unsubscribe?token=B"),
            });

        var (htmlA, textA) = UnsubscribeLinks.Personalise(message, "A@x.com");
        htmlA.Should().Contain("https://web/unsubscribe?token=A&amp;x=1").And.NotContain(UnsubscribeLinks.Placeholder);
        textA.Should().Contain("https://web/unsubscribe?token=A&x=1");

        var (htmlB, _) = UnsubscribeLinks.Personalise(message, "b@x.com");
        htmlB.Should().Contain("token=B").And.NotContain("token=A");
    }

    [Fact]
    public void Consent_keeps_its_first_date_and_can_be_withdrawn()
    {
        var patient = Patient.Create("owner", "Jane", "jane@example.com", null, null);
        patient.RecordMarketingConsent(true, "Recorded by the pharmacy");
        var first = patient.MarketingConsentAtUtc;
        first.Should().NotBeNull();

        patient.RecordMarketingConsent(true, "again");
        patient.MarketingConsentAtUtc.Should().Be(first);

        patient.RecordMarketingConsent(false, "");
        patient.MarketingConsentAtUtc.Should().BeNull();
        patient.MarketingConsentSource.Should().BeNull();
    }

    // ── R2-15: personal data in logs ─────────────────────────────────────────

    [Theory]
    [InlineData("jane.doe@example.com", "j•••e@example.com")]
    [InlineData(" Jo@Example.com ", "J•••@Example.com")]
    [InlineData("not-an-email", "•••")]
    [InlineData(null, "•••")]
    public void An_email_is_masked_for_logs(string? email, string expected) =>
        PersonalData.Mask(email).Should().Be(expected);

    [Theory]
    [InlineData("+44 7700 900123", "•••123")]
    [InlineData("12", "•••")]
    public void A_phone_number_keeps_only_its_last_digits(string phone, string expected) =>
        PersonalData.MaskPhone(phone).Should().Be(expected);

    // ── R2-06: consent from an imported contact file ────────────────────────

    [Fact]
    public void A_consent_column_is_read_as_yes_or_no_and_nothing_is_guessed()
    {
        var parsed = PatientCsv.Parse(
            "Full Name,Email,Phone Number,Notes,Marketing Consent\n"
            + "Yes Person,yes@example.com,,,Yes\n"
            + "No Person,no@example.com,,,no\n"
            + "Blank Person,blank@example.com,,,\n"
            + "Maybe Person,maybe@example.com,,,maybe\n");

        parsed.Rows.Select(r => (r.FullName, r.MarketingConsent)).Should().Equal(
            ("Yes Person", (bool?)true), ("No Person", (bool?)false), ("Blank Person", (bool?)null));
        parsed.Issues.Should().ContainSingle(i => i.Reason.Contains("Yes or No"));
    }

    [Fact]
    public void A_file_without_a_consent_column_still_imports()
    {
        var parsed = PatientCsv.Parse("Full Name,Email,Phone Number,Notes\nJane,jane@example.com,,\n");
        parsed.Rows.Should().ContainSingle(r => r.MarketingConsent == null);
    }

    [Fact]
    public void Export_carries_consent_and_imports_straight_back()
    {
        var withConsent = Patient.Create("o", "Jane", "jane@example.com", null, null);
        withConsent.RecordMarketingConsent(true, "test");
        var without = Patient.Create("o", "Mo", "mo@example.com", null, null);

        var parsed = PatientCsv.Parse(PatientCsv.Write(new[] { withConsent, without }));

        parsed.Rows.Select(r => r.MarketingConsent).Should().Equal(true, null);
    }
}
