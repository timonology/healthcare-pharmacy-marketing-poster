using Acme.Application.BrandKit;
using Acme.Domain.Users;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.BrandKit;

/// <summary>
/// The booking QR, as it reaches a printed poster and a shop window.
/// </summary>
/// <remarks>
/// <para>
/// A canvas stores the path <c>/api/brand-kit/booking-qr</c> and each renderer
/// resolves it, exactly as the logo works. Resolution happens in one place, so
/// the campaign email, an in-pharmacy screen and a PDF export cannot disagree
/// about what a poster looks like — all three come through
/// <c>PosterImageService.EnsureRenderedAsync</c>.
/// </para>
/// <para>
/// The failure worth designing against is not a missing QR. It is a QR that is
/// present and wrong: a patient cannot tell a dead code from a working one until
/// they have already pointed a phone at it, and by then the posters are on the
/// wall.
/// </para>
/// </remarks>
public class BookingQrRenderTests
{
    /// <summary>
    /// A stand-in encoder. Deterministic, which is the property under test.
    /// </summary>
    private sealed class FakeQr : IQrCodeGenerator
    {
        public int Calls { get; private set; }
        public string? LastContent { get; private set; }

        public byte[]? ToPng(string content, int pixelsPerModule = 8)
        {
            Calls++;
            LastContent = content;
            // Bytes derived from the content, so a different link produces a
            // different image — the thing the cache key depends on.
            return System.Text.Encoding.UTF8.GetBytes("PNG:" + content);
        }
    }

    private sealed class RefusingQr : IQrCodeGenerator
    {
        public byte[]? ToPng(string content, int pixelsPerModule = 8) => null;
    }

    /// <summary>The profile. It no longer carries the booking link.</summary>
    /// <remarks>
    /// The link moved to the brand kit, so these tests now pass a kit alongside
    /// the profile. Which record holds it is the whole point of the move: there
    /// is one copy, and <c>ResolveForRender</c> reads it from the kit or not at
    /// all.
    /// </remarks>
    private static PharmacyProfile Riverside() => PharmacyProfile.Create(
        pharmacyName: "Riverside Pharmacy",
        address: "12 Riverside Walk",
        postCode: "M1 4AB",
        description: "",
        contactName: "A Pharmacist",
        contactPhone: "0161 496 0000",
        sonarFCode: null);

    private static string CanvasWithQr(int copies = 1)
    {
        var shapes = string.Join(",", Enumerable.Range(0, copies).Select(i =>
            $$"""{"id":"q{{i}}","kind":"image","blobKey":"/api/brand-kit/booking-qr","width":200,"height":200}"""));
        return $$"""{"schemaVersion":1,"shapes":[{{shapes}}]}""";
    }

    [Fact]
    public void The_code_is_inlined_so_every_renderer_can_draw_it()
    {
        var qr = new FakeQr();

        var resolved = BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking("https://booking.riverside.co.uk/flu"),
            Riverside(), qr);

        resolved.Should().Contain("data:image/png;base64,",
            "the renderers already load images by data URI, and inlining means "
            + "the export does not have to fetch anything to draw the code");
        resolved.Should().NotContain("/api/brand-kit/booking-qr",
            "an unresolved path would be handed to the image loader as a blob key");
        qr.LastContent.Should().Be("https://booking.riverside.co.uk/flu");
    }

    [Fact]
    public void Without_a_booking_link_there_is_no_code_rather_than_a_dead_one()
    {
        var resolved = BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking(null),
            Riverside(), new FakeQr());

        resolved.Should().Contain("\"blobKey\":\"\"",
            "an empty source is skipped by every renderer, so the poster prints a "
            + "gap. A square that scans to nothing is worse: the patient finds out "
            + "by pointing a phone at it");
        resolved.Should().NotContain("data:image/png");
    }

    [Fact]
    public void An_encoder_that_refuses_costs_the_code_not_the_poster()
    {
        // A pharmacist losing a whole export over one unencodable URL is a much
        // worse day than a poster with a gap in it.
        var act = () => BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking("https://booking.riverside.co.uk/"),
            Riverside(), new RefusingQr());

        act.Should().NotThrow();
        act().Should().Contain("\"blobKey\":\"\"");
    }

    [Fact]
    public void With_no_encoder_wired_the_reference_still_does_not_reach_the_renderer()
    {
        // The generator is an optional parameter so the callers that do not care
        // stay simple. The cost of forgetting to wire one has to be a missing
        // QR, never a path handed to the image loader as though it were a key.
        var resolved = BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking("https://booking.riverside.co.uk/"),
            Riverside(), qr: null);

        resolved.Should().Contain("\"blobKey\":\"\"");
    }

    [Fact]
    public void The_code_is_encoded_once_however_many_times_it_appears()
    {
        // A poster with the code on the front and again on a tear-off strip.
        var qr = new FakeQr();

        BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(copies: 3), TestBrandKit.WithBooking("https://booking.riverside.co.uk/"),
            Riverside(), qr);

        qr.Calls.Should().Be(1,
            "encoding per reference pays for it repeatedly, and would embed two "
            + "different images if the encoder ever stopped being deterministic");
    }

    [Fact]
    public void Changing_the_booking_link_changes_the_rendered_canvas()
    {
        // <b>This is what makes the render cache correct.</b> The resolved canvas
        // is hashed into the poster's cache key, so the bytes of the QR being
        // part of it is exactly what makes "change your booking link and every
        // poster updates" work — the same property the logo relies on.
        var before = BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking("https://booking.riverside.co.uk/"),
            Riverside(), new FakeQr());

        var after = BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking("https://book.riverside.nhs.uk/"),
            Riverside(), new FakeQr());

        after.Should().NotBe(before,
            "identical output would leave every existing poster pointing at the "
            + "old booking page, with nothing to show anything had changed");
    }

    [Fact]
    public void The_same_link_resolves_identically_every_time()
    {
        // The other half of the cache key. An encoder that varied its bytes would
        // miss the cache on every render and re-rasterise every poster forever —
        // nothing would look wrong, it would just be slow, and nobody would trace
        // it back to here.
        var first = BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking("https://booking.riverside.co.uk/"),
            Riverside(), new FakeQr());

        var second = BrandCanvasAssets.ResolveForRender(
            CanvasWithQr(), TestBrandKit.WithBooking("https://booking.riverside.co.uk/"),
            Riverside(), new FakeQr());

        second.Should().Be(first);
    }

    [Fact]
    public void A_logo_and_a_code_on_the_same_canvas_each_resolve_to_their_own_thing()
    {
        var canvas = """
            {"schemaVersion":1,"shapes":[
              {"id":"l","kind":"image","blobKey":"/api/brand-kit/logo","width":100,"height":100},
              {"id":"q","kind":"image","blobKey":"/api/brand-kit/booking-qr","width":200,"height":200}
            ]}
            """;

        var resolved = BrandCanvasAssets.ResolveForRender(
            canvas,
            TestBrandKit.With(
                logoBlobKey: "logos/owner-1/mark.png",
                bookingUrl: "https://booking.riverside.co.uk/"),
            Riverside(), new FakeQr());

        resolved.Should().Contain("logos/owner-1/mark.png");
        resolved.Should().Contain("data:image/png;base64,");
        resolved.Should().NotContain("/api/brand-kit/");
    }
}
