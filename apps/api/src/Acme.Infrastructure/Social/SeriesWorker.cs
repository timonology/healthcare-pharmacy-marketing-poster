using Acme.Application.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Turns due recurring-series occurrences into scheduled posts, and emails
/// the pharmacy how finished ones went.
/// </summary>
/// <remarks>
/// <para>
/// It never publishes. Each occurrence it creates is an ordinary scheduled
/// post, due now, which <see cref="PublishWorker"/> claims and sends with the
/// usual lease. Safe to run on every instance: occurrence ids are stable, so
/// two instances creating the same one produce one document, and each
/// outcome email is claimed atomically before it is sent.
/// </para>
/// <para>
/// Switched with the publish worker (<c>Social:Worker:Enabled</c>):
/// queuing posts nobody will send would only fill the queue with posts that
/// are skipped as late when someone finally turns the worker on.
/// </para>
/// </remarks>
public sealed class SeriesWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PublishWorkerOptions> options,
    ILogger<SeriesWorker> log) : BackgroundService
{
    /// <summary>
    /// Often enough that a 09:00 post is queued within seconds of 09:00 — the
    /// publish worker then picks it up on its own next poll.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            log.LogInformation("Recurring series worker is off because the publish worker is disabled.");
            return;
        }

        log.LogInformation("Recurring series worker started, every {Tick}s", Tick.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Two independent steps: a fault queuing posts must not also stop
            // the pharmacy hearing how earlier ones went, and vice versa.
            if (!await StepAsync("queue due occurrences",
                    s => s.RunDueAsync(DateTime.UtcNow, stoppingToken), stoppingToken))
                break;
            if (!await StepAsync("report finished occurrences",
                    s => s.ReportOutcomesAsync(DateTime.UtcNow, stoppingToken), stoppingToken))
                break;

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        log.LogInformation("Recurring series worker stopping.");
    }

    /// <summary>One step in its own scope. False only when the host is stopping.</summary>
    private async Task<bool> StepAsync(
        string what, Func<RecurringSeriesService, Task<int>> step, CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await step(scope.ServiceProvider.GetRequiredService<RecurringSeriesService>());
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Never let the loop die: the next tick tries again.
            log.LogError(ex, "Recurring series worker could not {What}; continuing", what);
            return true;
        }
    }
}
