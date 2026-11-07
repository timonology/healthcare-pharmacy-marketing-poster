using System.Security.Cryptography;
using System.Text;
using Acme.Application.Audit;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Application.Videos;
using Acme.Domain.Displays;
using Acme.Domain.Posters;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Displays;

/// <summary>A screen, and the credential it presented.</summary>
public sealed record KioskSession(DisplayScreen Screen, DisplayCredential Credential);

/// <summary>
/// Everything an unauthenticated screen is allowed to ask for.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="DisplayService"/> on purpose. Anonymous traffic and
/// owner traffic share no method here, so there is no route by which a missing
/// owner check on this side quietly exposes the other side's data. Every method
/// begins by resolving a token to exactly one screen, and nothing takes an
/// owner id at all.
/// </para>
/// <para>
/// <b>The payload is public by assumption.</b> A kiosk hangs in a room strangers
/// walk through and can be photographed at any time, so it receives only what a
/// passer-by could already read off the glass: the screen's name, its address
/// line if one is configured, and images of published posters.
/// </para>
/// </remarks>
public sealed class DisplayKioskService(
    IDisplayScreenRepository screens,
    IPlaylistRepository playlists,
    IPosterRepository posters,
    IVideoRepository videos,
    PosterImageService posterImages,
    IPosterImageStore posterImageStore,
    IAuditScope audit,
    ILogger<DisplayKioskService> logger)
{
    /// <summary>
    /// How often a screen asks again.
    /// </summary>
    /// <remarks>
    /// Thirty seconds is fast enough that a pharmacist who changes a playlist
    /// sees it happen while still standing there, and slow enough that a
    /// hundred screens cost two requests a second — nearly all of them 304s
    /// answered from an indexed lookup.
    /// </remarks>
    private const int PollSeconds = 30;

    /* ── pairing ──────────────────────────────────────────────────────── */

    /// <summary>
    /// Exchanges a typed pairing code for a device token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The code is consumed on success, so it works exactly once. A second
    /// device typing the same six characters a minute later gets nothing.
    /// </para>
    /// <para>
    /// Failures are deliberately vague to the caller and specific in the log.
    /// This endpoint is anonymous and the code space is small, so a response
    /// that distinguished "no such code" from "expired" would let somebody
    /// enumerate live codes; the pharmacy can see the truth in the log.
    /// </para>
    /// </remarks>
    public async Task<Result<DisplayTokenDto>> ClaimAsync(
        ClaimScreenRequest request, CancellationToken ct)
    {
        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();

        if (code.Length != DisplayScreen.PairingCodeLength)
            return Result<DisplayTokenDto>.Invalid("That pairing code does not look right.");

        var screen = await screens.FindByPairingCodeAsync(code, ct);

        if (screen is null || !screen.PairingCodeIsLive)
        {
            logger.LogWarning(
                "Display pairing refused for code {Code}: {Reason}",
                code, screen is null ? "no such code" : "code expired");

            return Result<DisplayTokenDto>.Invalid(
                "That code is not valid any more. Generate a new one from the Screens page.");
        }

        var (raw, hash) = DisplayToken.Issue();
        screen.AddCredential(DisplayCredentialKind.PairedDevice, hash);
        screen.ClearPairingCode();
        await screens.UpdateAsync(screen, ct);

        logger.LogInformation(
            "Display screen {ScreenId} ({Name}) paired with a device", screen.Id, screen.Name);

        // The device has no account: file the pairing under the screen's pharmacy.
        audit.SetEntity("display", screen.Id);
        audit.SetPharmacy(screen.OwnerId);

        return Result<DisplayTokenDto>.Success(new DisplayTokenDto(raw, screen.Id, screen.Name));
    }

    /* ── content ──────────────────────────────────────────────────────── */

    /// <summary>Resolves a presented token to a screen, or nothing.</summary>
    public async Task<KioskSession?> ResolveAsync(string? token, CancellationToken ct)
    {
        // Shape first: a scanner throwing junk at this endpoint costs a length
        // check rather than a database round trip.
        if (!DisplayToken.LooksWellFormed(token)) return null;

        var hash = DisplayToken.HashOf(token!);
        var screen = await screens.FindByCredentialHashAsync(hash, ct);
        if (screen is null) return null;

        // The repository query already filters to live credentials, but the
        // domain is what decides what "live" means, so it gets the last word.
        var credential = screen.MatchCredential(hash);
        return credential is null ? null : new KioskSession(screen, credential);
    }

    /// <summary>
    /// What to show, and how long to show each thing for.
    /// </summary>
    /// <remarks>
    /// Unplayable slots are dropped rather than reported. A poster that has been
    /// unpublished should leave the screen immediately — that is the point of
    /// unpublishing — and the pharmacy sees it struck through in the playlist
    /// editor, which is where somebody can actually do something about it.
    /// </remarks>
    public async Task<KioskContentDto> ContentAsync(
        KioskSession session, CancellationToken ct, bool canPlayVideo = false,
        DateTimeOffset? now = null)
    {
        var screen = session.Screen;
        var slides = new List<KioskSlideDto>();
        var at = now ?? DateTimeOffset.UtcNow;

        var playlist = screen.PlaylistId is null
            ? null
            : await playlists.FindByIdAsync(screen.PlaylistId, ct);

        if (playlist is not null && playlist.OwnerId == screen.OwnerId)
        {
            // One read per kind for the whole playlist, not one per slide.
            //
            // This is the hot path of the entire feature: every screen in every
            // pharmacy runs it every thirty seconds for years. Resolving items
            // one at a time made a fifty-item loop fifty queries per poll per
            // screen, and the ETag does not rescue that — the content has to be
            // built before its revision can be computed, so a 304 costs exactly
            // as much database work as a 200.
            //
            // Both reads are scoped to the owner in the query, so a playlist
            // that somehow referenced another pharmacy's poster or video
            // resolves to nothing rather than relying on a check further down.
            var posterIds = IdsOf(playlist, PlaylistItemKind.Poster);
            var postersById = (await posters.FindManyByIdsAsync(screen.OwnerId, posterIds, ct))
                .ToDictionary(p => p.Id, StringComparer.Ordinal);

            var videoIds = canPlayVideo ? IdsOf(playlist, PlaylistItemKind.Video) : new List<string>();
            var videosById = (await videos.FindManyByIdsAsync(screen.OwnerId, videoIds, ct))
                .ToDictionary(v => v.Id, StringComparer.Ordinal);

            foreach (var item in playlist.Items)
            {
                if (item.Kind == PlaylistItemKind.Poster)
                {
                    if (!postersById.TryGetValue(item.RefId, out var poster)
                        || poster.Status != PosterStatus.Published)
                    {
                        continue;
                    }

                    // A path, not a URL with the token in it. The device fetches
                    // this with the token in a header, which keeps the
                    // credential out of access logs, out of Referer, and out of
                    // anything a proxy writes down.
                    var path = $"/api/display/slides/{poster.Id}";
                    slides.Add(new KioskSlideDto(
                        "Poster", path, poster.Name, item.DurationSeconds, path));
                }
                else if (item.Kind == PlaylistItemKind.Video && canPlayVideo)
                {
                    // Ready and theirs, checked on every poll: a video deleted,
                    // failed or being re-cut drops off the screen now, not when
                    // someone next edits the playlist.
                    if (!videosById.TryGetValue(item.RefId, out var video)
                        || !DisplayMedia.IsShowable(video, screen.OwnerId))
                    {
                        continue;
                    }

                    // The video's own length, read now rather than the one
                    // saved with the playlist: a re-cut can change it. The
                    // kiosk moves on when the video ends; this is its safety net.
                    var seconds = Math.Clamp(
                        video.DurationSeconds, Playlist.MinDurationSeconds, Playlist.MaxDurationSeconds);

                    slides.Add(new KioskSlideDto(
                        "Video", string.Empty, video.Name, seconds,
                        DisplayMedia.VideoPath(video.Id, at, DisplayMedia.VersionOf(video))));
                }
            }
        }

        return new KioskContentDto(
            screen.Name,
            screen.AddressLine,
            slides,
            Revision(screen, playlist, slides),
            PollSeconds);
    }

    /// <summary>
    /// Streams one published poster, scoped to the screen that asked.
    /// </summary>
    /// <remarks>
    /// The poster must be on this screen's own playlist. Without that, a valid
    /// token for any screen would be a key to every poster the pharmacy has
    /// ever published, which is a much larger door than the one being opened.
    /// </remarks>
    public async Task<Result<PosterImageStream>> SlideAsync(
        KioskSession session, string posterId, CancellationToken ct)
    {
        var screen = session.Screen;
        if (screen.PlaylistId is null)
            return Result<PosterImageStream>.NotFound("Nothing is assigned to this screen.");

        var playlist = await playlists.FindByIdAsync(screen.PlaylistId, ct);
        if (playlist is null || playlist.OwnerId != screen.OwnerId)
            return Result<PosterImageStream>.NotFound("Nothing is assigned to this screen.");

        var onPlaylist = playlist.Items.Any(
            i => i.Kind == PlaylistItemKind.Poster && i.RefId == posterId);

        if (!onPlaylist)
            return Result<PosterImageStream>.NotFound("That poster is not on this screen.");

        var poster = await posters.FindByIdAsync(posterId, ct);
        if (poster is null
            || poster.OwnerId != screen.OwnerId
            || poster.Status != PosterStatus.Published)
        {
            return Result<PosterImageStream>.NotFound("That poster is not available.");
        }

        // The same cached render the campaign email uses, so a screen and an
        // email never disagree about what a poster looks like.
        var rendered = await posterImages.EnsureRenderedAsync(poster.Id, screen.OwnerId, ct);
        if (rendered.Kind != ResultKind.Success)
        {
            logger.LogWarning(
                "Screen {ScreenId} could not be given poster {PosterId}: {Error}",
                screen.Id, posterId, rendered.Error);

            return Result<PosterImageStream>.Invalid(
                rendered.Error ?? "That poster could not be rendered.");
        }

        var content = await posterImageStore.OpenReadAsync(rendered.Value!.BlobKey, ct);
        return Result<PosterImageStream>.Success(
            new PosterImageStream(content, "image/png", rendered.Value!.BlobKey));
    }

    /// <summary>Records that a screen is alive, without rewriting its document.</summary>
    private static List<string> IdsOf(Playlist playlist, PlaylistItemKind kind) =>
        playlist.Items
            .Where(i => i.Kind == kind)
            .Select(i => i.RefId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public Task NoteSeenAsync(KioskSession session, CancellationToken ct) =>
        screens.TouchLastSeenAsync(
            session.Screen.Id, session.Credential.Id, DateTime.UtcNow, ct);

    /// <summary>
    /// A short stable string that changes when, and only when, the picture does.
    /// </summary>
    /// <remarks>
    /// Built from the playlist's version and the two screen fields that are
    /// actually drawn. A playlist rename must not move it: every screen would
    /// drop its cache and re-download every image because somebody fixed a
    /// spelling mistake. The slide list is folded in too, so a poster being
    /// unpublished — which changes nothing on either record — still reaches the
    /// screen on the next poll.
    /// </remarks>
    private static string Revision(
        DisplayScreen screen, Playlist? playlist, IReadOnlyList<KioskSlideDto> slides)
    {
        var seed = new StringBuilder()
            .Append(playlist?.Id ?? "none").Append('|')
            .Append(playlist?.Version ?? 0).Append('|')
            .Append(screen.Name).Append('|')
            .Append(screen.AddressLine).Append('|');

        foreach (var slide in slides)
            seed.Append(slide.Kind).Append(':')
                .Append(slide.MediaUrl ?? slide.ImageUrl).Append(':')
                .Append(slide.DurationSeconds).Append(',');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

/// <summary>A rendered poster on its way to a screen.</summary>
public sealed record PosterImageStream(Stream Content, string ContentType, string BlobKey);
