using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Export;
using Acme.Application.Posters;
using Acme.Application.Social;
using Acme.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Builds the Performance Max image set from a poster and the brand kit logo.
/// </summary>
/// <remarks>
/// <para>
/// Google needs a 1.91:1 landscape and a 1:1 square marketing image and a 1:1
/// logo, and rejects anything else with ASPECT_RATIO_NOT_ALLOWED when the
/// assets are linked. A poster is A4 portrait, so it is fitted whole into each
/// frame on its own edge colour — never cropped, because cropping an A4 design
/// cuts off the headline or the contact line. The logo is fitted on white.
/// </para>
/// <para>
/// Built from the same cached render as everything else, so it shows the
/// poster exactly as published, watermark rules included.
/// </para>
/// </remarks>
public sealed class AdsCreativeBuilder(
    IPosterRepository posterRecords,
    PosterImageService posters,
    IPosterImageStore store,
    IImageTranscoder transcoder,
    IBrandKitRepository brandKits,
    IBrandAssetService brandAssets,
    ILogger<AdsCreativeBuilder> log) : IAdsCreativeBuilder
{
    /// <summary>Google's recommended sizes; comfortably above the minimums.</summary>
    public const int LandscapeWidth = 1200, LandscapeHeight = 628, SquareSize = 1200;

    public async Task<Result<AdsCreative>> BuildAsync(string ownerId, string posterId, CancellationToken ct)
    {
        // The same sign-off rule as publishing to social: an ad is the poster
        // in public, so a draft does not go out.
        var poster = await posterRecords.FindByIdAsync(posterId, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<AdsCreative>.NotFound("That poster no longer exists.");
        if (PosterUsage.WhyCannotSend(poster, "used in an ad") is { } notYet)
            return Result<AdsCreative>.Invalid(notYet);

        var rendered = await posters.EnsureRenderedAsync(posterId, ownerId, ct);
        if (rendered.Kind != ResultKind.Success)
            return Result<AdsCreative>.Invalid(rendered.Error ?? "That poster could not be turned into an image.");

        byte[] png;
        await using (var stream = await store.OpenReadAsync(rendered.Value!.BlobKey, ct))
            png = await ReadAllAsync(stream, ct);

        var landscape = transcoder.FitToFrameJpeg(png, LandscapeWidth, LandscapeHeight);
        var square = transcoder.FitToFrameJpeg(png, SquareSize, SquareSize);
        if (landscape is null || square is null)
            return Result<AdsCreative>.Invalid("The poster could not be prepared for Google Ads.");

        var logo = await LogoAsync(ownerId, ct);
        if (logo is null)
            return Result<AdsCreative>.Invalid(
                "Performance Max needs your logo. Add one in Brand kit, then try again.");

        return Result<AdsCreative>.Success(new AdsCreative(landscape, square, logo));
    }

    private async Task<byte[]?> LogoAsync(string ownerId, CancellationToken ct)
    {
        var kit = await brandKits.FindByOwnerAsync(ownerId, ct);
        if (kit?.LogoBlobKey is not { Length: > 0 } key) return null;

        await using var stream = await brandAssets.OpenLogoAsync(key, ct);
        if (stream is null) return null;

        var bytes = await ReadAllAsync(stream, ct);

        // Vector logos are common and Google takes rasters only. Same
        // rasteriser, and the same refusal of external references, as the
        // poster renderer.
        if (SvgRasterizer.LooksLikeSvg(bytes))
        {
            bytes = SvgRasterizer.ToPng(bytes) ?? Array.Empty<byte>();
            if (bytes.Length == 0)
            {
                log.LogWarning("Brand kit logo for {Owner} is an SVG that could not be rasterised", ownerId);
                return null;
            }
        }

        return transcoder.FitToFrameJpeg(bytes, SquareSize, SquareSize, whiteBackground: true);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
