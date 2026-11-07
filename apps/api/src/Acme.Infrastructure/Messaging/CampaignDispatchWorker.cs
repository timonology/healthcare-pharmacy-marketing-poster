using Acme.Application.Campaigns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Acme.Infrastructure.Messaging;

/// <summary>
/// Sends queued campaigns, one at a time, off the request thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Sending happened inside <c>POST /api/campaigns</c>,
/// which held the HTTP connection until the last recipient had been handed to
/// the provider. At four recipients that is a few seconds. At a thousand it is
/// minutes of a blocked request thread and a spinner with no progress, and
/// Azure App Service terminates an idle request at 230 seconds however patient
/// the browser is.
/// </para>
/// <para>
/// It also fixes two things that were not about speed. A campaign interrupted
/// mid-send used to be stranded in <c>Sending</c> — nothing read that status,
/// and Retry refuses anything that is not <c>Failed</c>, so it could neither
/// finish nor be retried. And Retry re-sent to the whole original list, so a
/// campaign that reached 700 of 1000 emailed those 700 again.
/// </para>
/// <para>
/// <b>The pattern is the video pipeline's.</b> A row in Mongo is the queue, a
/// worker claims it atomically, progress is written as it goes and the UI polls.
/// Choosing it over a real queue service was deliberate: no new infrastructure,
/// no new credential, and an operational story the team already runs. Service
/// Bus remains the migration path if volume ever justifies it.
/// </para>
/// </remarks>
public sealed class CampaignDispatchWorker(
    IServiceScopeFactory scopes,
    ILogger<CampaignDispatchWorker> log) : BackgroundService
{
    /// <summary>How often to look for work when there was none last time.</summary>
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Identifies this process in the lease.
    /// </summary>
    /// <remarks>
    /// Machine name and process id, so a stuck campaign in production can be
    /// traced to the instance that was holding it. A GUID would be unique and
    /// tell nobody anything.
    /// </remarks>
    private readonly string _worker =
        $"{Environment.MachineName}/{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        log.LogInformation("Campaign dispatch worker started as {Worker}", _worker);

        while (!stopping.IsCancellationRequested)
        {
            var claimedSomething = false;

            try
            {
                claimedSomething = await RunOnceAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let one bad campaign stop the worker. The loop is the
                // only thing sending email; if it exits, sending stops silently
                // and the symptom is campaigns sitting in Queued forever.
                log.LogError(ex, "Campaign dispatch loop failed; continuing");
            }

            if (!claimedSomething)
            {
                try { await Task.Delay(Idle, stopping); }
                catch (OperationCanceledException) { break; }
            }
        }

        log.LogInformation("Campaign dispatch worker stopping");
    }

    /// <summary>Claims and sends at most one campaign. True if it found one.</summary>
    private async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        // A scope per campaign. The services below are scoped, and holding one
        // for the lifetime of the process would keep a Mongo session and every
        // resolved dependency alive for days.
        using var scope = scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();
        var service = scope.ServiceProvider.GetRequiredService<CampaignService>();

        var lease = DateTime.UtcNow.Add(CampaignService.LeaseDuration);
        var campaign = await repo.ClaimNextForDispatchAsync(_worker, lease, ct);
        if (campaign is null) return false;

        log.LogInformation(
            "Dispatching campaign {CampaignId} ({Pending} of {Total} recipients outstanding)",
            campaign.Id, campaign.PendingRecipients.Count, campaign.Recipients.Count);

        try
        {
            await service.DispatchClaimedAsync(campaign, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down mid-campaign. Deliberately left as it is rather than
            // marked failed: the lease lapses, another instance claims it, and
            // the recipients already recorded as Sent are not sent again. Marking
            // it Failed here would be a lie about recipients that did go out.
            log.LogInformation(
                "Campaign {CampaignId} interrupted by shutdown; it will resume when the lease lapses",
                campaign.Id);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Campaign {CampaignId} failed during dispatch", campaign.Id);
            try
            {
                campaign.MarkFailed("The send stopped unexpectedly. See server logs.");
                await repo.UpdateAsync(campaign, ct);
            }
            catch (Exception saveEx)
            {
                // If even this fails the lease will lapse and the campaign will
                // be retried, which is the right outcome — but say so, because
                // otherwise it looks like a campaign that quietly retried itself.
                log.LogError(saveEx,
                    "Could not record the failure of campaign {CampaignId}; it will be retried when its lease lapses",
                    campaign.Id);
            }
        }

        return true;
    }
}
