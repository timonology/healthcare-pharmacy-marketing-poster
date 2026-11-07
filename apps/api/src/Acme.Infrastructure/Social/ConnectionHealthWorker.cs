using Acme.Application.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Runs <see cref="ConnectionHealthService"/> once an hour. Each connection is
/// checked at most once a day; the hourly pass spreads that through the day
/// instead of sending every pharmacy's check to Meta at midnight.
/// </summary>
public sealed class ConnectionHealthWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PublishWorkerOptions> options,
    ILogger<ConnectionHealthWorker> log) : BackgroundService
{
    private static readonly TimeSpan Pass = TimeSpan.FromHours(1);

    /// <summary>
    /// Per pass. A day of passes covers 24× this, which is far beyond any
    /// realistic number of connections; the cap is there so one pass cannot
    /// run for an hour and overlap the next.
    /// </summary>
    private const int MaxPerPass = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled || !o.HealthChecks)
        {
            log.LogInformation("Social connection health checks are disabled by configuration.");
            return;
        }

        // Not at the same instant as every other startup task.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ConnectionHealthService>()
                    .CheckDueAsync(MaxPerPass, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Must not die: a database blip would otherwise end health
                // checks until the next deploy.
                log.LogError(ex, "Social connection health check pass failed; continuing");
            }

            try { await Task.Delay(Pass, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
