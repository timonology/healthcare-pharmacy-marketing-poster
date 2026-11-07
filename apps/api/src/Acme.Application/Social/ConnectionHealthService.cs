using Acme.Domain.Social;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>
/// Checks once a day that each connected account still works.
/// </summary>
/// <remarks>
/// <para>
/// Without it, a pharmacy learns that Meta revoked a Page token — a password
/// change, a removed admin, an expired session — when the flu-clinic post they
/// scheduled last week fails at 8am. With it, the connection shows "Reconnect"
/// the day it breaks, and the publish dialog warns before anything is queued.
/// </para>
/// <para>
/// A check sends nothing to anyone's feed: it reads the account's own id with
/// the stored token. Only a definite "token not accepted" marks a connection
/// for reconnecting; an outage or rate limit changes nothing and the
/// connection is simply looked at again tomorrow.
/// </para>
/// <para>
/// Claims are atomic (<see cref="ISocialConnectionRepository.ClaimForHealthCheckAsync"/>)
/// so several API instances share the work instead of repeating it, and
/// results are written with a targeted update that cannot revive a connection
/// revoked in the meantime.
/// </para>
/// </remarks>
public sealed class ConnectionHealthService(
    ISocialConnectionRepository connections,
    ITokenCipher cipher,
    IEnumerable<ISocialConnectionProbe> probes,
    ILogger<ConnectionHealthService> log)
{
    /// <summary>How often each connection is checked.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Checks up to <paramref name="max"/> due connections.</summary>
    /// <returns>How many were checked.</returns>
    public async Task<int> CheckDueAsync(int max, CancellationToken ct)
    {
        if (!cipher.IsConfigured) return 0;

        var usable = probes.Where(p => p.IsConfigured).ToList();
        if (usable.Count == 0) return 0;

        var platforms = usable.Select(p => p.Platform).ToArray();
        var checkedCount = 0;

        while (checkedCount < max && !ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var connection = await connections.ClaimForHealthCheckAsync(platforms, now - Interval, now, ct);
            if (connection is null) break;

            checkedCount++;
            await CheckAsync(connection, usable.First(p => p.Platform == connection.Platform), ct);
        }

        if (checkedCount > 0)
            log.LogInformation("Checked {Count} social connection(s)", checkedCount);

        return checkedCount;
    }

    private async Task CheckAsync(SocialConnection connection, ISocialConnectionProbe probe, CancellationToken ct)
    {
        if (connection.AccessToken is null) return;
        var used = connection.AccessToken;

        string token;
        try
        {
            token = cipher.Decrypt(connection.AccessToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A key problem affects every connection under it, and is the
            // publish path's to report loudly; recorded here so the pharmacy
            // is told to reconnect rather than left to find out at publish.
            log.LogError(ex, "Could not decrypt credentials for connection {Id} during health check", connection.Id);
            await connections.RecordHealthAsync(connection.Id, healthy: false,
                "Stored credentials could not be read. Reconnect the account.", DateTime.UtcNow, ct);
            return;
        }

        (ProbeResult Result, string? Detail) outcome;
        try
        {
            if (probe is IRenewingConnectionProbe renewing)
            {
                var (result, detail, renewed) = await renewing.ProbeAndRenewAsync(connection.ExternalAccountId, token, ct);
                outcome = (result, detail);
                if (renewed is not null)
                    await connections.RenewCredentialAsync(
                        connection.Id, cipher.Encrypt(renewed.Credential), renewed.ExpiresAtUtc, ct);

                // A publish rotated the credential while this check held the
                // old one (TikTok, X): its refusal says nothing about the
                // account. Look again tomorrow.
                else if (result == ProbeResult.Unauthorized
                         && await ConnectionWrites.RotatedElsewhereAsync(connections, connection.Id, used))
                    outcome = (ProbeResult.Inconclusive, detail);
            }
            else
            {
                outcome = await probe.ProbeAsync(connection.ExternalAccountId, token, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Health check for connection {Id} failed to run", connection.Id);
            return;
        }

        switch (outcome.Result)
        {
            case ProbeResult.Healthy:
                await connections.RecordHealthAsync(connection.Id, healthy: true, null, DateTime.UtcNow, ct);
                break;

            case ProbeResult.Unauthorized:
                log.LogInformation(
                    "{Platform} connection {Id} no longer accepted; marked for reconnecting",
                    connection.Platform, connection.Id);
                await connections.RecordHealthAsync(connection.Id, healthy: false,
                    outcome.Detail ?? "The connection to this account is no longer valid. Reconnect it.",
                    DateTime.UtcNow, ct);
                break;

            default:
                // Inconclusive: leave it. It was stamped as checked by the
                // claim, so it is looked at again tomorrow, not in a tight loop.
                break;
        }
    }
}
