using System.Reflection;
using Acme.Domain.Displays;
using MongoDB.Bson.Serialization.Attributes;

namespace Acme.Infrastructure.Persistence;

/* ── documents ────────────────────────────────────────────────────────── */

internal sealed class DisplayCredentialDocument
{
    [BsonElement("id")] public string Id { get; set; } = default!;
    [BsonElement("kind")] public int Kind { get; set; }

    /// <summary>SHA-256 hex. The raw token is never written down.</summary>
    [BsonElement("tokenHash")] public string TokenHash { get; set; } = default!;

    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("lastSeenAtUtc")] public DateTime? LastSeenAtUtc { get; set; }
    [BsonElement("revokedAtUtc")] public DateTime? RevokedAtUtc { get; set; }
}

internal sealed class DisplayScreenDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("location")] public string Location { get; set; } = string.Empty;
    [BsonElement("addressLine")] public string AddressLine { get; set; } = string.Empty;
    [BsonElement("playlistId")] public string? PlaylistId { get; set; }
    [BsonElement("pairingCode")] public string? PairingCode { get; set; }
    [BsonElement("pairingCodeExpiresAtUtc")] public DateTime? PairingCodeExpiresAtUtc { get; set; }
    [BsonElement("credentials")] public List<DisplayCredentialDocument> Credentials { get; set; } = new();
    [BsonElement("lastSeenAtUtc")] public DateTime? LastSeenAtUtc { get; set; }
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class PlaylistItemDocument
{
    [BsonElement("kind")] public int Kind { get; set; }
    [BsonElement("refId")] public string RefId { get; set; } = default!;
    [BsonElement("durationSeconds")] public int DurationSeconds { get; set; }
}

internal sealed class PlaylistDocument
{
    [BsonId] public string Id { get; set; } = default!;
    [BsonElement("ownerId")] public string OwnerId { get; set; } = default!;
    [BsonElement("name")] public string Name { get; set; } = default!;
    [BsonElement("items")] public List<PlaylistItemDocument> Items { get; set; } = new();
    [BsonElement("version")] public int Version { get; set; } = 1;
    [BsonElement("createdAtUtc")] public DateTime CreatedAtUtc { get; set; }
    [BsonElement("updatedAtUtc")] public DateTime UpdatedAtUtc { get; set; }
}

/* ── mappers ──────────────────────────────────────────────────────────── */

/// <summary>
/// Document ↔ entity, by reflection into the private setters.
/// </summary>
/// <remarks>
/// The same approach as <see cref="BrandKitMapper"/>, for the same reason: the
/// entities guard their own invariants through methods, and rehydrating one
/// from storage is not a state change anybody should have to justify to those
/// guards. Going through the public API would mean re-running validation on
/// data that was already valid when it was written.
/// </remarks>
internal static class DisplayMapper
{
    private static readonly ConstructorInfo ScreenCtor = typeof(DisplayScreen)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("DisplayScreen parameterless ctor missing.");

    private static readonly ConstructorInfo PlaylistCtor = typeof(Playlist)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
        ?? throw new InvalidOperationException("Playlist parameterless ctor missing.");

    public static DisplayScreen Hydrate(DisplayScreenDocument d)
    {
        var screen = (DisplayScreen)ScreenCtor.Invoke(null);

        Set(screen, nameof(DisplayScreen.Id), d.Id);
        Set(screen, nameof(DisplayScreen.OwnerId), d.OwnerId);
        Set(screen, nameof(DisplayScreen.Name), d.Name);
        Set(screen, nameof(DisplayScreen.Location), d.Location ?? string.Empty);
        Set(screen, nameof(DisplayScreen.AddressLine), d.AddressLine ?? string.Empty);
        Set(screen, nameof(DisplayScreen.PlaylistId), d.PlaylistId);
        Set(screen, nameof(DisplayScreen.PairingCode), d.PairingCode);
        Set(screen, nameof(DisplayScreen.PairingCodeExpiresAtUtc), d.PairingCodeExpiresAtUtc);
        Set(screen, nameof(DisplayScreen.LastSeenAtUtc), d.LastSeenAtUtc);
        Set(screen, nameof(DisplayScreen.CreatedAtUtc), d.CreatedAtUtc);
        Set(screen, nameof(DisplayScreen.UpdatedAtUtc), d.UpdatedAtUtc);

        // The backing list, not the read-only projection over it.
        var credentials = Backing<List<DisplayCredential>>(screen, "_credentials");
        credentials.Clear();
        credentials.AddRange((d.Credentials ?? new()).Select(c => new DisplayCredential(
            c.Id,
            (DisplayCredentialKind)c.Kind,
            c.TokenHash,
            c.CreatedAtUtc,
            c.LastSeenAtUtc,
            c.RevokedAtUtc)));

        return screen;
    }

    public static DisplayScreenDocument ToDocument(DisplayScreen s) => new()
    {
        Id = s.Id,
        OwnerId = s.OwnerId,
        Name = s.Name,
        Location = s.Location,
        AddressLine = s.AddressLine,
        PlaylistId = s.PlaylistId,
        PairingCode = s.PairingCode,
        PairingCodeExpiresAtUtc = s.PairingCodeExpiresAtUtc,
        LastSeenAtUtc = s.LastSeenAtUtc,
        CreatedAtUtc = s.CreatedAtUtc,
        UpdatedAtUtc = s.UpdatedAtUtc,
        Credentials = s.Credentials.Select(c => new DisplayCredentialDocument
        {
            Id = c.Id,
            Kind = (int)c.Kind,
            TokenHash = c.TokenHash,
            CreatedAtUtc = c.CreatedAtUtc,
            LastSeenAtUtc = c.LastSeenAtUtc,
            RevokedAtUtc = c.RevokedAtUtc,
        }).ToList(),
    };

    public static Playlist Hydrate(PlaylistDocument d)
    {
        var playlist = (Playlist)PlaylistCtor.Invoke(null);

        Set(playlist, nameof(Playlist.Id), d.Id);
        Set(playlist, nameof(Playlist.OwnerId), d.OwnerId);
        Set(playlist, nameof(Playlist.Name), d.Name);
        Set(playlist, nameof(Playlist.Version), d.Version);
        Set(playlist, nameof(Playlist.CreatedAtUtc), d.CreatedAtUtc);
        Set(playlist, nameof(Playlist.UpdatedAtUtc), d.UpdatedAtUtc);

        var items = Backing<List<PlaylistItem>>(playlist, "_items");
        items.Clear();
        items.AddRange((d.Items ?? new()).Select(i => new PlaylistItem(
            (PlaylistItemKind)i.Kind, i.RefId, i.DurationSeconds)));

        return playlist;
    }

    public static PlaylistDocument ToDocument(Playlist p) => new()
    {
        Id = p.Id,
        OwnerId = p.OwnerId,
        Name = p.Name,
        Version = p.Version,
        CreatedAtUtc = p.CreatedAtUtc,
        UpdatedAtUtc = p.UpdatedAtUtc,
        Items = p.Items.Select(i => new PlaylistItemDocument
        {
            Kind = (int)i.Kind,
            RefId = i.RefId,
            DurationSeconds = i.DurationSeconds,
        }).ToList(),
    };

    private static void Set(object target, string property, object? value)
    {
        var prop = target.GetType().GetProperty(
            property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{property} not found.");

        prop.SetValue(target, value);
    }

    private static T Backing<T>(object target, string field)
    {
        var info = target.GetType().GetField(
            field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{field} not found.");

        return (T)info.GetValue(target)!;
    }
}
