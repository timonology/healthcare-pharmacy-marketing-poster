using Acme.Domain.Social;

namespace Acme.Application.Social;

/// <summary>
/// Guards for platforms whose credential rotates on use (TikTok, X).
/// </summary>
/// <remarks>
/// <para>
/// Two things can touch one connection at once: a publish and the daily
/// health check, or two publishes on two instances. With a rotating refresh
/// token, the first to refresh gets the new credential and retires the old
/// one — so the second, still holding the old one, is refused. Without these
/// guards that refusal marks a perfectly healthy account for reconnecting,
/// and a later full-document save writes the dead credential back over the
/// live one.
/// </para>
/// </remarks>
internal static class ConnectionWrites
{
    /// <summary>
    /// True when the stored credential is no longer the one the caller used:
    /// someone else rotated it meanwhile, so a refusal of the old one says
    /// nothing about the account.
    /// </summary>
    public static async Task<bool> RotatedElsewhereAsync(
        ISocialConnectionRepository connections, string id, EncryptedSecret? used)
    {
        if (used is null) return false;
        var stored = await connections.FindByIdAsync(id, CancellationToken.None);
        return stored?.AccessToken is { } latest && latest != used;
    }

    /// <summary>
    /// A full-document save of a status change that first takes over any
    /// credential saved elsewhere since this copy was read, and does nothing
    /// for a connection disconnected meanwhile (a save must not bring it back).
    /// </summary>
    public static async Task SaveStatusAsync(ISocialConnectionRepository connections, SocialConnection connection)
    {
        var stored = await connections.FindByIdAsync(connection.Id, CancellationToken.None);
        if (stored is null || stored.Status == ConnectionStatus.Revoked) return;

        if (!ReferenceEquals(stored, connection)
            && stored.AccessToken is { } latest && latest != connection.AccessToken)
            connection.RenewCredential(latest, stored.ExpiresAtUtc);

        await connections.UpdateAsync(connection, CancellationToken.None);
    }
}
