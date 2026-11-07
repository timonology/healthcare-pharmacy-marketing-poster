using Acme.Domain.BrandKit;
using Acme.Domain.Users;
using FluentAssertions;
using Xunit;
using DomainBrandKit = Acme.Domain.BrandKit.BrandKit;

namespace Acme.Tests.BrandKit;

/// <summary>
/// Which record names the pharmacy, when the two disagree.
/// </summary>
/// <remarks>
/// <para>
/// A pharmacy's name lives in the onboarding profile and again in the brand
/// kit, and before <see cref="PharmacyBrand"/> existed three surfaces resolved
/// that disagreement three different ways: poster tokens read the profile and
/// never looked at the kit, the campaign sender name read the profile then the
/// kit, and video was specified to read the kit then the profile.
/// </para>
/// <para>
/// The last of those is the one worth dwelling on, because it had not shipped
/// yet. A video and a poster usually go out in the same campaign. Opposite
/// precedence would have put one name on the video and a different one on the
/// poster beside it, for every pharmacy whose two records disagree — and the
/// pharmacy would have had no way to tell which we were going to use.
/// </para>
/// </remarks>
public class PharmacyBrandTests
{
    /// <summary>
    /// A profile, including shapes <see cref="PharmacyProfile.Create"/> refuses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built through the record constructor rather than the factory, because the
    /// factory requires a pharmacy name and these tests are largely about what
    /// happens when there isn't one.
    /// </para>
    /// <para>
    /// That is not an invented state to make a test go green. Every account that
    /// has not finished onboarding holds <see cref="PharmacyProfile.Empty"/>,
    /// and every staff account holds <see cref="PharmacyProfile.ForStaff"/> —
    /// both have a blank name, both are persisted, and both reach this resolver.
    /// The booking URL is no longer here at all — it moved to the kit, which is
    /// the one record that holds it.
    /// </para>
    /// </remarks>
    private static PharmacyProfile Profile(
        string name = "Riverside Pharmacy",
        string address = "12 Riverside Walk",
        string postCode = "M1 4AB",
        string phone = "0161 496 0000") =>
        new(
            PharmacyName: name,
            Address: address,
            PostCode: postCode,
            Description: "",
            ContactName: "A Pharmacist",
            ContactPhone: phone,
            SonarFCode: null,
            OnboardingCompleted: true);

    private static DomainBrandKit Kit(
        string name = "Riverside Chemists",
        string phone = "0161 000 0000",
        string address = "Old Address, M2 9ZZ",
        string? logoKey = "brand/riverside/logo.png",
        string? bookingUrl = null)
    {
        var kit = DomainBrandKit.Create(
            ownerId: "owner-1",
            name: "Riverside brand",
            colors: BrandColors.Create("#0d9488", "#134e4a", "#f59e0b"),
            pharmacy: PharmacyDetails.Create(name, "FP-1234", phone, address, bookingUrl),
            regulatoryFooter: "Registered pharmacy.");

        if (logoKey is not null) kit.SetLogo(logoKey);
        return kit;
    }

    [Fact]
    public void The_profile_wins_when_both_records_have_a_name()
    {
        var brand = PharmacyBrand.Resolve(Profile(), Kit());

        brand.Name.Should().Be("Riverside Pharmacy",
            "the profile is what onboarding asks for, what the app nags about "
            + "until it is done, and what posters already print. A video that "
            + "disagreed with the poster in the same campaign is the outcome "
            + "this ordering exists to prevent");
    }

    [Fact]
    public void The_brand_kit_fills_a_gap_rather_than_losing_outright()
    {
        var brand = PharmacyBrand.Resolve(Profile(name: ""), Kit());

        brand.Name.Should().Be("Riverside Chemists",
            "somebody who filled in the brand kit has plainly told us what their "
            + "pharmacy is called. Preferring the profile must not mean ignoring "
            + "the other screen that asked the same question");
        brand.IsUsable.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void Whitespace_in_the_profile_does_not_beat_a_real_brand_kit(string blank)
    {
        // The three hand-written copies of this rule disagreed about whether
        // whitespace counted as a value. It is the difference between a pharmacy
        // whose name is a stray space getting branded and getting "Pharmacy".
        var brand = PharmacyBrand.Resolve(Profile(name: blank), Kit());

        brand.Name.Should().Be("Riverside Chemists");
    }

    [Fact]
    public void With_neither_record_the_name_is_generic_and_marked_unusable()
    {
        var brand = PharmacyBrand.Resolve(null, null);

        brand.Name.Should().Be("Pharmacy",
            "never the account holder's own name — a patient has no idea who "
            + "that is and every reason to treat it as spam");
        brand.IsUsable.Should().BeFalse(
            "this is the flag every surface reads to decide whether to brand at "
            + "all, and branding something \"Pharmacy\" is worse than leaving it "
            + "unbranded");
    }

    [Fact]
    public void A_pharmacy_actually_called_Pharmacy_is_still_usable()
    {
        // IsUsable is recorded during resolution rather than inferred by
        // comparing Name to the fallback. Inferring it would strip the branding
        // from a real pharmacy for the crime of its own name.
        var brand = PharmacyBrand.Resolve(Profile(name: "Pharmacy"), null);

        brand.Name.Should().Be("Pharmacy");
        brand.IsUsable.Should().BeTrue();
    }

    [Fact]
    public void The_address_is_assembled_from_the_profile_rather_than_picked()
    {
        var brand = PharmacyBrand.Resolve(Profile(), Kit());

        brand.Address.Should().Be("12 Riverside Walk, M1 4AB",
            "the profile splits the address across two fields and the brand kit "
            + "holds it as one line. Taking the profile's street without its "
            + "postcode would be worse than taking the kit's complete address");
    }

    [Fact]
    public void A_profile_with_only_a_postcode_still_beats_the_brand_kit()
    {
        var brand = PharmacyBrand.Resolve(Profile(address: "", postCode: "M1 4AB"), Kit());

        brand.Address.Should().Be("M1 4AB");
    }

    [Fact]
    public void An_empty_profile_address_falls_through_to_the_brand_kit()
    {
        var brand = PharmacyBrand.Resolve(Profile(address: "", postCode: ""), Kit());

        brand.Address.Should().Be("Old Address, M2 9ZZ");
    }

    [Fact]
    public void The_logo_colours_licence_and_footer_come_only_from_the_brand_kit()
    {
        // Not a precedence question: the profile has no such fields, so there is
        // nothing to prefer over them.
        var brand = PharmacyBrand.Resolve(Profile(), Kit());

        brand.LogoBlobKey.Should().Be("brand/riverside/logo.png");
        brand.Colors.Should().NotBeNull();
        brand.Colors!.Value.Primary.Should().Be("#0d9488");
        brand.LicenseNumber.Should().Be("FP-1234");
        brand.RegulatoryFooter.Should().Be("Registered pharmacy.");
    }

    [Fact]
    public void Without_a_brand_kit_there_is_no_logo_and_no_palette()
    {
        var brand = PharmacyBrand.Resolve(Profile(), null);

        brand.Name.Should().Be("Riverside Pharmacy", "the profile alone is enough to brand with");
        brand.LogoBlobKey.Should().BeNull();
        brand.Colors.Should().BeNull("a video must fall back to a neutral palette, not invent one");
        brand.LicenseNumber.Should().BeEmpty();
    }

    [Fact]
    public void The_booking_link_is_brand_kit_only()
    {
        // The profile has no booking field any more, and BookingLink.Normalize
        // is the single gate keeping a javascript: URL off a poster and out of a
        // QR code. A second source would be a second way round it.
        var brand = PharmacyBrand.Resolve(
            Profile(), Kit(bookingUrl: "book.riverside.example"));

        brand.BookingUrl.Should().StartWith("https://",
            "the kit normalises a bare domain on the way in");
    }

    [Fact]
    public void A_pharmacy_with_no_kit_has_no_booking_link()
    {
        // The one case the move created, and it has to resolve quietly. Before,
        // a pharmacy could have a booking link without ever opening the brand
        // kit page; now the link and the kit arrive together, and a pharmacy
        // with neither must produce null rather than anything that would print.
        var brand = PharmacyBrand.Resolve(Profile(), kit: null);

        brand.BookingUrl.Should().BeNull();
        brand.Name.Should().Be("Riverside Pharmacy",
            "the rest of the identity still resolves from the profile");
    }

    [Fact]
    public void No_booking_link_is_null_rather_than_blank()
    {
        var brand = PharmacyBrand.Resolve(Profile(), Kit());

        brand.BookingUrl.Should().BeNull(
            "callers test this for presence. An empty string would print an "
            + "empty line on an end card instead of omitting it");
    }

    [Fact]
    public void Every_string_is_trimmed_on_the_way_out()
    {
        var brand = PharmacyBrand.Resolve(Profile(name: "  Riverside Pharmacy  "), null);

        brand.Name.Should().Be("Riverside Pharmacy",
            "this lands in an email From line and on a video end card, and "
            + "leading whitespace shows in both");
    }

    [Fact]
    public void Unknown_is_the_same_as_resolving_nothing()
    {
        PharmacyBrand.Unknown().Should().Be(PharmacyBrand.Resolve(null, null));
    }
}
