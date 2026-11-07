using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Application.Social;
using Acme.Application.Videos;
using Acme.Domain.Social;
using Acme.Domain.Videos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Turns a poster or video into a URL the platforms can fetch.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these platforms pulls media from a URL rather than accepting an
/// upload, so whatever this returns must be reachable from the public internet
/// for the whole life of the call — which for an Instagram video means minutes,
/// not seconds. Hence the deliberately generous SAS window.
/// </para>
/// <para>
/// <b>Posters are rendered on demand.</b> Nothing in the product stores a
/// rendered poster: the canvas JSON is the source of truth, previews are drawn
/// in the browser as SVG and PDFs are drawn per request. An earlier version of
/// this class read <c>Poster.ThumbnailBlobKey</c>, which is never populated by
/// anything — so every poster publish failed with a message about the poster
/// not existing. Rendering is now explicit, cached by content hash, and
/// reports its own failures.
/// </para>
/// </remarks>
public sealed class PublishMediaResolver(
    IVideoRepository videos,
    PosterImageService posterImages,
    IBrandAssetService assets,
    ILogger<PublishMediaResolver> log) : IPublishMediaResolver
{
    /// <summary>
    /// Long enough for Instagram to ingest a video, short enough that the link
    /// is useless by the time it turns up in anyone's logs.
    /// </summary>
    private static readonly TimeSpan MediaUrlValidity = TimeSpan.FromHours(2);

    public async Task<ResolvedMedia?> ResolveAsync(
        PublishSourceKind kind, string sourceId, string ownerId, bool forInstagram, CancellationToken ct)
    {
        if (kind == PublishSourceKind.Poster)
        {
            var rendered = await posterImages.EnsureRenderedAsync(sourceId, ownerId, ct);

            if (rendered.Kind != ResultKind.Success)
            {
                // The service already logged the detail. Logged again here at
                // warning level with the poster id, because this is the point
                // where a publish is about to be marked failed.
                log.LogWarning(
                    "Poster {Poster} could not be rendered for publishing: {Error}",
                    sourceId, rendered.Error);
                return null;
            }

            string? instagramUrl = null;
            if (forInstagram)
            {
                var ig = await posterImages.EnsureInstagramVariantAsync(sourceId, ownerId, ct);
                if (ig.Kind == ResultKind.Success)
                    instagramUrl = ig.Value!.Url;
                else
                    log.LogWarning(
                        "Instagram version of poster {Poster} could not be prepared: {Error}",
                        sourceId, ig.Error);
            }

            return new ResolvedMedia(
                rendered.Value!.Url, IsVideo: false, rendered.Value.Name, instagramUrl);
        }

        var video = await videos.FindByIdAsync(sourceId, ct);
        if (video is null || video.OwnerId != ownerId) return null;

        // A video still rendering has no blob yet. Returning null gives the
        // caller a clear "not ready" rather than a broken URL the platform
        // would reject with something unhelpful.
        if (video.Status != VideoStatus.Ready || string.IsNullOrWhiteSpace(video.BlobKey))
        {
            log.LogWarning(
                "Video {Video} is {Status} and has no blob, so it cannot be published",
                video.Id, video.Status);
            return null;
        }

        return new ResolvedMedia(
            assets.GetReadOnlyUrl(video.BlobKey!, MediaUrlValidity).ToString(),
            IsVideo: true,
            video.Name);
    }
}

/* ------------------------------------------------------------------ *
 * Not-yet-implemented adapters
 * ------------------------------------------------------------------ */

/// <summary>
/// Placeholder for Google Business Profile publishing.
/// </summary>
/// <remarks>
/// Registered rather than omitted so the shape is fixed and the failure is
/// legible. A missing publisher produces "no publisher for this platform" deep
/// in the worker; this produces a message a pharmacy can understand and an
/// administrator can act on.
///
/// Implementing it means POSTing to
/// <c>mybusiness.googleapis.com/v4/{parent}/localPosts</c> with a summary,
/// media item and, for Offer and Event types, the corresponding sub-object.
/// </remarks>
internal sealed class GoogleBusinessProfilePublisher : ISocialPublisher
{
    public SocialPlatform Platform => SocialPlatform.GoogleBusinessProfile;

    public bool IsConfigured => false;

    public Task<PublishOutcome> PublishAsync(PublishRequest request, CancellationToken ct) =>
        Task.FromResult(new PublishOutcome(
            false, null, null,
            "Google Business Profile publishing is not available yet.",
            IsAuthFailure: false,
            IsRetryable: false));
}

/// <summary>
/// Placeholder for the Google Ads API.
/// </summary>
/// <remarks>
/// Used when the Google OAuth client is not configured (or Ads is switched
/// off). The real client (<c>GoogleAdsRestClient</c>) needs only the OAuth
/// client — and, for anything beyond a test account, Basic access for its
/// Cloud project (developer tokens were sunset in September 2026). Every method fails clearly rather than
/// silently returning empty results, so a missing configuration cannot be
/// mistaken for "this pharmacy has no campaigns".
/// </remarks>
internal sealed class NotConfiguredGoogleAdsClient : IGoogleAdsClient
{
    private const string Message =
        "Google Ads is not connected on this server yet. It needs the Google OAuth client.";

    public bool IsConfigured => false;

    public Task<IReadOnlyList<AdsAccountSummary>> ListAccessibleCustomersAsync(
        string refreshToken, CancellationToken ct) =>
        throw new InvalidOperationException(Message);

    public Task<AdsCreateResult> CreateCampaignAsync(
        Acme.Domain.Social.GoogleAdsCampaign campaign, AdsAccess access, AdsCreative? creative,
        CancellationToken ct) =>
        Task.FromResult(new AdsCreateResult(false, null, null, null, Message));

    public Task<string?> GetAccountCurrencyAsync(AdsAccess access, CancellationToken ct) =>
        throw new InvalidOperationException(Message);

    public Task<bool> SetCampaignStatusAsync(
        string campaignId, bool enabled, AdsAccess access, CancellationToken ct) =>
        throw new InvalidOperationException(Message);

    public Task<AdsMetrics?> FetchMetricsAsync(string campaignId, AdsAccess access, CancellationToken ct) =>
        throw new InvalidOperationException(Message);
}
