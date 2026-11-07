using Acme.Application.BrandKit;
using Acme.Domain.Users;
using FluentAssertions;
using Xunit;

using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Tests.BrandKit;

/// <summary>
/// A poster must never print <c>{{address}}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The built-in templates write their contact line as tokens, which the
/// editor's <b>Apply pharmacy info</b> button fills. That button is pressed by
/// a person, and people do not always press it — open a template, change the
/// headline, publish. Without resolution at render time the poster goes to
/// patients with the token in it.
/// </para>
/// <para>
/// This is the reason the render resolves them as well as the editor. The
/// templates were converted to tokens to stop the address printing twice; these
/// tests are what stop that fix turning into a worse fault.
/// </para>
/// </remarks>
public class PharmacyTokenRenderTests
{
    private static PharmacyProfile Riverside() => PharmacyProfile.Create(
        pharmacyName: "Riverside Pharmacy",
        address: "12 Riverside Walk",
        postCode: "M1 4AB",
        description: "",
        contactName: "A Pharmacist",
        contactPhone: "0161 496 0000",
        sonarFCode: null);

    /// <summary>
    /// The kit, which is where the booking link lives.
    /// </summary>
    /// <remarks>
    /// <c>{{booking_url}}</c> is the one pharmacy token that does not come from
    /// the profile. It never did resolve from two places — it moved, wholesale,
    /// so that the value a QR code encodes and the value a token prints are the
    /// same value.
    /// </remarks>
    private static DomainBrandKit RiversideKit() =>
        TestBrandKit.WithBooking("https://booking.riverside.co.uk/flu");

    /// <summary>
    /// Every spelling the token pattern admits, walked one by one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the shape of the bug it catches.</b> The pattern generates
    /// variants — <c>booking[_ ]?(?:url|link)</c> is six spellings — and the
    /// resolver matches them against a hand-written list. Miss one and it
    /// matches the pattern, misses the list, and is printed verbatim on a
    /// poster. <c>{{booking link}}</c> and <c>{{bookinglink}}</c> did precisely
    /// that when the booking token was added.
    /// </para>
    /// <para>
    /// Enumerated here rather than trusted, because the failure is silent on
    /// every screen a pharmacist looks at: the editor's own button uses a
    /// different list, so it fills the token in correctly and only the printed
    /// or emailed poster carries the literal text.
    /// </para>
    /// </remarks>
    [Theory]
    // separator variants: underscore, space, nothing
    [InlineData("{{pharmacy_name}}")]
    [InlineData("{{pharmacy name}}")]
    [InlineData("{{pharmacyname}}")]
    [InlineData("{{post_code}}")]
    [InlineData("{{post code}}")]
    [InlineData("{{postcode}}")]
    [InlineData("{{contact_name}}")]
    [InlineData("{{contact name}}")]
    [InlineData("{{contactname}}")]
    [InlineData("{{booking_url}}")]
    [InlineData("{{booking url}}")]
    [InlineData("{{bookingurl}}")]
    [InlineData("{{booking_link}}")]
    [InlineData("{{booking link}}")]
    [InlineData("{{bookinglink}}")]
    [InlineData("{{address}}")]
    [InlineData("{{phone}}")]
    // and the forms the pattern tolerates around the name
    [InlineData("{{ booking_url }}")]
    [InlineData("{{BOOKING_URL}}")]
    [InlineData("{{Pharmacy Name}}")]
    public void No_spelling_the_pattern_accepts_survives_to_the_poster(string token)
    {
        var resolved = BrandCanvasAssets.ResolveForRender(CanvasWith(token), RiversideKit(), Riverside());

        resolved.Should().NotContain("{{",
            "'{0}' matches the token pattern, so it reaches a patient either "
            + "resolved or printed as-is. There is no third option", token);
    }

    private static string CanvasWith(string text) =>
        $$"""{"schemaVersion":1,"shapes":[{"id":"a","kind":"text","text":"{{text}}"}]}""";

    [Fact]
    public void Tokens_are_replaced_with_the_pharmacy_details()
    {
        var canvas = CanvasWith("{{pharmacy_name}} · {{phone}}");

        var resolved = BrandCanvasAssets.ResolveForRender(canvas, RiversideKit(), Riverside());

        resolved.Should().Contain("Riverside Pharmacy").And.Contain("0161 496 0000");
        resolved.Should().NotContain("{{");
    }

    [Fact]
    public void The_address_token_includes_the_postcode()
    {
        // The editor's footer joins them the same way. If only one side did,
        // the postcode would appear or vanish depending on whether somebody
        // pressed a button.
        var resolved = BrandCanvasAssets.ResolveForRender(
            CanvasWith("{{address}}"), RiversideKit(), Riverside());

        resolved.Should().Contain("12 Riverside Walk, M1 4AB");
    }

    [Fact]
    public void An_unfinished_profile_leaves_a_blank_rather_than_a_token()
    {
        // A pharmacy that skipped onboarding. A gap on the poster is a gap;
        // a literal {{phone}} is a mistake a patient can read.
        var resolved = BrandCanvasAssets.ResolveForRender(
            CanvasWith("{{pharmacy_name}} · {{phone}}"), RiversideKit(), PharmacyProfile.Empty());

        resolved.Should().NotContain("{{");
        resolved.Should().NotContain("}}");
    }

    [Fact]
    public void A_quote_in_the_pharmacy_name_does_not_break_the_canvas()
    {
        // The value lands inside a "text":"..." JSON value. Unescaped, a name
        // like O"Brien would truncate the shape and take the poster with it.
        // The positional constructor, not Create — Create requires a contact
        // name and phone, and what is under test here is escaping, not
        // validation. A profile can also reach this code from the database
        // without passing through Create at all.
        var profile = new PharmacyProfile(
            PharmacyName: "O\"Brien \\ Sons",
            Address: "1 High St",
            PostCode: "M1 1AA",
            Description: "",
            ContactName: "",
            ContactPhone: "",
            SonarFCode: null,
            OnboardingCompleted: true);

        var resolved = BrandCanvasAssets.ResolveForRender(
            CanvasWith("{{pharmacy_name}}"), RiversideKit(), profile);

        var parse = () => System.Text.Json.JsonDocument.Parse(resolved);
        parse.Should().NotThrow("the resolved canvas must still be loadable");
    }

    [Fact]
    public void Every_built_in_template_resolves_with_nothing_left_over()
    {
        // The end-to-end statement: take what ships, resolve it the way a
        // render does, and there must be no token left anywhere.
        foreach (var template in Acme.Infrastructure.Persistence.TemplateSeeder
                     .DefaultTemplatesForTesting())
        {
            var resolved = BrandCanvasAssets.ResolveForRender(
                template.CanvasJson,
                TestBrandKit.With(
                    logoBlobKey: "logo/key.png",
                    bookingUrl: "https://booking.riverside.co.uk/flu"),
                Riverside());

            resolved.Should().NotContain("{{",
                $"template {template.Id} ({template.Name}) still has an unresolved token");
        }
    }

    [Fact]
    public void Without_a_profile_print_shows_blanks_not_tokens()
    {
        // A pharmacy that signed up and skipped onboarding has no profile. Its
        // PDF, PNG, email and screen must show a gap, never "{{address}}".
        var canvas = CanvasWith("Visit {{pharmacy_name}} at {{address}}. Call {{phone}}.");

        var printed = BrandCanvasAssets.ResolveForRender(canvas, kit: null, profile: null);

        printed.Should().NotContain("{{").And.Contain("Visit  at . Call .");
    }

    [Fact]
    public void Without_a_profile_the_booking_link_still_fills_from_the_brand_kit()
    {
        var printed = BrandCanvasAssets.ResolveForRender(
            CanvasWith("Book: {{booking_url}} or ask {{contact_name}}"), RiversideKit(), profile: null);

        printed.Should().Contain("Book: https://booking.riverside.co.uk/flu or ask ").And.NotContain("{{");
    }

    [Fact]
    public void Without_a_profile_a_new_poster_keeps_its_tokens_for_later()
    {
        // Stored as tokens, so completing onboarding later fills every poster
        // already made, exactly as for a poster made afterwards.
        var canvas = CanvasWith("{{pharmacy_name}} | {{address}}");

        BrandCanvasAssets.ResolveTokensForNewPoster(canvas, profile: null, kit: null).Should().Be(canvas);
    }

    [Fact]
    public void Once_onboarding_is_done_the_same_stored_poster_prints_the_details()
    {
        var stored = BrandCanvasAssets.ResolveTokensForNewPoster(
            CanvasWith("{{pharmacy_name}} | {{phone}}"), profile: null, kit: null);

        BrandCanvasAssets.ResolveForRender(stored, kit: null, profile: Riverside())
            .Should().Contain("Riverside Pharmacy | 0161 496 0000");
    }
}
