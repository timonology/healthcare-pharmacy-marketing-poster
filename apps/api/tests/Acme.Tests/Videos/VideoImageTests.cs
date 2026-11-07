using System.Text.RegularExpressions;
using Acme.Application.Videos;
using Acme.Domain.Common;
using Acme.Domain.Videos;
using FluentAssertions;
using Xunit;

namespace Acme.Tests.Videos;

/// <summary>
/// The pictures a pharmacist attaches to their own video.
/// </summary>
/// <remarks>
/// <para>
/// Three things are worth holding onto here, and they fail in different ways.
/// </para>
/// <para>
/// <b>The limits have to refuse the right files.</b> Chiefly SVG: the brand-kit
/// logo accepts one because a renderer that understands vectors draws it, but
/// these go to ffmpeg, which rasterises nothing. An SVG accepted here uploads
/// cleanly, shows a thumbnail, and then silently is not in the video.
/// </para>
/// <para>
/// <b>The limit has to be true away from the edge.</b> The controller's check
/// is for the pharmacist's benefit — it produces a sentence rather than an
/// exception — but a rule that lives only at the edge is one the next caller
/// walks around, so the domain is checked directly here too.
/// </para>
/// <para>
/// <b>A bad row must not make a video unreadable.</b> Hydration runs on every
/// list and every poll. One malformed entry costs a beat of stock footage; a
/// throw costs the pharmacist their whole video list.
/// </para>
/// </remarks>
public class VideoImageTests
{
    private static VideoImage Image(string key = "videos/o/attachments/a.jpg") =>
        VideoImage.Create(key, VideoImageSource.Upload, null);

    private static Video CreateWith(IReadOnlyList<VideoImage>? images) =>
        Video.Create(
            "owner-1", "Flu clinic", "flu jabs",
            VideoScriptSource.Ai, VideoVisualStyle.Realistic,
            null, null, null, null, images);

    // ── what may be attached ────────────────────────────────────────────

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("IMAGE/JPEG")] // browsers are not consistent about case
    public void A_photograph_of_an_allowed_type_is_accepted(string contentType)
    {
        VideoImagePolicy.WhyFileRefused(contentType, 2 * 1024 * 1024).Should().BeNull();
    }

    [Fact]
    public void An_svg_is_refused_even_though_the_logo_upload_takes_one()
    {
        // The one refusal that is not obvious from the outside, and the one
        // whose absence would fail silently rather than loudly.
        VideoImagePolicy.WhyFileRefused("image/svg+xml", 4096)
            .Should().Be("Images must be JPEG, PNG or WebP.");
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_ffmpeg_would_not_turn_into_a_beat_is_refused(string? contentType)
    {
        VideoImagePolicy.WhyFileRefused(contentType, 4096).Should().NotBeNull();
    }

    [Fact]
    public void A_file_over_the_cap_is_refused_and_told_how_big_it_was()
    {
        // "Too big" without a number leaves the pharmacist guessing how much to
        // shrink it by; a phone photograph is often only just over.
        var why = VideoImagePolicy.WhyFileRefused("image/jpeg", 12 * 1024 * 1024);

        why.Should().Contain("8MB").And.Contain("12MB");
    }

    [Fact]
    public void A_file_barely_over_the_cap_does_not_claim_to_be_exactly_the_cap()
    {
        // The commonest refusal there is, and it used to read "Images must be
        // under 8MB. That one is 8MB." — a sentence that contradicts itself and
        // tells the pharmacist nothing to act on. The size is rounded up now.
        var why = VideoImagePolicy.WhyFileRefused("image/jpeg", VideoImagePolicy.MaxBytes + 1);

        why.Should().NotBeNull();
        // Written as the sentence that must not appear rather than the one that
        // must: the figure is formatted in the machine's culture, so asserting
        // "8.1MB" would fail on a comma-decimal locale for no useful reason.
        why.Should().NotContain("That one is 8MB",
            "the size shown must be visibly over the limit it exceeded");
    }

    [Fact]
    public void A_file_exactly_on_the_cap_is_accepted()
    {
        // An off-by-one here refuses a file the dialog told the pharmacist was
        // within the limit.
        VideoImagePolicy.WhyFileRefused("image/jpeg", VideoImagePolicy.MaxBytes)
            .Should().BeNull();
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        VideoImagePolicy.WhyFileRefused("image/jpeg", 0).Should().NotBeNull();
    }

    // ── how many ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)] // attaching nothing is the ordinary case
    [InlineData(1)]
    [InlineData(4)]
    public void Up_to_the_limit_is_allowed(int count)
    {
        VideoImagePolicy.WhyCountRefused(count).Should().BeNull();
    }

    [Fact]
    public void More_than_the_limit_is_refused_with_both_numbers()
    {
        var why = VideoImagePolicy.WhyCountRefused(5);

        why.Should().Contain("4").And.Contain("5");
    }

    // ── the request carries them through to the video ───────────────────

    [Fact]
    public void A_create_request_may_carry_no_images_at_all()
    {
        // Every video queued before this feature existed, and most after it.
        var request = new CreateVideoRequest("flu jabs");

        request.Images.Should().BeNull();
        CreateWith(null).Images.Should().BeEmpty();
    }

    [Fact]
    public void A_create_request_accepts_images()
    {
        var request = new CreateVideoRequest("flu jabs")
        {
            Images = new[]
            {
                new VideoImageDto("videos/o/attachments/shopfront.jpg"),
                new VideoImageDto("brand/o/logo.png", "logo"),
            },
        };

        request.Images.Should().HaveCount(2);
        request.Images![0].Source.Should().Be("upload", "that is the default source");
        request.Images![1].Source.Should().Be("logo");
    }

    [Fact]
    public void The_pictures_land_on_the_video_in_the_order_they_were_attached()
    {
        // The order in the dialog is the order the pharmacist arranged them in,
        // and the worker pins them to beats in that order.
        var video = CreateWith(new[]
        {
            Image("videos/o/attachments/1.jpg"),
            Image("videos/o/attachments/2.jpg"),
            Image("videos/o/attachments/3.jpg"),
        });

        video.Images.Select(i => i.BlobKey).Should().ContainInOrder(
            "videos/o/attachments/1.jpg",
            "videos/o/attachments/2.jpg",
            "videos/o/attachments/3.jpg");
    }

    [Fact]
    public void The_domain_refuses_more_than_the_limit_even_though_the_service_checks_first()
    {
        var five = Enumerable.Range(0, 5).Select(n => Image($"k{n}.jpg")).ToArray();

        var attach = () => CreateWith(five);

        attach.Should().Throw<DomainException>().WithMessage("*4*");
    }

    [Fact]
    public void An_image_with_no_blob_key_is_refused_rather_than_stored_unusable()
    {
        var create = () => VideoImage.Create("   ", VideoImageSource.Upload, null);

        create.Should().Throw<DomainException>();
    }

    // ── surviving storage ───────────────────────────────────────────────

    /// <remarks>
    /// Read from the source rather than exercised, because <c>VideoDocument</c>
    /// and <c>VideoMapper</c> are <c>internal</c> and this codebase has twice
    /// declined to open them with <c>InternalsVisibleTo</c>
    /// (<c>PosterImageService.BuildKey</c> and <c>CampaignService</c> both say
    /// so). <c>VideoDocumentOwnershipTests</c> already reads this same file for
    /// the same reason, so this follows it rather than inventing a second
    /// approach.
    /// </remarks>
    [Fact]
    public void A_video_with_no_pictures_stores_no_images_field_at_all()
    {
        // Not an empty array on every row. The worker reads `doc.get("images")`
        // and an absent field is what it expects; `[]` on every video would also
        // be a write to a collection this feature otherwise leaves alone.
        var source = File.ReadAllText(
            Path.Combine(FindInfrastructureDir(), "Persistence", "VideoDocument.cs"));

        var declaration = Regex.Match(
            source, @"\[BsonElement\(""images""\)\][\s\S]{0,200}?public\s+List<VideoImageDocument>\?\s+Images");

        declaration.Success.Should().BeTrue(
            "VideoDocument should declare a nullable images list");
        declaration.Value.Should().Contain("BsonIgnoreIfNull",
            "without it, a video with no pictures writes images: [] to every row");
    }

    // ── framing: focal point and zoom ───────────────────────────────────

    [Fact]
    public void A_picture_nobody_framed_is_centred_at_the_original_crop()
    {
        var image = Image();

        image.FocalX.Should().Be(VideoImage.Centre);
        image.FocalY.Should().Be(VideoImage.Centre);
        image.Zoom.Should().Be(1.0, "1.0 is the cover crop every picture had before zoom");
    }

    [Fact]
    public void A_request_that_says_nothing_about_zoom_means_one()
    {
        // Older clients and the smoke test send no zoom at all; a null is the
        // same thing said explicitly, and must not refuse the whole request.
        new VideoImageDto("videos/o/attachments/a.jpg").Zoom.Should().BeNull();
        new VideoImageDto("videos/o/attachments/a.jpg", Zoom: null).Zoom.Should().BeNull();

        var service = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "apps", "api", "src", "Acme.Application", "Videos", "VideoService.cs"));
        service.Should().Contain("i.Zoom ?? VideoImage.ZoomDefault",
            "an absent zoom has to become 1.0 on the way into the domain");
    }

    [Theory]
    [InlineData(0.6, 0.6)]
    [InlineData(1.3, 1.3)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.5, 1.5)]
    [InlineData(0.1, 0.5)]
    [InlineData(-2.0, 0.5)]
    [InlineData(9.0, 1.5)]
    public void A_zoom_is_kept_within_its_bounds(double sent, double stored)
    {
        VideoImage.Create("videos/o/attachments/a.jpg", VideoImageSource.Upload, null, zoom: sent)
            .Zoom.Should().Be(stored);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void An_unreadable_zoom_is_one_rather_than_a_refusal(double sent)
    {
        // Infinity is not "as far in as possible" — nobody chose it.
        VideoImage.ClampZoom(sent).Should().Be(1.0);
    }

    [Fact]
    public void An_old_row_with_no_zoom_reads_as_one_and_is_not_migrated()
    {
        // Nullable in storage: a plain double reads a missing field as 0, which
        // would clamp to 0.5 and shrink every photo attached before zoom existed.
        var document = File.ReadAllText(
            Path.Combine(FindInfrastructureDir(), "Persistence", "VideoDocument.cs"));
        Regex.IsMatch(document, @"\[BsonElement\(""zoom""\)\]\s*public\s+double\?\s+Zoom")
            .Should().BeTrue("the stored zoom must be nullable so absent is distinguishable from 0");

        var mapper = File.ReadAllText(
            Path.Combine(FindInfrastructureDir(), "Persistence", "VideoMapper.cs"));
        mapper.Should().Contain("d.Zoom ?? VideoImage.ZoomDefault",
            "an absent zoom has to hydrate as 1.0");
        mapper.Should().Contain("Zoom = i.Zoom", "a chosen zoom has to be written back");
    }

    [Fact]
    public void The_zoom_bounds_agree_in_all_three_languages()
    {
        // The browser clamps the slider, the API clamps what it stores, the
        // worker clamps what it renders. Three copies of two numbers.
        var repo = FindRepoRoot();
        var ts = Path.Combine(repo, "packages", "shared-types", "src", "framing.ts");
        var py = Path.Combine(repo, "apps", "ai-service", "app", "video_agent", "framing.py");

        DecimalFrom(ts, @"VIDEO_IMAGE_ZOOM_MIN\s*=\s*([\d.]+)").Should().Be(VideoImage.ZoomMin);
        DecimalFrom(ts, @"VIDEO_IMAGE_ZOOM_MAX\s*=\s*([\d.]+)").Should().Be(VideoImage.ZoomMax);
        DecimalFrom(ts, @"VIDEO_IMAGE_ZOOM_DEFAULT\s*=\s*([\d.]+)").Should().Be(VideoImage.ZoomDefault);
        DecimalFrom(py, @"ZOOM_MIN\s*=\s*([\d.]+)").Should().Be(VideoImage.ZoomMin);
        DecimalFrom(py, @"ZOOM_MAX\s*=\s*([\d.]+)").Should().Be(VideoImage.ZoomMax);
        DecimalFrom(py, @"ZOOM_DEFAULT\s*=\s*([\d.]+)").Should().Be(VideoImage.ZoomDefault);
    }

    private static double DecimalFrom(string path, string pattern)
    {
        File.Exists(path).Should().BeTrue($"{path} is read by this test");
        var match = Regex.Match(File.ReadAllText(path), pattern);
        match.Success.Should().BeTrue($"/{pattern}/ should match something in {path}");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── whose picture is it ─────────────────────────────────────────────

    // Built the way the services that mint them build them, rather than pasted,
    // so a prefix that moves breaks this loudly instead of quietly passing.
    private const string Owner = "owner-1";
    private const string Stranger = "owner-2";

    private static string Attachment(string o) => $"videos/{o}/attachments/{Guid.NewGuid():N}.jpg";
    private static string Poster(string o) => $"posters/{o}/{Guid.NewGuid():N}-abc.png";
    private static string Logo(string o) => $"brand/logos/{o}/{Guid.NewGuid():N}.png";

    [Fact]
    public void An_owner_may_attach_their_own_upload_poster_or_logo()
    {
        // All three sources the dialog offers. None of these may be refused, or
        // the feature does not work.
        VideoImagePolicy.WhyKeyRefused(Attachment(Owner), Owner).Should().BeNull();
        VideoImagePolicy.WhyKeyRefused(Poster(Owner), Owner).Should().BeNull();
        VideoImagePolicy.WhyKeyRefused(Logo(Owner), Owner).Should().BeNull();
    }

    [Fact]
    public void Another_pharmacys_blob_key_is_refused()
    {
        // The key arrives in the request body. Accepting a stranger's would
        // render their picture into this pharmacy's video *and* delete their
        // blob when that video was deleted — disclosure and destruction from
        // one missing check.
        VideoImagePolicy.WhyKeyRefused(Attachment(Stranger), Owner).Should().NotBeNull();
        VideoImagePolicy.WhyKeyRefused(Poster(Stranger), Owner).Should().NotBeNull();
        VideoImagePolicy.WhyKeyRefused(Logo(Stranger), Owner).Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("/videos/owner-1/attachments/a.jpg")]      // leading slash
    [InlineData("videos/owner-1/attachments/../../x.png")] // relative segment
    [InlineData("posters/owner-10/a.png")]                 // owner-1 is a prefix of owner-10
    [InlineData("videos/owner-1/a.jpg")]                   // right owner, not an attachment
    [InlineData("secrets/owner-1/a.jpg")]                  // unknown prefix
    public void A_key_that_is_not_plainly_this_owners_is_refused(string? key)
    {
        VideoImagePolicy.WhyKeyRefused(key, Owner).Should().NotBeNull();
    }

    [Fact]
    public void The_refusal_never_says_which_part_of_the_key_was_wrong()
    {
        // Same sentence for every rejection. The dialog cannot reach these, so
        // the only caller reading them is one probing keys by hand.
        var refusals = new[]
        {
            VideoImagePolicy.WhyKeyRefused(Attachment(Stranger), Owner),
            VideoImagePolicy.WhyKeyRefused("secrets/owner-1/a.jpg", Owner),
            VideoImagePolicy.WhyKeyRefused("", Owner),
        };

        refusals.Should().OnlyContain(r => r == "That image could not be attached.");
    }

    [Fact]
    public void Only_uploads_are_the_videos_to_delete()
    {
        // A logo or poster borrowed as B-roll is still on screen elsewhere.
        // Deleting the video that borrowed it must not delete it.
        VideoImagePolicy.IsOwnAttachment(Attachment(Owner), Owner).Should().BeTrue();
        VideoImagePolicy.IsOwnAttachment(Logo(Owner), Owner).Should().BeFalse();
        VideoImagePolicy.IsOwnAttachment(Poster(Owner), Owner).Should().BeFalse();
        VideoImagePolicy.IsOwnAttachment(Attachment(Stranger), Owner).Should().BeFalse();
    }

    // ── the same number, in four languages ──────────────────────────────

    [Fact]
    public void The_limit_agrees_everywhere_it_is_written_down()
    {
        // This number exists four times — twice in C#, once in TypeScript for
        // the dialog, once in Python for the worker — because each side has to
        // be true on its own. Copies drifting apart is the fault shape that has
        // caused most of what has gone wrong in this codebase, so the copies are
        // read here rather than trusted.
        var repo = FindRepoRoot();

        VideoImagePolicy.MaxImages.Should().Be(Video.VideoImageLimit);

        NumberFrom(
            Path.Combine(repo, "packages", "shared-types", "src", "video.ts"),
            @"MAX_VIDEO_IMAGES\s*=\s*(\d+)")
            .Should().Be(VideoImagePolicy.MaxImages, "shared-types drives the create dialog");

        NumberFrom(
            Path.Combine(repo, "apps", "ai-service", "app", "video_agent", "user_media.py"),
            @"MAX_USER_IMAGES\s*=\s*(\d+)")
            .Should().Be(VideoImagePolicy.MaxImages, "the worker decides what actually renders");
    }

    [Fact]
    public void The_size_cap_agrees_with_the_browsers_copy_of_it()
    {
        // The browser needs this number to refuse an oversized file *before*
        // uploading it. It cannot ask: `[RequestSizeLimit]` rejects the request
        // in the framework, before the action runs, so the answer is a bare 413
        // with no body — which is how a 20MB photograph uploaded in full and
        // then failed without saying why.
        //
        // A copy, therefore, and one that drifts silently in the safe-looking
        // direction: raise the server limit alone and the browser refuses files
        // the API would have taken.
        var megabytes = NumberFrom(
            Path.Combine(FindRepoRoot(), "packages", "shared-types", "src", "video.ts"),
            @"MAX_VIDEO_IMAGE_BYTES\s*=\s*(\d+)\s*\*\s*1024\s*\*\s*1024");

        (megabytes * 1024L * 1024L).Should().Be(
            VideoImagePolicy.MaxBytes,
            "the browser's cap and the server's have to be the same number");
    }

    [Fact]
    public void The_accepted_types_agree_with_the_browsers_copy_of_them()
    {
        // The other half of the same copy. Pinning only the size cap would leave
        // this list free to drift, and it drifts quietly in both directions:
        // add a format here and the browser refuses files the API would take;
        // add it there and the pharmacist gets a thumbnail for a file that is
        // refused a second later.
        var source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "packages", "shared-types", "src", "video.ts"));

        var declaration = Regex.Match(
            source, @"VIDEO_IMAGE_TYPES\s*=\s*\[([^\]]*)\]");
        declaration.Success.Should().BeTrue("shared-types should declare VIDEO_IMAGE_TYPES");

        var browserTypes = Regex.Matches(declaration.Groups[1].Value, @"""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        browserTypes.Should().BeEquivalentTo(
            VideoImagePolicy.AllowedContentTypes,
            "the two lists decide the same question and must answer it the same way");
    }

    private static int NumberFrom(string path, string pattern)
    {
        File.Exists(path).Should().BeTrue($"{path} is read by this test");

        var match = Regex.Match(File.ReadAllText(path), pattern);
        match.Success.Should().BeTrue($"/{pattern}/ should match something in {path}");
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>Walks up from the test binary to the Infrastructure sources.</summary>
    private static string FindInfrastructureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Acme.Infrastructure");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find apps/api/src/Acme.Infrastructure from the test output "
            + "directory. This test reads the repository source, so it needs the "
            + "tree rather than just the compiled assembly.");
    }

    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "packages", "shared-types")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root from the test output directory. "
            + "This test reads the TypeScript and Python copies of the image "
            + "limit, so it needs the tree rather than just the assemblies.");
    }
}
