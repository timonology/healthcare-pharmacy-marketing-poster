using Acme.Application.Social;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acme.Infrastructure.Social;

/// <summary>
/// Executes due publishes, one claimed post at a time.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately dull. It polls, claims, delegates to
/// <see cref="PublishService.ExecuteAsync"/>, and repeats. All of the
/// interesting behaviour — what to publish where, how to classify a failure,
/// what a retry may touch — lives in the service, where it can be exercised
/// without a host running.
/// </para>
/// <para>
/// <b>Safety rests entirely on the claim being atomic.</b> Several instances
/// running this loop is the expected deployment, not an edge case, and the only
/// thing standing between that and a pharmacy's followers seeing the same
/// poster twice is <c>ClaimNextDueAsync</c> being a single findAndModify. This
/// class must never "optimise" that into a read followed by a write.
/// </para>
/// </remarks>
public sealed class PublishWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<PublishWorkerOptions> options,
    ILogger<PublishWorker> log) : BackgroundService
{
    private readonly PublishWorkerOptions _options = options.Value;

    /// <summary>
    /// Identifies this instance in the lease.
    /// </summary>
    /// <remarks>
    /// Machine name plus a per-process id: two containers on one host, or two
    /// processes in one container, must not share an identity or each could
    /// take over the other's in-flight post.
    /// </remarks>
    private readonly string _leaseHolder =
        $"{Environment.MachineName}:{Guid.NewGuid().ToString("N")[..8]}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            log.LogInformation("Publish worker is disabled by configuration.");
            return;
        }

        // Checked once, here, rather than discovered on the first claimed post.
        //
        // The cipher validates its keys in its constructor, so an unconfigured
        // server would otherwise throw inside the loop — every fifteen seconds,
        // for ever, burying the one message that says what to do. Failing once
        // and standing down is quieter and more useful.
        try
        {
            using var probe = scopeFactory.CreateScope();
            if (!probe.ServiceProvider.GetRequiredService<ITokenCipher>().IsConfigured)
                throw new InvalidOperationException(TokenCipher.NotConfiguredMessage);
        }
        catch (Exception ex)
        {
            // One placeholder, one argument. Structured logging binds
            // positionally, so repeating {Section} would need it passed twice —
            // and CA2017 rightly treats the mismatch as an error.
            log.LogError(
                ex,
                "Publish worker cannot start: credential encryption is not configured. " +
                "Set {Section} with a Keys entry (base64 32 bytes from `openssl rand -base64 32`) " +
                "and a matching ActiveVersion. Scheduled posts will not publish until then.",
                TokenCipherOptions.SectionName);
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Clamp(_options.PollSeconds, 5, 300));
        var lease = TimeSpan.FromSeconds(Math.Clamp(_options.LeaseSeconds, 60, 1800));

        log.LogInformation(
            "Publish worker started as {LeaseHolder}, polling every {Poll}s with a {Lease}s lease",
            _leaseHolder, poll.TotalSeconds, lease.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = false;

            try
            {
                didWork = await DrainAsync(lease, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The loop must not die. A failure here is usually the database
                // being briefly unreachable, and a worker that exits on that
                // silently stops publishing for everyone until someone notices
                // a pharmacy complaining.
                log.LogError(ex, "Publish worker iteration failed; continuing");
            }

            // Straight round again if there was work — a backlog should drain
            // at the speed of the platforms, not the poll interval.
            if (!didWork)
            {
                try { await Task.Delay(poll, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        log.LogInformation("Publish worker stopping.");
    }

    /// <summary>Claims and runs due posts until there are none left.</summary>
    /// <returns>True if at least one post was processed.</returns>
    private async Task<bool> DrainAsync(TimeSpan lease, CancellationToken ct)
    {
        // A ceiling per iteration so one pharmacy queueing a hundred posts
        // cannot monopolise the loop and starve everyone else's schedule.
        const int MaxPerIteration = 20;

        var processed = 0;

        while (processed < MaxPerIteration && !ct.IsCancellationRequested)
        {
            // A fresh scope per post. The repositories and the Mongo context are
            // scoped, and reusing one scope across a long-running loop would
            // hold their lifetimes open for as long as the worker runs.
            using var scope = scopeFactory.CreateScope();

            var posts = scope.ServiceProvider.GetRequiredService<IScheduledPostRepository>();
            var publish = scope.ServiceProvider.GetRequiredService<PublishService>();

            var post = await posts.ClaimNextDueAsync(_leaseHolder, lease, DateTime.UtcNow, ct);
            if (post is null) break;

            processed++;

            log.LogInformation(
                "Claimed post {Post} for pharmacy {Owner} with {Targets} target(s)",
                post.Id, post.OwnerId, post.Targets.Count);

            try
            {
                await publish.ExecuteAsync(post, lease, ct);
            }
            catch (Exception ex)
            {
                // ExecuteAsync handles per-target failures, so reaching here is
                // a bug or an outage (the database, most likely). Hand the post
                // back for a later attempt rather than leave it to the lease:
                // anything that was mid-call is marked uncertain first, so the
                // retry cannot resend it. Delayed, not immediate, so a fault that
                // repeats does not spin.
                log.LogError(ex, "Publishing post {Post} threw; re-queuing it", post.Id);

                try
                {
                    post.ResolveInterruptedAttempts();
                    // Shutting down is not a fault: straight back to the queue
                    // for the next instance.
                    if (ct.IsCancellationRequested) post.Requeue();
                    else post.ScheduleAutomaticRetry(DateTime.UtcNow.AddMinutes(5));
                    // Conditional, like every other worker write: if the lease
                    // has already moved on, the new owner's state wins.
                    await posts.TryUpdateClaimedAsync(post, _leaseHolder, CancellationToken.None);
                }
                catch (Exception releaseEx)
                {
                    // Left for the lease to lapse; the claim reclaims it then.
                    log.LogError(releaseEx, "Could not re-queue post {Post}", post.Id);
                }
            }
        }

        return processed > 0;
    }
}
