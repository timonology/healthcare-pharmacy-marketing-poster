using Acme.Domain.Displays;

namespace Acme.Application.Displays;

/// <summary>Two screens were about to hold the same live pairing code.</summary>
/// <remarks>
/// <para>
/// Six characters from a 23-letter alphabet is about 148 million codes, so this
/// is rare — but over years of installations it is not never, and the unique
/// index is what stops the bad outcome: without it, two screens could share a
/// code and an installer could pair a television to <i>another pharmacy's</i>
/// screen record.
/// </para>
/// <para>
/// Translated out of the storage layer so the service can simply generate
/// another code, rather than the caller receiving a 500 for something the
/// server can resolve by itself.
/// </para>
/// </remarks>
public sealed class DuplicatePairingCodeException : Exception
{
    public DuplicatePairingCodeException()
        : base("That pairing code is already in use.") { }
}

/* ── repositories ─────────────────────────────────────────────────────── */

public interface IDisplayScreenRepository
{
    Task<IReadOnlyList<DisplayScreen>> ListByOwnerAsync(string ownerId, CancellationToken ct);
    Task<int> CountByOwnerAsync(string ownerId, CancellationToken ct);
    Task<DisplayScreen?> FindByIdAsync(string id, CancellationToken ct);

    /// <summary>
    /// Finds the screen holding a live credential with this hash.
    /// </summary>
    /// <remarks>
    /// The kiosk's hot path: one indexed read per poll, no owner in the query
    /// because the token is the only thing the device knows about itself.
    /// </remarks>
    Task<DisplayScreen?> FindByCredentialHashAsync(string tokenHash, CancellationToken ct);

    /// <summary>Finds a screen by an outstanding pairing code, expired or not.</summary>
    /// <remarks>
    /// Expiry is checked by the domain rather than the query, so the service can
    /// tell "that code has expired" from "no such code" and say the useful one
    /// to somebody standing at a television.
    /// </remarks>
    Task<DisplayScreen?> FindByPairingCodeAsync(string code, CancellationToken ct);

    Task AddAsync(DisplayScreen screen, CancellationToken ct);
    Task UpdateAsync(DisplayScreen screen, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>Records a poll without rewriting the whole document.</summary>
    Task TouchLastSeenAsync(string screenId, string credentialId, DateTime seenAtUtc, CancellationToken ct);
}

public interface IPlaylistRepository
{
    Task<IReadOnlyList<Playlist>> ListByOwnerAsync(string ownerId, CancellationToken ct);
    Task<Playlist?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(Playlist playlist, CancellationToken ct);
    Task UpdateAsync(Playlist playlist, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

/* ── requests ─────────────────────────────────────────────────────────── */

public sealed record RegisterScreenRequest(string Name, string? Location, string? AddressLine);

public sealed record UpdateScreenRequest(string Name, string? Location, string? AddressLine);

public sealed record AssignPlaylistRequest(string? PlaylistId);

public sealed record PlaylistItemRequest(string Kind, string RefId, int DurationSeconds);

public sealed record UpsertPlaylistRequest(string Name, IReadOnlyList<PlaylistItemRequest>? Items);

/// <summary>What a device sends when an installer types the code.</summary>
public sealed record ClaimScreenRequest(string Code);

/* ── responses ────────────────────────────────────────────────────────── */

public sealed record DisplayCredentialDto(
    string Id,
    string Kind,
    DateTime CreatedAtUtc,
    DateTime? LastSeenAtUtc);

public sealed record DisplayScreenDto(
    string Id,
    string Name,
    string Location,
    string AddressLine,
    string? PlaylistId,
    string? PlaylistName,
    int ItemCount,
    bool IsPaired,
    string? PairingCode,
    DateTime? PairingCodeExpiresAtUtc,
    DateTime? LastSeenAtUtc,
    IReadOnlyList<DisplayCredentialDto> Credentials,
    DateTime CreatedAtUtc);

/// <param name="IsPlayable">
/// False when the poster has since been unpublished or deleted. Shown struck
/// through in the editor rather than silently dropped, so the pharmacy can see
/// why the loop got shorter.
/// </param>
public sealed record PlaylistItemDto(
    string Kind,
    string RefId,
    string Name,
    int DurationSeconds,
    bool IsPlayable);

public sealed record PlaylistDto(
    string Id,
    string Name,
    int Version,
    IReadOnlyList<PlaylistItemDto> Items,
    int TotalSeconds,
    IReadOnlyList<string> ScreenNames,
    DateTime UpdatedAtUtc);

/// <summary>The raw token, handed over exactly once.</summary>
public sealed record DisplayTokenDto(string Token, string ScreenId, string ScreenName);

/* ── what the kiosk receives ──────────────────────────────────────────── */

/// <param name="Kind">"Poster" or "Video".</param>
/// <param name="ImageUrl">
/// For a poster, the path the kiosk fetches with its token in a header. Empty
/// for a video. Kept for kiosks running code from before videos existed.
/// </param>
/// <param name="Name">What the item is called, for alt text.</param>
/// <param name="DurationSeconds">
/// A poster's dwell time. For a video, the longest it may hold the screen — the
/// kiosk moves on when the video ends; this is the safety net if it never does.
/// </param>
/// <param name="MediaUrl">
/// What to show: the poster path above, or a signed, expiring path for a video
/// (see <see cref="DisplayMedia.VideoPath"/>). Never a blob key.
/// </param>
public sealed record KioskSlideDto(
    string Kind,
    string ImageUrl,
    string Name,
    int DurationSeconds,
    string? MediaUrl = null);

/// <summary>
/// Everything a screen needs to draw itself, and nothing else.
/// </summary>
/// <remarks>
/// No owner id, no poster ids, no pharmacy account details. A kiosk is a device
/// in a public room that anyone can photograph, so the payload is scoped to
/// what a passer-by could already see by looking at the screen.
/// </remarks>
/// <param name="Revision">
/// Changes only when what is displayed changes. The ETag is built from it, so a
/// renamed playlist must not move it — otherwise every screen re-downloads
/// every image because somebody fixed a typo.
/// </param>
/// <param name="PollSeconds">Seconds the device should wait before asking again.</param>
public sealed record KioskContentDto(
    string ScreenName,
    string AddressLine,
    IReadOnlyList<KioskSlideDto> Slides,
    string Revision,
    int PollSeconds);
