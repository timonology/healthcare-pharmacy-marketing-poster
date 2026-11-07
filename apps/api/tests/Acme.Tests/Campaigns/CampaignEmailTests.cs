using Acme.Application.Campaigns;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Campaigns;

/// <summary>
/// The HTML a patient's mail client receives.
/// </summary>
/// <remarks>
/// Assertions are about structure and safety rather than exact markup — a test
/// that pins the whole string breaks on every wording change and tells you
/// nothing about whether the email works. What matters is that the poster is
/// referenced the way the sender attaches it, that names cannot inject markup,
/// and that the layout rules mail clients actually need are present.
/// </remarks>
public class CampaignEmailTests
{
    private static readonly IReadOnlyList<VideoEmailLink> NoVideos = Array.Empty<VideoEmailLink>();

    private static readonly IReadOnlyList<VideoEmailLink> OneVideo = new[]
    {
        new VideoEmailLink("Flu jab explainer", "https://api.example.com/v/1?token=abc", "data:image/png;base64,AAA"),
    };

    /// <summary>The address the poster is served from in these tests.</summary>
    private const string PosterUrl =
        "https://api.example.com/api/posters/email-image?token=signed-abc";

    /// <summary>A linked poster, the shape a real deployment sends.</summary>
    private static CampaignEmail.CampaignPosterImage Linked =>
        new(PosterUrl, PosterUrl);

    /// <summary>An embedded poster, the shape a machine with no public API sends.</summary>
    private static CampaignEmail.CampaignPosterImage Embedded =>
        new($"cid:{CampaignEmail.PosterContentId}", LinkUrl: null);

    /// <summary>The ordinary case: a linked poster.</summary>
    private static string Html(IReadOnlyList<VideoEmailLink>? videos = null) =>
        HtmlWith(Linked, videos);

    /// <summary>
    /// Any other case. Separate from <see cref="Html"/> on purpose: a nullable
    /// parameter defaulting to "the linked poster" would make
    /// <c>Html(poster: null)</c> — the obvious way to write "no poster" — do the
    /// opposite of what it says.
    /// </summary>
    private static string HtmlWith(
        CampaignEmail.CampaignPosterImage? poster,
        IReadOnlyList<VideoEmailLink>? videos = null) =>
        CampaignEmail.BuildHtml(
            "Winter flu clinic", "Flu poster", "Riverside Pharmacy",
            poster, videos ?? NoVideos);

    /* ── the poster ──────────────────────────────────────────────────── */

    [Fact]
    public void The_poster_is_linked_rather_than_embedded()
    {
        // <b>This is the assertion that keeps campaigns sendable.</b>
        //
        // The poster used to be a cid: reference to an inline attachment, and
        // Mailjet's Send v3.1 copies an inline attachment into every recipient's
        // personalisation. Measured on a real failure: 977,045 bytes for four
        // recipients, 244 KB each, eighty seconds of uploading, and the provider
        // closing the connection before the request finished. A thousand
        // recipients would have been a quarter of a gigabyte.
        //
        // So going back to cid: is not a styling regression, it is a campaign
        // that cannot be sent — and nothing else in the suite would notice,
        // because every other test here is about markup.
        var html = Html();

        html.Should().Contain(PosterUrl);
        html.Should().NotContain("cid:",
            "an inline attachment is copied per recipient, which is what made "
            + "a four-recipient campaign a 977 KB request");
    }

    [Fact]
    public void An_embedded_poster_gets_no_link_because_there_is_nowhere_for_it_to_go()
    {
        // The shape a machine with no publicly reachable API sends, where a
        // link would reach nobody and the design travels in the message.
        //
        // `cid:` addresses a MIME part inside that message. It means nothing to
        // a browser, so wrapping the image in it would give the reader a link
        // that silently does nothing, and a "Not showing?" line offering to fix
        // a problem they do not have — the image is right there.
        var html = HtmlWith(Embedded);

        html.Should().Contain($"cid:{CampaignEmail.PosterContentId}");
        html.Should().NotContain("Not showing?");
        html.Should().NotContain($"href=\"cid:", "a cid is not an address anything can open");
    }

    [Fact]
    public void A_reader_whose_client_blocks_images_still_has_a_way_to_the_poster()
    {
        // The cost of linking instead of embedding, and the thing that pays it.
        // Outlook desktop blocks remote images from an unknown sender, and the
        // poster is not decoration — it is the entire message. Without this the
        // reader gets a blank box and no idea what they were sent.
        var html = Html();

        html.Should().Contain("Not showing?");

        // The link has to be text, outside the image, or it is blocked with it.
        var imgAt = html.IndexOf("<img", StringComparison.Ordinal);
        var linkAt = html.IndexOf("Not showing?", StringComparison.Ordinal);
        linkAt.Should().BeGreaterThan(imgAt, "it sits under the design, not inside it");
    }

    [Fact]
    public void The_poster_scales_on_a_phone_and_is_centred()
    {
        var html = Html();

        // `width:100%` with `max-width` is what makes an image shrink on a
        // narrow screen instead of forcing a horizontal scroll.
        html.Should().Contain("width:100%");
        html.Should().Contain("max-width:600px");

        // Outlook ignores CSS centring on block elements; the align attribute
        // on the containing cell is what it honours.
        html.Should().Contain("align=\"center\"");

        // Without display:block an inline image sits on the text baseline and
        // leaves a pale stripe beneath it.
        html.Should().Contain("display:block");
    }

    [Fact]
    public void Without_a_rendered_image_it_says_so_rather_than_showing_a_broken_one()
    {
        // Rendering can fail, and the campaign still goes out. What it must not
        // do is emit an <img> pointing at nothing.
        var html = HtmlWith(poster: null);

        html.Should().NotContain("<img");
        html.Should().NotContain("Not showing?", "there is nothing to show");
        html.Should().Contain("Flu poster", "the name stands in for the design");
    }

    /* ── the videos ──────────────────────────────────────────────────── */

    [Fact]
    public void Videos_appear_below_the_poster()
    {
        // Order is the feature. The design leads; the clip supports it.
        var html = Html(videos: OneVideo);

        var poster = html.IndexOf(PosterUrl, StringComparison.Ordinal);
        var video = html.IndexOf("Watch", StringComparison.Ordinal);

        poster.Should().BeGreaterThan(0);
        video.Should().BeGreaterThan(poster, "the poster is the message and the video supports it");
    }

    [Fact]
    public void A_video_is_a_still_that_links_out_not_an_embedded_player()
    {
        // No mail client plays <video>. A still linking to the watch URL is the
        // only thing that works everywhere.
        var html = Html(videos: OneVideo);

        html.Should().NotContain("<video");
        html.Should().Contain("https://api.example.com/v/1?token=abc");
    }

    [Fact]
    public void The_video_block_is_absent_when_there_are_none()
    {
        var html = Html(videos: NoVideos);

        html.Should().NotContain("Watch");
        html.Should().NotContain("expire in 48 hours");
    }

    [Fact]
    public void A_video_without_a_thumbnail_still_gets_a_link()
    {
        // Thumbnail generation is best-effort. Losing it must not lose the video.
        var html = Html(videos: new[] { new VideoEmailLink("No thumb", "https://x/y", null) });

        html.Should().Contain("https://x/y");
        html.Should().Contain("No thumb");
    }

    /* ── safety ──────────────────────────────────────────────────────── */

    [Fact]
    public void Names_cannot_inject_markup()
    {
        // Campaign and poster names are free text typed by a pharmacy, and this
        // lands in someone else's inbox.
        var html = CampaignEmail.BuildHtml(
            "<script>alert(1)</script>",
            "\"><img src=x onerror=alert(2)>",
            "Riverside & Co",
            Linked,
            new[] { new VideoEmailLink("<b>vid</b>", "https://x/y\"onmouseover=alert(3)", null) });

        // What makes injected text dangerous is a tag that opens or an attribute
        // that closes — not the presence of a word.
        //
        // The first version of this asserted NotContain("onerror="), and failed
        // against perfectly escaped output: `onerror=` survives inside
        // alt="&quot;&gt;&lt;img src=x onerror=alert(2)&gt;", where every
        // delimiter is encoded and nothing can execute. An assertion that fails
        // on correct code is worse than none — it teaches whoever hits it to
        // loosen the test.
        html.Should().NotContain("<script", "a raw tag would run");
        html.Should().NotContain("<img src=x", "the poster name must not open a tag");
        html.Should().NotContain("\"onmouseover", "the URL must not break out of its attribute");

        // And the encoded forms are present, so the text is still shown.
        html.Should().Contain("&lt;script&gt;");
        html.Should().Contain("&lt;img src=x");
        html.Should().Contain("Riverside &amp; Co");
    }

    /* ── the text part ───────────────────────────────────────────────── */

    [Fact]
    public void There_is_always_a_plain_text_alternative()
    {
        // A message with no text part is markedly more likely to be filtered as
        // spam — and since the poster is an image, a text-only reader would
        // otherwise receive an entirely blank email.
        var text = CampaignEmail.BuildText(
            "Winter flu clinic", "Flu poster", "Riverside Pharmacy", PosterUrl, OneVideo);

        text.Should().Contain("Winter flu clinic");
        text.Should().Contain("Riverside Pharmacy");
        text.Should().Contain("https://api.example.com/v/1?token=abc");
        text.Should().NotContain("<");
    }

    [Fact]
    public void The_text_part_carries_the_poster_address_not_just_advice_about_images()
    {
        // A plain-text reader has no images to turn on. Since the poster stopped
        // being embedded, this line is the only route they have to it at all —
        // "turn on images in your email app" is advice they cannot act on.
        var text = CampaignEmail.BuildText(
            "Winter flu clinic", "Flu poster", "Riverside Pharmacy", PosterUrl, NoVideos);

        text.Should().Contain("Flu poster");
        text.Should().Contain(PosterUrl);
    }

    [Fact]
    public void With_no_render_the_text_part_names_the_poster_and_promises_no_link()
    {
        var text = CampaignEmail.BuildText(
            "Winter flu clinic", "Flu poster", "Riverside Pharmacy", null, NoVideos);

        text.Should().Contain("Flu poster");
        text.Should().NotContain("http", "there is no image to link to");
    }
}
