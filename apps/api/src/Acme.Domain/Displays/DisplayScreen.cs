using Acme.Domain.Common;

namespace Acme.Domain.Displays;

/// <summary>How a screen came to be trusted.</summary>
public enum DisplayCredentialKind
{
    /// <summary>An installer typed the pairing code at the device.</summary>
    PairedDevice = 1,

    /// <summary>Someone copied a direct link out of the dashboard.</summary>
    DirectLink = 2,
}

/// <summary>
/// One way of reaching a screen's content, and a thing that can be taken away.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the hash is kept.</b> The raw token is returned once, at the moment
/// it is created, and never again — the same rule the invitation flow follows,
/// and for the same reason: a database dump should not let anyone walk up to a
/// pharmacy's screens.
/// </para>
/// <para>
/// Credentials are a list rather than a single field because a screen can be
/// reached two ways at once. Collapsing them would mean copying a direct link
/// silently unpaired the device already hanging on the wall.
/// </para>
/// </remarks>
public sealed record DisplayCredential(
    string Id,
    DisplayCredentialKind Kind,
    string TokenHash,
    DateTime CreatedAtUtc,
    DateTime? LastSeenAtUtc,
    DateTime? RevokedAtUtc)
{
    public bool IsActive => RevokedAtUtc is null;
}

/// <summary>
/// A screen hanging in a pharmacy, and the playlist it loops.
/// </summary>
/// <remarks>
/// <para>
/// Registered by the pharmacy first, then claimed by the device. That order
/// matters: the pharmacy decides a screen exists and what it is called, and the
/// installer's whole job is to type six characters. Nothing secret is spoken
/// aloud, written on a card, or left in a browser's history.
/// </para>
/// <para>
/// The screen holds no patient data and never will. It shows published posters
/// and, if configured, the branch address — which is on the shop front anyway.
/// </para>
/// </remarks>
public sealed class DisplayScreen : Entity
{
    /// <summary>
    /// Pairing alphabet, minus the characters people get wrong.
    /// </summary>
    /// <remarks>
    /// No O/0, I/1/L, U/V, S/5, Z/2, B/8. An installer is reading this off a
    /// laptop and typing it into a television with a remote control, and every
    /// ambiguous glyph is a support call.
    /// </remarks>
    private const string PairingAlphabet = "ACDEFGHJKMNPQRTWXY34679";

    public const int PairingCodeLength = 6;

    /// <summary>How long a pairing code is worth typing.</summary>
    /// <remarks>
    /// Long enough to walk across the shop and switch a television on; short
    /// enough that a code left on a printed rota is useless by the afternoon.
    /// </remarks>
    public static readonly TimeSpan PairingWindow = TimeSpan.FromHours(4);

    public string OwnerId { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    /// <summary>Where in the shop it hangs. For the pharmacy, not the screen.</summary>
    public string Location { get; private set; } = string.Empty;

    /// <summary>
    /// Address drawn on the screen itself, or empty for none.
    /// </summary>
    /// <remarks>
    /// Deliberately a free-text line on the screen rather than a lookup of the
    /// pharmacy profile. A chain's branch address is not always the account's
    /// registered address, and a screen in a supermarket concession may want
    /// the aisle rather than the postcode.
    /// </remarks>
    public string AddressLine { get; private set; } = string.Empty;

    public string? PlaylistId { get; private set; }

    public string? PairingCode { get; private set; }
    public DateTime? PairingCodeExpiresAtUtc { get; private set; }

    private readonly List<DisplayCredential> _credentials = new();
    public IReadOnlyList<DisplayCredential> Credentials => _credentials;

    public DateTime? LastSeenAtUtc { get; private set; }

    public bool IsPaired => _credentials.Any(c => c.IsActive);

    public bool PairingCodeIsLive =>
        PairingCode is not null
        && PairingCodeExpiresAtUtc is { } expiry
        && expiry > DateTime.UtcNow;

    private DisplayScreen() { }

    public static DisplayScreen Register(string ownerId, string name, string? location, string? addressLine)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");

        var screen = new DisplayScreen
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            Name = CleanName(name),
            Location = Clean(location, 200, "Location"),
            AddressLine = Clean(addressLine, 300, "Address"),
        };

        screen.IssuePairingCode();
        return screen;
    }

    public void Update(string name, string? location, string? addressLine)
    {
        Name = CleanName(name);
        Location = Clean(location, 200, "Location");
        AddressLine = Clean(addressLine, 300, "Address");
        Touch();
    }

    public void AssignPlaylist(string? playlistId)
    {
        PlaylistId = string.IsNullOrWhiteSpace(playlistId) ? null : playlistId;
        Touch();
    }

    /// <summary>
    /// Issues a fresh pairing code, replacing any that was outstanding.
    /// </summary>
    /// <remarks>
    /// The code is stored in the clear, unlike the token it leads to. It is six
    /// characters, lives four hours, works once, and is shown to the person who
    /// already owns the screen — hashing it would buy nothing and would stop the
    /// dashboard displaying it.
    /// </remarks>
    public string IssuePairingCode()
    {
        PairingCode = RandomCode();
        PairingCodeExpiresAtUtc = DateTime.UtcNow.Add(PairingWindow);
        Touch();
        return PairingCode;
    }

    public void ClearPairingCode()
    {
        PairingCode = null;
        PairingCodeExpiresAtUtc = null;
        Touch();
    }

    /// <summary>
    /// Records a new credential. The caller keeps the raw token; this keeps its hash.
    /// </summary>
    public DisplayCredential AddCredential(DisplayCredentialKind kind, string tokenHash)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
            throw new DomainException("A credential needs a token hash.");

        // One live credential of each kind. Pairing a replacement device should
        // retire the old one rather than leaving a token that still works on
        // hardware the pharmacy has thrown away.
        foreach (var existing in _credentials.Where(c => c.Kind == kind && c.IsActive).ToList())
            Revoke(existing.Id);

        var credential = new DisplayCredential(
            Guid.NewGuid().ToString("N"), kind, tokenHash, DateTime.UtcNow, null, null);

        _credentials.Add(credential);
        Touch();
        return credential;
    }

    public bool Revoke(string credentialId)
    {
        var index = _credentials.FindIndex(c => c.Id == credentialId && c.IsActive);
        if (index < 0) return false;

        _credentials[index] = _credentials[index] with { RevokedAtUtc = DateTime.UtcNow };
        Touch();
        return true;
    }

    /// <summary>Finds a live credential by the hash of a presented token.</summary>
    public DisplayCredential? MatchCredential(string tokenHash) =>
        _credentials.FirstOrDefault(c => c.IsActive && c.TokenHash == tokenHash);

    /// <summary>
    /// Notes that a screen called home.
    /// </summary>
    /// <remarks>
    /// <b>Does not Touch().</b> A kiosk polls every half-minute for years; if
    /// last-seen moved UpdatedAtUtc, every screen would rewrite its own document
    /// on every poll and the field would be useless for spotting real edits.
    /// The write is worth it anyway: "this screen has not been seen since
    /// Tuesday" is the one thing a pharmacy needs to know about a dark display.
    /// </remarks>
    public void NoteSeen(string credentialId)
    {
        LastSeenAtUtc = DateTime.UtcNow;

        var index = _credentials.FindIndex(c => c.Id == credentialId);
        if (index >= 0)
            _credentials[index] = _credentials[index] with { LastSeenAtUtc = LastSeenAtUtc };
    }

    private static string RandomCode()
    {
        var chars = new char[PairingCodeLength];
        for (var i = 0; i < chars.Length; i++)
        {
            // Rejection-free because the alphabet length divides evenly enough
            // for a code nobody is attacking offline; the token behind it is
            // what carries the entropy.
            chars[i] = PairingAlphabet[
                System.Security.Cryptography.RandomNumberGenerator.GetInt32(PairingAlphabet.Length)];
        }
        return new string(chars);
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("A screen needs a name, so it can be told from the others.");

        var cleaned = name.Trim();
        if (cleaned.Length > 120)
            throw new DomainException("Screen name must be 120 characters or fewer.");

        return cleaned;
    }

    private static string Clean(string? value, int max, string label)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length > max)
            throw new DomainException($"{label} must be {max} characters or fewer.");

        return cleaned;
    }
}
