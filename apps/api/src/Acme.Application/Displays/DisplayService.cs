using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Acme.Application.Videos;
using Acme.Domain.Displays;
using Acme.Domain.Posters;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Displays;

/// <summary>
/// Screens and playlists, from the pharmacy's side of the glass.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is scoped by <c>ownerId</c>, which the controller takes from
/// the token's subject. A head office working as a branch therefore manages
/// that branch's screens through the machinery that already exists — no new
/// permission model, and the switch is already audited.
/// </para>
/// <para>
/// The kiosk never reaches this class. Anonymous traffic is served by
/// <see cref="DisplayKioskService"/>, which shares no method with this one, so
/// there is no path where forgetting an owner check hands a device somebody
/// else's content.
/// </para>
/// </remarks>
public sealed class DisplayService(
    IDisplayScreenRepository screens,
    IPlaylistRepository playlists,
    IPosterRepository posters,
    IVideoRepository videos,
    IUserRepository users,
    ILogger<DisplayService> logger)
{
    /* ── screens ──────────────────────────────────────────────────────── */

    public async Task<IReadOnlyList<DisplayScreenDto>> ListScreensAsync(
        string ownerId, CancellationToken ct)
    {
        var all = await screens.ListByOwnerAsync(ownerId, ct);
        var lists = await playlists.ListByOwnerAsync(ownerId, ct);
        var byId = lists.ToDictionary(p => p.Id);

        return all.Select(s => ToDto(s, byId.GetValueOrDefault(s.PlaylistId ?? ""))).ToList();
    }

    public async Task<Result<DisplayScreenDto>> RegisterScreenAsync(
        string ownerId, RegisterScreenRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<DisplayScreenDto>.NotFound("Account not found.");

        var plan = Plans.For(user.Tier);
        if (plan.MaxDisplayScreens == 0)
            return Result<DisplayScreenDto>.Forbidden(
                "In-pharmacy screens are not part of the Free plan. Upgrade to run one.");

        if (plan.MaxDisplayScreens != Plans.Unlimited)
        {
            var existing = await screens.CountByOwnerAsync(ownerId, ct);
            if (existing >= plan.MaxDisplayScreens)
                return Result<DisplayScreenDto>.Forbidden(
                    $"Your {plan.DisplayName} plan covers {plan.MaxDisplayScreens} screen"
                    + $"{(plan.MaxDisplayScreens == 1 ? "" : "s")}. Upgrade to add another.");
        }

        DisplayScreen screen;
        try
        {
            screen = DisplayScreen.Register(
                ownerId, request.Name, request.Location, request.AddressLine);
        }
        catch (Domain.Common.DomainException ex)
        {
            return Result<DisplayScreenDto>.Invalid(ex.Message);
        }

        await WithFreshCodeAsync(screen, () => screens.AddAsync(screen, ct));

        logger.LogInformation(
            "Display screen {ScreenId} registered by {OwnerId}", screen.Id, ownerId);

        return Result<DisplayScreenDto>.Success(ToDto(screen, null));
    }

    public async Task<Result<DisplayScreenDto>> UpdateScreenAsync(
        string ownerId, string screenId, UpdateScreenRequest request, CancellationToken ct)
    {
        var screen = await OwnedScreenAsync(ownerId, screenId, ct);
        if (screen is null) return Result<DisplayScreenDto>.NotFound("Screen not found.");

        try { screen.Update(request.Name, request.Location, request.AddressLine); }
        catch (Domain.Common.DomainException ex) { return Result<DisplayScreenDto>.Invalid(ex.Message); }

        await screens.UpdateAsync(screen, ct);
        return Result<DisplayScreenDto>.Success(await WithPlaylistAsync(screen, ct));
    }

    public async Task<Result<DisplayScreenDto>> AssignPlaylistAsync(
        string ownerId, string screenId, AssignPlaylistRequest request, CancellationToken ct)
    {
        var screen = await OwnedScreenAsync(ownerId, screenId, ct);
        if (screen is null) return Result<DisplayScreenDto>.NotFound("Screen not found.");

        if (!string.IsNullOrWhiteSpace(request.PlaylistId))
        {
            // Checked rather than trusted: a playlist id from another pharmacy
            // would otherwise put their posters on this screen.
            var playlist = await playlists.FindByIdAsync(request.PlaylistId, ct);
            if (playlist is null || playlist.OwnerId != ownerId)
                return Result<DisplayScreenDto>.NotFound("Playlist not found.");
        }

        screen.AssignPlaylist(request.PlaylistId);
        await screens.UpdateAsync(screen, ct);

        return Result<DisplayScreenDto>.Success(await WithPlaylistAsync(screen, ct));
    }

    public async Task<Result<bool>> DeleteScreenAsync(
        string ownerId, string screenId, CancellationToken ct)
    {
        var screen = await OwnedScreenAsync(ownerId, screenId, ct);
        if (screen is null) return Result<bool>.NotFound("Screen not found.");

        await screens.DeleteAsync(screen.Id, ct);
        logger.LogInformation("Display screen {ScreenId} deleted by {OwnerId}", screenId, ownerId);

        return Result<bool>.Success(true);
    }

    /// <summary>Issues a fresh pairing code for an installer to type.</summary>
    public async Task<Result<DisplayScreenDto>> RefreshPairingCodeAsync(
        string ownerId, string screenId, CancellationToken ct)
    {
        var screen = await OwnedScreenAsync(ownerId, screenId, ct);
        if (screen is null) return Result<DisplayScreenDto>.NotFound("Screen not found.");

        screen.IssuePairingCode();
        await WithFreshCodeAsync(screen, () => screens.UpdateAsync(screen, ct));

        return Result<DisplayScreenDto>.Success(await WithPlaylistAsync(screen, ct));
    }

    /// <summary>
    /// Persists a screen, minting another pairing code if this one is taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A unique index stops two screens holding the same live code, which is
    /// what prevents an installer pairing a television to another pharmacy's
    /// screen. The cost is that a collision becomes a write failure — and left
    /// unhandled, that is a 500 shown to somebody standing on a stepladder.
    /// </para>
    /// <para>
    /// Three attempts, because a second collision after a fresh 148-million-way
    /// draw means something is wrong that retrying will not fix.
    /// </para>
    /// </remarks>
    private async Task WithFreshCodeAsync(DisplayScreen screen, Func<Task> write)
    {
        const int attempts = 3;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await write();
                return;
            }
            catch (DuplicatePairingCodeException) when (attempt < attempts)
            {
                logger.LogWarning(
                    "Pairing code collision on screen {ScreenId} (attempt {Attempt}); "
                    + "generating another", screen.Id, attempt);

                screen.IssuePairingCode();
            }
        }
    }

    /// <summary>
    /// Mints a direct link token, shown once.
    /// </summary>
    /// <remarks>
    /// The escape hatch for hardware that cannot take a pairing code — a browser
    /// with no usable keyboard, or a screen somebody has to configure remotely.
    /// It is a worse credential than a paired device by nature, because it
    /// exists as a string somebody can paste anywhere, which is exactly why it
    /// is revocable on its own and is not created unless asked for.
    /// </remarks>
    public async Task<Result<DisplayTokenDto>> IssueDirectLinkAsync(
        string ownerId, string screenId, CancellationToken ct)
    {
        var screen = await OwnedScreenAsync(ownerId, screenId, ct);
        if (screen is null) return Result<DisplayTokenDto>.NotFound("Screen not found.");

        var (raw, hash) = DisplayToken.Issue();
        screen.AddCredential(DisplayCredentialKind.DirectLink, hash);
        await screens.UpdateAsync(screen, ct);

        logger.LogWarning(
            "Direct display link issued for screen {ScreenId} by {OwnerId}. Any previous "
            + "direct link for this screen has stopped working.", screenId, ownerId);

        return Result<DisplayTokenDto>.Success(new DisplayTokenDto(raw, screen.Id, screen.Name));
    }

    public async Task<Result<DisplayScreenDto>> RevokeCredentialAsync(
        string ownerId, string screenId, string credentialId, CancellationToken ct)
    {
        var screen = await OwnedScreenAsync(ownerId, screenId, ct);
        if (screen is null) return Result<DisplayScreenDto>.NotFound("Screen not found.");

        if (!screen.Revoke(credentialId))
            return Result<DisplayScreenDto>.NotFound("That access has already been removed.");

        await screens.UpdateAsync(screen, ct);

        logger.LogWarning(
            "Display credential {CredentialId} on screen {ScreenId} revoked by {OwnerId}",
            credentialId, screenId, ownerId);

        return Result<DisplayScreenDto>.Success(await WithPlaylistAsync(screen, ct));
    }

    /* ── playlists ────────────────────────────────────────────────────── */

    public async Task<IReadOnlyList<PlaylistDto>> ListPlaylistsAsync(
        string ownerId, CancellationToken ct)
    {
        var all = await playlists.ListByOwnerAsync(ownerId, ct);
        if (all.Count == 0) return Array.Empty<PlaylistDto>();

        var allScreens = await screens.ListByOwnerAsync(ownerId, ct);
        var names = await ItemNamesAsync(ownerId, all.SelectMany(p => p.Items), ct);

        return all.Select(p => ToDto(p, names, allScreens)).ToList();
    }

    public async Task<Result<PlaylistDto>> CreatePlaylistAsync(
        string ownerId, UpsertPlaylistRequest request, CancellationToken ct)
    {
        Playlist playlist;
        try { playlist = Playlist.Create(ownerId, request.Name); }
        catch (Domain.Common.DomainException ex) { return Result<PlaylistDto>.Invalid(ex.Message); }

        if (request.Items is { Count: > 0 })
        {
            var items = await ResolveItemsAsync(ownerId, request.Items, ct);
            if (!items.IsSuccess) return Result<PlaylistDto>.Invalid(items.Error!);

            try { playlist.SetItems(items.Value!); }
            catch (Domain.Common.DomainException ex) { return Result<PlaylistDto>.Invalid(ex.Message); }
        }

        await playlists.AddAsync(playlist, ct);
        return Result<PlaylistDto>.Success(await SingleDtoAsync(ownerId, playlist, ct));
    }

    public async Task<Result<PlaylistDto>> UpdatePlaylistAsync(
        string ownerId, string playlistId, UpsertPlaylistRequest request, CancellationToken ct)
    {
        var playlist = await playlists.FindByIdAsync(playlistId, ct);
        if (playlist is null || playlist.OwnerId != ownerId)
            return Result<PlaylistDto>.NotFound("Playlist not found.");

        var items = await ResolveItemsAsync(ownerId, request.Items ?? Array.Empty<PlaylistItemRequest>(), ct);
        if (!items.IsSuccess) return Result<PlaylistDto>.Invalid(items.Error!);

        try
        {
            playlist.Rename(request.Name);
            playlist.SetItems(items.Value!);
        }
        catch (Domain.Common.DomainException ex) { return Result<PlaylistDto>.Invalid(ex.Message); }

        await playlists.UpdateAsync(playlist, ct);
        return Result<PlaylistDto>.Success(await SingleDtoAsync(ownerId, playlist, ct));
    }

    public async Task<Result<bool>> DeletePlaylistAsync(
        string ownerId, string playlistId, CancellationToken ct)
    {
        var playlist = await playlists.FindByIdAsync(playlistId, ct);
        if (playlist is null || playlist.OwnerId != ownerId)
            return Result<bool>.NotFound("Playlist not found.");

        // Screens showing it are left pointing at nothing rather than deleted.
        // A screen is a physical object on a wall; removing the record because
        // somebody tidied up a playlist would be a surprising amount of damage.
        var showing = (await screens.ListByOwnerAsync(ownerId, ct))
            .Where(s => s.PlaylistId == playlistId)
            .ToList();

        foreach (var screen in showing)
        {
            screen.AssignPlaylist(null);
            await screens.UpdateAsync(screen, ct);
        }

        await playlists.DeleteAsync(playlistId, ct);

        if (showing.Count > 0)
        {
            logger.LogInformation(
                "Playlist {PlaylistId} deleted; {Count} screen(s) now have nothing assigned",
                playlistId, showing.Count);
        }

        return Result<bool>.Success(true);
    }

    /* ── helpers ──────────────────────────────────────────────────────── */

    private async Task<DisplayScreen?> OwnedScreenAsync(
        string ownerId, string screenId, CancellationToken ct)
    {
        var screen = await screens.FindByIdAsync(screenId, ct);
        return screen is null || screen.OwnerId != ownerId ? null : screen;
    }

    /// <summary>
    /// Turns requested items into domain items, refusing anything unshowable.
    /// </summary>
    /// <remarks>
    /// A draft is refused here for the same reason a campaign refuses one: a
    /// screen in a shop window is publication. A video must be Ready and the
    /// pharmacy's own — one still rendering, failed or somebody else's is
    /// refused with a sentence, because a slot that silently shows nothing is
    /// worse than a request that explains itself. The kiosk checks again on
    /// every poll, since a video can stop being Ready after it is added.
    /// </remarks>
    private async Task<Result<IReadOnlyList<PlaylistItem>>> ResolveItemsAsync(
        string ownerId, IReadOnlyList<PlaylistItemRequest> requested, CancellationToken ct)
    {
        var resolved = new List<PlaylistItem>(requested.Count);

        foreach (var item in requested)
        {
            // IsDefined as well: TryParse also accepts any number ("7"), which
            // would otherwise be stored as a kind nothing knows how to show.
            if (!Enum.TryParse<PlaylistItemKind>(item.Kind, ignoreCase: true, out var kind)
                || !Enum.IsDefined(kind))
                return Result<IReadOnlyList<PlaylistItem>>.Invalid($"Unknown item type '{item.Kind}'.");

            if (kind == PlaylistItemKind.Video)
            {
                var video = await videos.FindByIdAsync(item.RefId, ct);
                if (video is null || video.OwnerId != ownerId)
                    return Result<IReadOnlyList<PlaylistItem>>.Invalid("That video could not be found.");

                if (!DisplayMedia.IsShowable(video, ownerId))
                    return Result<IReadOnlyList<PlaylistItem>>.Invalid(
                        $"'{video.Name}' is not ready yet. Only finished videos can go on a screen.");

                // The video's own length, not whatever the editor sent: a video
                // plays to its end, so its "duration" here only feeds the loop's
                // total and the kiosk's safety net if `ended` never fires.
                resolved.Add(new PlaylistItem(kind, item.RefId, Math.Clamp(
                    video.DurationSeconds, Playlist.MinDurationSeconds, Playlist.MaxDurationSeconds)));
                continue;
            }

            var poster = await posters.FindByIdAsync(item.RefId, ct);
            if (poster is null || poster.OwnerId != ownerId)
                return Result<IReadOnlyList<PlaylistItem>>.Invalid("That poster could not be found.");

            if (poster.Status != PosterStatus.Published)
                return Result<IReadOnlyList<PlaylistItem>>.Invalid(
                    $"'{poster.Name}' is not published yet. Publish it before putting it on a screen.");

            resolved.Add(new PlaylistItem(kind, item.RefId, item.DurationSeconds));
        }

        return Result<IReadOnlyList<PlaylistItem>>.Success(resolved);
    }

    private async Task<DisplayScreenDto> WithPlaylistAsync(DisplayScreen screen, CancellationToken ct)
    {
        var playlist = screen.PlaylistId is null
            ? null
            : await playlists.FindByIdAsync(screen.PlaylistId, ct);

        return ToDto(screen, playlist);
    }

    private async Task<PlaylistDto> SingleDtoAsync(
        string ownerId, Playlist playlist, CancellationToken ct)
    {
        var names = await ItemNamesAsync(ownerId, playlist.Items, ct);
        var allScreens = await screens.ListByOwnerAsync(ownerId, ct);
        return ToDto(playlist, names, allScreens);
    }

    /// <summary>
    /// Names for every poster and video a playlist mentions, and whether each
    /// is showable right now — keyed by kind and id.
    /// </summary>
    private async Task<Dictionary<(PlaylistItemKind, string), (string Name, bool Playable)>> ItemNamesAsync(
        string ownerId, IEnumerable<PlaylistItem> items, CancellationToken ct)
    {
        var all = items.ToList();
        var result = new Dictionary<(PlaylistItemKind, string), (string, bool)>();

        var posterIds = all.Where(i => i.Kind == PlaylistItemKind.Poster)
            .Select(i => i.RefId).Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in posterIds)
        {
            var poster = await posters.FindByIdAsync(id, ct);

            // A poster that was deleted, or that belongs to somebody else,
            // reads the same way to the editor: it is not showable and it says
            // so, rather than vanishing from the list without explanation.
            result[(PlaylistItemKind.Poster, id)] = poster is null || poster.OwnerId != ownerId
                ? ("Removed poster", false)
                : (poster.Name, poster.Status == PosterStatus.Published);
        }

        var videoIds = all.Where(i => i.Kind == PlaylistItemKind.Video)
            .Select(i => i.RefId).Distinct(StringComparer.Ordinal).ToList();
        var found = (await videos.FindManyByIdsAsync(ownerId, videoIds, ct))
            .ToDictionary(v => v.Id, StringComparer.Ordinal);
        foreach (var id in videoIds)
        {
            // Not playable while it is being re-cut or after it failed: the
            // screen is skipping it, and the editor says so.
            result[(PlaylistItemKind.Video, id)] = found.TryGetValue(id, out var video)
                ? (video.Name, DisplayMedia.IsShowable(video, ownerId))
                : ("Removed video", false);
        }

        return result;
    }

    private static DisplayScreenDto ToDto(DisplayScreen screen, Playlist? playlist) =>
        new(
            screen.Id,
            screen.Name,
            screen.Location,
            screen.AddressLine,
            screen.PlaylistId,
            playlist?.Name,
            playlist?.Items.Count ?? 0,
            screen.IsPaired,
            // Only shown while it is still worth typing. A stale code on screen
            // is a support call waiting to happen.
            screen.PairingCodeIsLive ? screen.PairingCode : null,
            screen.PairingCodeIsLive ? screen.PairingCodeExpiresAtUtc : null,
            screen.LastSeenAtUtc,
            screen.Credentials
                .Where(c => c.IsActive)
                .Select(c => new DisplayCredentialDto(
                    c.Id, c.Kind.ToString(), c.CreatedAtUtc, c.LastSeenAtUtc))
                .ToList(),
            screen.CreatedAtUtc);

    private static PlaylistDto ToDto(
        Playlist playlist,
        IReadOnlyDictionary<(PlaylistItemKind, string), (string Name, bool Playable)> names,
        IReadOnlyList<DisplayScreen> allScreens) =>
        new(
            playlist.Id,
            playlist.Name,
            playlist.Version,
            playlist.Items.Select(i =>
            {
                var known = names.TryGetValue((i.Kind, i.RefId), out var n) ? n : ("Unknown", false);
                return new PlaylistItemDto(
                    i.Kind.ToString(), i.RefId, known.Item1, i.DurationSeconds, known.Item2);
            }).ToList(),
            playlist.Items.Sum(i => i.DurationSeconds),
            allScreens.Where(s => s.PlaylistId == playlist.Id).Select(s => s.Name).ToList(),
            playlist.UpdatedAtUtc);
}
