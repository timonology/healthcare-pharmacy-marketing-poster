using Acme.Domain.BrandKit;
using Acme.Domain.Common;
using Acme.Domain.Users;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Domain;

/// <summary>
/// The booking link, which ends up somewhere it cannot be corrected.
/// </summary>
/// <remarks>
/// <para>
/// This is not an ordinary text field. It is printed on posters, rendered as a
/// QR code in a waiting room, and put in front of patients. A printed run
/// cannot be edited, and a QR is unreadable by the person holding it — they
/// cannot tell a wrong address from a right one until it fails to load, and by
/// then the posters are on the wall.
/// </para>
/// <para>
/// So the rules lean two ways at once: forgiving about what a pharmacist types,
/// strict about what is allowed through.
/// </para>
/// </remarks>
public class BookingUrlTests
{
    /* ── forgiving about how it is typed ─────────────────────────────── */

    [Theory]
    [InlineData("booking.mypharmacy.co.uk", "https://booking.mypharmacy.co.uk/")]
    [InlineData("www.mypharmacy.co.uk/book", "https://www.mypharmacy.co.uk/book")]
    [InlineData("  booking.mypharmacy.co.uk  ", "https://booking.mypharmacy.co.uk/")]
    public void A_bare_domain_gets_https_rather_than_a_telling_off(string typed, string expected)
    {
        // This is what a pharmacist types. Refusing it would teach them the
        // field is fussy rather than helpful, and the guess is unambiguous.
        BookingLink.Normalize(typed).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://booking.mypharmacy.co.uk/flu")]
    [InlineData("http://booking.mypharmacy.co.uk/flu")]
    public void A_full_address_is_kept_as_given(string typed)
    {
        BookingLink.Normalize(typed).Should().Be(typed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_link_is_a_real_answer(string? typed)
    {
        // Optional, and it has to stay optional. Onboarding predates this field,
        // so every existing pharmacy has none — a required one would make their
        // profile un-saveable the next time they edited a phone number.
        BookingLink.Normalize(typed).Should().BeNull();
    }

    /* ── strict about what gets through ──────────────────────────────── */

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://files.example.com")]
    public void Only_web_addresses_are_allowed(string typed)
    {
        // This becomes an href on a page and a QR code somebody scans in a shop.
        // A javascript: or data: URL reaching either is not a broken link, it is
        // a way to run something on a patient's phone from a poster on a wall.
        var act = () => BookingLink.Normalize(typed);
        act.Should().Throw<DomainException>().WithMessage("*web address*");
    }

    [Theory]
    [InlineData("booking")]
    [InlineData("localhost")]
    [InlineData("my pharmacy")]
    public void A_domain_has_to_look_like_a_domain(string typed)
    {
        // Catches the fat-fingered entry that would otherwise be printed as a
        // QR pointing at nothing. Nobody notices until a patient tries it.
        var act = () => BookingLink.Normalize(typed);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void An_absurdly_long_link_is_refused()
    {
        var act = () => BookingLink.Normalize(
            "https://booking.example.com/" + new string('a', 500));
        act.Should().Throw<DomainException>().WithMessage("*400 characters*");
    }

    /* ── how it reaches the brand kit ────────────────────────────────── */

    [Fact]
    public void Create_normalises_what_it_is_given()
    {
        var details = PharmacyDetails.Create(
            "Riverside Pharmacy", "FP-1234", "07000000000", "1 High St",
            bookingUrl: "booking.riverside.co.uk");

        details.BookingUrl.Should().Be("https://booking.riverside.co.uk/");
    }

    [Fact]
    public void A_kit_without_one_is_perfectly_valid()
    {
        var details = PharmacyDetails.Create(
            "Riverside Pharmacy", "FP-1234", "07000000000", "1 High St");

        details.BookingUrl.Should().BeNull();
    }

    [Fact]
    public void A_stored_link_that_cannot_be_normalised_is_dropped_not_thrown()
    {
        // TryNormalize is what hydration and the migration use. A value already
        // in the database must never take down the request that reads it: the
        // link got in before this gate existed in its current form, and throwing
        // now would break every page that touches that pharmacy's brand kit
        // while fixing nothing.
        BookingLink.TryNormalize("javascript:alert(1)").Should().BeNull();
        BookingLink.TryNormalize("https://booking.example.com/" + new string('a', 500))
            .Should().BeNull();
        BookingLink.TryNormalize("booking.riverside.co.uk")
            .Should().Be("https://booking.riverside.co.uk/",
                "a usable link still comes through unchanged");
    }

    [Fact]
    public void Normalising_an_already_normalised_link_changes_nothing()
    {
        // Relied on by BrandKitMapper, which TryNormalizes on the way in and
        // then lets PharmacyDetails.Create normalise again. If that were not
        // idempotent, every read would mutate the value it just read.
        var once = BookingLink.Normalize("booking.riverside.co.uk/flu?x=1");
        BookingLink.Normalize(once).Should().Be(once);
    }
}
