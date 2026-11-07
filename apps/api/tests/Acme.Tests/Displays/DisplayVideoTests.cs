using Acme.Application.Displays;
using Acme.Application.Videos;
using Acme.Domain.Common;
using Acme.Domain.Displays;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Displays;

/// <summary>
/// The rules for a video on an in-pharmacy screen, without a database.
/// </summary>
/// <remarks>
/// The integration suite checks the same rules through HTTP; these pin each one
/// alone, so a failure names the rule rather than the endpoint.
/// </remarks>
public class DisplayVideoTests
{
    private const string Owner = "owner-1";

    // One fixed secret, the same in every test, so tests running in parallel
    // cannot sign with one key and validate with another.
    private const string Secret = "display-video-tests-secret-at-least-32-chars";

    public DisplayVideoTests() => VideoDownloadToken.Configure(Secret);

    private static Video AVideo(VideoStatus status, string? blobKey = "videos/v.mp4", string owner = Owner)
    {
        var v = Video.Create(
            ownerId: owner, name: "Flu jabs", topic: "flu vaccination",
            scriptSource: VideoScriptSource.Ai, visualStyle: VideoVisualStyle.Realistic,
            description: null, customScript: null, voiceId: null, styleTone: null);
        Force(v, nameof(Video.Status), status);
        Force(v, nameof(Video.BlobKey), blobKey);
        return v;
    }

    private static void Force(object target, string name, object? value) =>
        target.GetType()
            .GetProperty(name, System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(target, value);

    /* ── which videos may be on a screen ─────────────────────────────── */

    [Fact]
    public void A_ready_video_of_mine_with_a_file_is_showable() =>
        DisplayMedia.IsShowable(AVideo(VideoStatus.Ready), Owner).Should().BeTrue();

    [Theory]
    [InlineData(VideoStatus.Pending)]
    [InlineData(VideoStatus.Rendering)]
    [InlineData(VideoStatus.Failed)]
    public void An_unfinished_video_is_not(VideoStatus status) =>
        DisplayMedia.IsShowable(AVideo(status), Owner).Should().BeFalse(
            "a video mid re-cut or failed would play a stale or missing file on the wall");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Ready_without_a_file_is_not(string? blobKey) =>
        DisplayMedia.IsShowable(AVideo(VideoStatus.Ready, blobKey), Owner).Should().BeFalse();

    [Fact]
    public void Another_pharmacys_video_is_not() =>
        DisplayMedia.IsShowable(AVideo(VideoStatus.Ready, owner: "someone-else"), Owner)
            .Should().BeFalse();

    [Fact]
    public void A_missing_video_is_not() =>
        DisplayMedia.IsShowable(null, Owner).Should().BeFalse();

    /* ── what a kiosk says it can play ───────────────────────────────── */

    [Theory]
    [InlineData("video", true)]
    [InlineData("Video", true)]
    [InlineData("poster, video", true)]
    [InlineData(" video ,x", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("videos", false)]
    [InlineData("poster", false)]
    public void Only_a_kiosk_that_says_video_gets_video(string? header, bool expected) =>
        DisplayMedia.CanPlayVideo(header).Should().Be(expected);

    /* ── the signed link ─────────────────────────────────────────────── */

    private static readonly DateTimeOffset Noon =
        new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_poll_inside_a_window_gets_the_identical_link()
    {
        // A link that differed per poll would change the content revision
        // every thirty seconds and restart the screen's loop each time.
        var first = DisplayMedia.VideoPath("v1", Noon);
        var later = DisplayMedia.VideoPath("v1", Noon.AddHours(5).AddMinutes(59));

        later.Should().Be(first);
    }

    [Fact]
    public void The_link_rotates_at_the_window_edge_and_the_old_one_is_still_good()
    {
        var edge = new DateTimeOffset(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);

        DisplayMedia.VideoPath("v1", edge).Should().NotBe(DisplayMedia.VideoPath("v1", Noon));

        // A kiosk still holding the previous link until its next poll must be
        // able to play it: the old link outlives the rotation by a full window.
        (DisplayMedia.LinkExpiry(Noon) - edge).Should().BeGreaterThanOrEqualTo(DisplayMedia.Window);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(359)]
    public void A_link_is_always_good_for_at_least_one_window(int minutesIn)
    {
        var at = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutesIn);
        var expiry = DisplayMedia.LinkExpiry(at);

        (expiry - at).Should().BeGreaterThan(DisplayMedia.Window);
        (expiry - at).Should().BeLessThanOrEqualTo(DisplayMedia.Window * 2);
    }

    [Fact]
    public void The_link_is_a_same_origin_path_signed_for_that_one_video()
    {
        // Now, not Noon: validation checks the expiry against the real clock.
        var path = DisplayMedia.VideoPath("abc123", DateTimeOffset.UtcNow);

        path.Should().StartWith("/api/display/video/abc123?sig=");
        path.Should().NotContain("videos/", "no blob key reaches the browser");

        var sig = Uri.UnescapeDataString(path[(path.IndexOf("?sig=") + 5)..]);
        VideoDownloadToken.TryValidate(sig, "abc123", expectedThumbnail: false, out _)
            .Should().BeTrue("the API's own signed download must accept it");
        VideoDownloadToken.TryValidate(sig, "other", expectedThumbnail: false, out _)
            .Should().BeFalse("it opens that video and no other");
        VideoDownloadToken.TryValidate(sig, "abc123", expectedThumbnail: true, out _)
            .Should().BeFalse("and not its thumbnail either");
    }

    [Fact]
    public void A_new_cut_is_a_new_link_and_the_signature_is_unchanged()
    {
        // The version keeps a browser-cached old cut from playing after a
        // re-cut; it must not disturb the signature the API checks.
        var cut1 = DisplayMedia.VideoPath("v1", Noon, "a1");
        var cut2 = DisplayMedia.VideoPath("v1", Noon, "b2");

        cut1.Should().NotBe(cut2);
        cut1.Should().EndWith("&v=a1");
        cut1[..cut1.IndexOf("&v=")].Should().Be(DisplayMedia.VideoPath("v1", Noon));
    }

    [Fact]
    public void A_videos_version_moves_when_it_is_updated()
    {
        var video = AVideo(VideoStatus.Ready);
        var before = DisplayMedia.VersionOf(video);

        Force(video, nameof(Video.UpdatedAtUtc), video.UpdatedAtUtc.AddSeconds(1));

        DisplayMedia.VersionOf(video).Should().NotBe(before);
    }

    /* ── the cap ─────────────────────────────────────────────────────── */

    private static PlaylistItem VideoItem(string id) => new(PlaylistItemKind.Video, id, 20);

    [Fact]
    public void A_playlist_holds_up_to_the_video_cap()
    {
        var playlist = Playlist.Create(Owner, "Loop");
        var items = Enumerable.Range(0, Playlist.MaxVideos).Select(i => VideoItem($"v{i}")).ToArray();

        playlist.Invoking(p => p.SetItems(items)).Should().NotThrow();
    }

    [Fact]
    public void One_video_more_is_refused()
    {
        var playlist = Playlist.Create(Owner, "Loop");
        var items = Enumerable.Range(0, Playlist.MaxVideos + 1).Select(i => VideoItem($"v{i}")).ToArray();

        playlist.Invoking(p => p.SetItems(items)).Should().Throw<DomainException>()
            .WithMessage($"*at most {Playlist.MaxVideos} videos*");
    }

    [Fact]
    public void Posters_do_not_count_toward_the_video_cap()
    {
        var playlist = Playlist.Create(Owner, "Loop");
        var items = Enumerable.Range(0, Playlist.MaxVideos)
            .SelectMany(i => new[]
            {
                VideoItem($"v{i}"),
                new PlaylistItem(PlaylistItemKind.Poster, $"p{i}", 10),
            })
            .ToArray();

        playlist.Invoking(p => p.SetItems(items)).Should().NotThrow();
    }
}
