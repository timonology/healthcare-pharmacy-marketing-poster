using System.Text.Json;
using Acme.Application.BrandKit;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.BrandKit;

/// <summary>
/// Turning a canvas's logo reference into something a renderer can load.
/// </summary>
/// <remarks>
/// This rewrites stored documents by regular expression, which is the sort of
/// thing that works on the examples somebody thought of and quietly mangles the
/// one they didn't. So the cases here are the awkward ones: other image sources
/// that must be left alone, a caption that merely talks about the field, and a
/// pharmacy that has no logo at all.
/// </remarks>
public class BrandCanvasAssetsTests
{
    private const string Logo = "brand/logos/pharmacy-1.png";

    /// <summary>
    /// The kit carrying <see cref="Logo"/>.
    /// </summary>
    /// <remarks>
    /// <c>ResolveForRender</c> took a bare logo key until the booking link moved
    /// onto the brand kit. It takes the record now, so the logo and the link
    /// cannot arrive separately — see the remarks on that method.
    /// </remarks>
    private static Acme.Domain.BrandKit.BrandKit LogoKit() => TestBrandKit.WithLogo(Logo);

    private static string Canvas(params string[] blobKeys)
    {
        var shapes = string.Join(",", blobKeys.Select(k =>
            $$"""{"kind":"image","blobKey":"{{k}}","width":10,"height":10}"""));
        return $$"""{"width":1240,"height":1754,"shapes":[{{shapes}}]}""";
    }

    private static string[] KeysOf(string canvasJson) =>
        JsonDocument.Parse(canvasJson).RootElement
            .GetProperty("shapes").EnumerateArray()
            .Select(s => s.GetProperty("blobKey").GetString()!)
            .ToArray();

    [Fact]
    public void The_logo_reference_becomes_the_owners_blob_key()
    {
        var resolved = BrandCanvasAssets.ResolveForRender(
            Canvas(BrandCanvasAssets.SrcPath), LogoKit());

        KeysOf(resolved).Should().Equal(Logo);
    }

    [Fact]
    public void Other_image_sources_are_left_exactly_as_they_were()
    {
        // Each of these is a real form a canvas holds, and each is resolved by
        // a different branch of the exporter. Rewriting any of them would break
        // a picture that was working. All three are on the image-source
        // allow-list (security review SEC-01): a template asset by its real
        // name shape, the one external stock host, and an inline picture.
        var others = new[]
        {
            "/api/assets/templates/0123456789abcdef0123456789abcdef.png",
            "https://images.unsplash.com/photo-1584515933487-779824d29309?w=800",
            "data:image/png;base64,iVBORw0KGgo=",
        };

        var resolved = BrandCanvasAssets.ResolveForRender(Canvas(others), LogoKit());

        KeysOf(resolved).Should().Equal(others);
    }

    [Fact]
    public void Image_sources_off_the_allow_list_are_blanked_at_render()
    {
        // Security review SEC-01: the renderer never fetches a source the
        // canvas was not allowed to hold. A stored canvas from before the rule
        // keeps its other pictures and prints a gap where these were.
        var resolved = BrandCanvasAssets.ResolveForRender(
            Canvas("https://images.example.com/stock/flu.jpg",
                   "/api/assets/templates/abc123.png",
                   "data:image/png;base64,iVBORw0KGgo="),
            LogoKit());

        KeysOf(resolved).Should().Equal("", "", "data:image/png;base64,iVBORw0KGgo=");
    }

    [Fact]
    public void A_logo_authored_against_another_host_still_resolves()
    {
        // A canvas outlives the deployment it was made on. Template assets make
        // the same allowance, for the same reason.
        var resolved = BrandCanvasAssets.ResolveForRender(
            Canvas("https://staging.example.test/api/brand-kit/logo"), LogoKit());

        KeysOf(resolved).Should().Equal(Logo);
    }

    [Fact]
    public void A_pharmacy_with_no_logo_gets_an_empty_source_not_a_dead_path()
    {
        // Leaving the path behind would make every render log a failed image
        // load for a pharmacy that simply has not uploaded a logo. An empty
        // source is skipped silently, which is the truth of the situation.
        var resolved = BrandCanvasAssets.ResolveForRender(
            Canvas(BrandCanvasAssets.SrcPath), TestBrandKit.WithLogo(null));

        KeysOf(resolved).Should().Equal("");
    }

    [Fact]
    public void A_pharmacy_with_no_brand_kit_at_all_gets_the_same_empty_source()
    {
        // Two different absences that used to be one argument. A kit with no
        // logo and no kit at all were both `logoBlobKey: null` before the
        // signature took the record; now they are distinguishable, and they must
        // still behave identically — a pharmacy that has not opened the brand
        // kit page is not a pharmacy whose posters should fail to render.
        var resolved = BrandCanvasAssets.ResolveForRender(
            Canvas(BrandCanvasAssets.SrcPath), kit: null);

        KeysOf(resolved).Should().Equal("");
    }

    [Fact]
    public void Every_logo_on_a_canvas_is_resolved_not_just_the_first()
    {
        var resolved = BrandCanvasAssets.ResolveForRender(
            Canvas(BrandCanvasAssets.SrcPath, "https://images.unsplash.com/photo-1?w=800", BrandCanvasAssets.SrcPath),
            LogoKit());

        KeysOf(resolved).Should().Equal(Logo, "https://images.unsplash.com/photo-1?w=800", Logo);
    }

    [Fact]
    public void Text_that_merely_mentions_the_field_is_not_rewritten()
    {
        // The regex looks for a JSON key. In valid JSON a quote inside a string
        // is escaped, so this cannot match — but the whole document is being
        // rewritten by pattern, and "it cannot happen" deserves a test.
        var canvas =
            """{"shapes":[{"kind":"text","text":"set \"blobKey\":\"/api/brand-kit/logo\" to fix it"}]}""";

        var resolved = BrandCanvasAssets.ResolveForRender(canvas, LogoKit());

        resolved.Should().Be(canvas);
        JsonDocument.Parse(resolved).RootElement
            .GetProperty("shapes")[0].GetProperty("text").GetString()
            .Should().Contain("/api/brand-kit/logo", "the caption is the designer's words");
    }

    [Fact]
    public void The_result_is_still_valid_json()
    {
        const string awkward = "brand/logos/it's \"quoted\".png";

        var resolved = BrandCanvasAssets.ResolveForRender(
            Canvas(BrandCanvasAssets.SrcPath), TestBrandKit.WithLogo(awkward));

        // A blob key with a quote in it would break the document if it were
        // pasted in raw. Parsing is the assertion.
        var act = () => JsonDocument.Parse(resolved);
        act.Should().NotThrow();
        KeysOf(resolved).Should().Equal(awkward);
    }

    [Theory]
    [InlineData("/api/brand-kit/logo", true)]
    [InlineData("/api/brand-kit/logo?v=2", true)]
    [InlineData("https://app.example.com/api/brand-kit/logo", true)]
    [InlineData("/api/assets/templates/x.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBrandLogo_recognises_the_forms_a_canvas_can_hold(string? src, bool expected)
        => BrandCanvasAssets.IsBrandLogo(src).Should().Be(expected);

    [Fact]
    public void An_empty_canvas_is_returned_untouched()
        => BrandCanvasAssets.ResolveForRender("", LogoKit()).Should().Be("");
}
