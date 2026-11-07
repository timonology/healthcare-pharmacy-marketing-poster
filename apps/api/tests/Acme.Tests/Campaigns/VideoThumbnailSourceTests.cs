using Acme.Application.Campaigns;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Campaigns;

/// <summary>
/// Where the video's still image comes from in a campaign email.
/// </summary>
/// <remarks>
/// <para>
/// A campaign went out with a poster and a video. The poster appeared, the watch
/// link worked, and the space above it was empty — because the thumbnail was a
/// base64 <c>data:</c> URL and <b>Gmail strips <c>data:</c> from
/// <c>img src</c></b>. The belief in the code was that data URLs "work
/// everywhere with zero fetch". They work in Apple Mail and Outlook desktop,
/// which is exactly why it survived testing.
/// </para>
/// <para>
/// The poster had already been moved to a signed link for the same reason and
/// the thumbnail was left behind — one question, answered twice, forty lines
/// apart.
/// </para>
/// <para>
/// Note where these sit: on the <i>choice</i> of source, not on the HTML.
/// <c>CampaignEmail.BuildHtml</c> renders whatever src it is handed, so a test
/// at that level passed happily throughout the bug.
/// </para>
/// </remarks>
public class VideoThumbnailSourceTests
{
    private const string Api = "https://api.example.com";
    private const string VideoId = "vid-123";
    private const string Token = "signed-token-abc";
    private const string DataUrl = "data:image/jpeg;base64,AAAA";

    private static Func<string> Signs(string token = Token) => () => token;
    private static Func<Task<string?>> Embeds(string? url = DataUrl) => () => Task.FromResult(url);

    [Fact]
    public async Task A_reachable_api_links_the_thumbnail()
    {
        var src = await CampaignService.ChooseThumbnailSrcAsync(
            canLink: true, Api, VideoId, Signs(), Embeds());

        src.Should().StartWith("https://");
        src.Should().Contain($"/api/videos/{VideoId}/public-download");
        src.Should().Contain($"token={Token}");
        src.Should().Contain("thumb=true", "the token is signed for the still, not the MP4");
    }

    [Fact]
    public async Task A_reachable_api_never_emits_a_data_url()
    {
        // The regression, stated as plainly as it can be. Gmail renders nothing
        // for these, and "nothing" is indistinguishable from a broken image.
        var src = await CampaignService.ChooseThumbnailSrcAsync(
            canLink: true, Api, VideoId, Signs(), Embeds());

        src.Should().NotStartWith("data:");
        src!.Should().NotContain("base64");
    }

    [Fact]
    public async Task Linking_does_not_read_the_blob_at_all()
    {
        // The cost this whole change exists to avoid. Every recipient's email is
        // built from one render, but reading and base64-ing a still that is
        // about to be thrown away is pure waste — and, worse, it is the kind of
        // waste that looks harmless until a campaign carries six videos.
        var loaded = false;

        var src = await CampaignService.ChooseThumbnailSrcAsync(
            canLink: true, Api, VideoId, Signs(),
            loadEmbedded: () => { loaded = true; return Task.FromResult<string?>(DataUrl); });

        loaded.Should().BeFalse("the embedded path must not run when the image can be linked");
        src.Should().StartWith("https://");
    }

    [Fact]
    public async Task An_unreachable_api_falls_back_to_the_embedded_image()
    {
        // A developer machine. Gmail could not fetch localhost either way, and
        // the desktop clients that can reach it do render data URLs — so this is
        // the one case where embedding is right.
        var src = await CampaignService.ChooseThumbnailSrcAsync(
            canLink: false, "http://localhost:5050", VideoId, Signs(), Embeds());

        src.Should().Be(DataUrl);
    }

    [Fact]
    public async Task An_unreachable_api_never_mints_a_token_nobody_can_use()
    {
        var signed = false;

        await CampaignService.ChooseThumbnailSrcAsync(
            canLink: false, "http://localhost:5050", VideoId,
            signThumbnailToken: () => { signed = true; return Token; },
            loadEmbedded: Embeds());

        signed.Should().BeFalse();
    }

    [Fact]
    public async Task No_still_anywhere_means_no_image_rather_than_a_broken_one()
    {
        // Better than an <img> pointing nowhere, which every client draws as a
        // broken-image icon.
        var src = await CampaignService.ChooseThumbnailSrcAsync(
            canLink: false, Api, VideoId, Signs(), Embeds(null));

        src.Should().BeNull();
    }

    [Fact]
    public async Task A_signer_that_returns_nothing_falls_back_rather_than_linking()
    {
        // A link with no signature is a 403 for the recipient — a broken image
        // where the embedded one would have worked.
        var src = await CampaignService.ChooseThumbnailSrcAsync(
            canLink: true, Api, VideoId, Signs(""), Embeds());

        src.Should().Be(DataUrl);
    }

    [Theory]
    [InlineData("https://api.example.com")]
    [InlineData("https://api.example.com/")]
    public async Task The_url_is_well_formed_whether_or_not_the_base_has_a_slash(string apiBase)
    {
        var src = await CampaignService.ChooseThumbnailSrcAsync(
            canLink: true, apiBase, VideoId, Signs(), Embeds(null));

        src.Should().NotContain("//api/videos", "a doubled slash is a 404 on some proxies");
        Uri.TryCreate(src, UriKind.Absolute, out _).Should().BeTrue();
    }
}
