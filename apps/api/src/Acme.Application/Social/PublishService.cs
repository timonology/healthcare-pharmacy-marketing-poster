using Acme.Application.Auth;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Domain.Social;
using Acme.Domain.Common;
using Acme.Domain.Subscriptions;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Social;

/// <summary>
/// Composing, scheduling and executing publishes.
/// </summary>
/// <remarks>
/// <para>
/// Two audiences. The pharmacy-facing methods create and manage requests; the
/// single worker-facing method, <see cref="ExecuteAsync"/>, performs one. They
/// are in the same class because the rules about what may be published to
/// where must not diverge between the two.
/// </para>
/// <para>
/// Nothing here decides <i>when</i> to run. The worker owns the schedule and
/// the lease; this service is given a post that has already been claimed and
/// gets on with it. Keeping the claim out of here means the logic below can be
/// exercised directly in a test without a background service running.
/// </para>
/// </remarks>
public sealed class PublishService(
    IScheduledPostRepository posts,
    ISocialConnectionRepository connections,
    IUserRepository users,
    ITokenCipher cipher,
    IPublishMediaResolver media,
    // Needed only to read a poster's status. The media resolver already loads
    // the poster, but it answers null for every failure, so it cannot tell
    // "still a draft" apart from "deleted" — and being told a poster you are
    // looking at no longer exists is worse than no message at all.
    IPosterRepository posters,
    IEnumerable<ISocialPublisher> publishers,
    ILogger<PublishService> log)
{
    /// <summary>
    /// Automatic attempts per target before it stops retrying on its own.
    /// </summary>
    /// <remarks>
    /// Three, not ten. Every attempt that fails for a non-transient reason —
    /// a caption the platform dislikes, an image the wrong shape — burns rate
    /// limit and delays the posts behind it. A person can always retry by hand.
    /// </remarks>
    public const int MaxAutomaticAttempts = 3;

    /// <summary>
    /// The publisher's reason plus "Trying again automatically." — once. Some
    /// publishers already say they will retry; saying it twice reads as a bug.
    /// </summary>
    public static string RetryNote(string? error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "Temporary problem." : error.Trim();
        return reason.Contains("again", StringComparison.OrdinalIgnoreCase)
               || reason.Contains("retried", StringComparison.OrdinalIgnoreCase)
            ? reason
            : $"{reason} Trying again automatically.";
    }

    /// <summary>The worker claimed the post between the pharmacy loading and saving it.</summary>
    private const string MovedOn =
        "This post has just started publishing. Refresh to see where it got to.";

    /* ---------------------------------------------------------------- *
     * Pharmacy-facing
     * ---------------------------------------------------------------- */

    public async Task<Result<ScheduledPostDto>> CreateAsync(
        string ownerId, CreatePublishRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<ScheduledPostDto>.NotFound("Account not found.");
        if (user.Tier == SubscriptionTier.Free)
            return Result<ScheduledPostDto>.Forbidden(
                "Social publishing is available on the Starter plan and above.");

        if (request.ConnectionIds is null || request.ConnectionIds.Count == 0)
            return Result<ScheduledPostDto>.Invalid("Choose at least one account to publish to.");

        // Publishing puts the poster on the pharmacy's public feed, which is
        // the same commitment as sending it to every patient — so it takes the
        // same deliberate Publish. Videos have their own readiness rule below
        // and are unaffected.
        if (request.SourceKind == PublishSourceKind.Poster)
        {
            var poster = await posters.FindByIdAsync(request.SourceId, ct);
            if (poster is null || poster.OwnerId != ownerId)
                return Result<ScheduledPostDto>.NotFound("That poster no longer exists.");

            if (PosterUsage.WhyCannotSend(poster, "posted to social") is { } cannotPost)
                return Result<ScheduledPostDto>.Invalid(cannotPost);
        }

        var resolved = await media.ResolveAsync(
            request.SourceKind, request.SourceId, ownerId, forInstagram: false, ct);
        if (resolved is null)
            return Result<ScheduledPostDto>.NotFound(
                request.SourceKind == PublishSourceKind.Poster
                    ? "That poster no longer exists."
                    : "That video no longer exists, or it has not finished rendering.");

        var owned = await connections.ListByOwnerAsync(ownerId, ct);
        var chosen = new List<(string ConnectionId, SocialPlatform Platform)>();

        foreach (var connectionId in request.ConnectionIds.Distinct())
        {
            var connection = owned.FirstOrDefault(c => c.Id == connectionId);

            // Not found and not-yours are the same answer on purpose. A
            // distinguishable "that exists but isn't yours" lets someone probe
            // for other pharmacies' connection ids.
            if (connection is null || connection.Status == ConnectionStatus.Revoked)
                return Result<ScheduledPostDto>.NotFound("One of the chosen accounts is no longer connected.");

            if (!connection.Platform.IsPublishTarget())
                return Result<ScheduledPostDto>.Invalid(
                    $"{connection.Platform.DisplayName()} is not somewhere posts can be published.");

            if (connection.Status == ConnectionStatus.NeedsReauth)
                return Result<ScheduledPostDto>.Invalid(
                    $"{connection.DisplayName} needs to be reconnected before it can be published to.");

            // Checked now rather than at publish time. Finding out at 8am on a
            // Saturday that Google Business Profile cannot take a video is
            // worse than being told while composing.
            if (resolved.IsVideo && !connection.Platform.SupportsVideo())
                return Result<ScheduledPostDto>.Invalid(
                    $"{connection.Platform.DisplayName()} cannot publish videos. Remove it, or publish a poster instead.");

            if (!resolved.IsVideo && !connection.Platform.SupportsImages())
                return Result<ScheduledPostDto>.Invalid(
                    $"{connection.Platform.DisplayName()} only takes videos. Remove it, or publish a video instead.");

            chosen.Add((connection.Id, connection.Platform));
        }

        var needsGbpOptions = chosen.Any(c => c.Platform == SocialPlatform.GoogleBusinessProfile);
        var gbpOptions = request.GbpOptions
            ?? (needsGbpOptions ? new GbpPostOptions(GbpPostType.Update) : null);

        if (needsGbpOptions && gbpOptions is not null)
        {
            if (gbpOptions.PostType == GbpPostType.Offer && string.IsNullOrWhiteSpace(gbpOptions.CouponCode)
                && string.IsNullOrWhiteSpace(gbpOptions.RedeemOnlineUrl))
                return Result<ScheduledPostDto>.Invalid(
                    "A Google Business Profile offer needs either a coupon code or a redeem link.");

            if (gbpOptions.PostType == GbpPostType.Event
                && (gbpOptions.EventStartUtc is null || string.IsNullOrWhiteSpace(gbpOptions.EventTitle)))
                return Result<ScheduledPostDto>.Invalid(
                    "A Google Business Profile event needs a title and a start time.");
        }

        ScheduledPost post;
        try
        {
            post = ScheduledPost.Create(
                ownerId,
                request.SourceKind,
                request.SourceId,
                resolved.Name,
                request.Caption ?? string.Empty,
                request.ScheduledForUtc,
                chosen,
                gbpOptions,
                request.TikTokOptions);
        }
        catch (DomainException ex)
        {
            return Result<ScheduledPostDto>.Invalid(ex.Message);
        }

        await posts.AddAsync(post, ct);

        log.LogInformation(
            "Pharmacy {Owner} queued {Source} '{Name}' for {Count} target(s), due {Due}",
            ownerId, request.SourceKind, resolved.Name, chosen.Count,
            request.ScheduledForUtc?.ToString("O") ?? "now");

        return Result<ScheduledPostDto>.Success(await ToDtoAsync(post, ct));
    }

    public async Task<Result<ScheduledPostListDto>> ListAsync(
        string ownerId, PublishStatus? status, int skip, int take,
        IReadOnlyCollection<PublishStatus>? anyOf, CancellationToken ct)
    {
        var query = new ScheduledPostQuery(
            ownerId, status, Math.Max(0, skip), Math.Clamp(take, 1, 100), anyOf);
        var items = await posts.ListAsync(query, ct);
        var total = await posts.CountAsync(query, ct);

        var dtos = new List<ScheduledPostDto>(items.Count);
        foreach (var post in items) dtos.Add(await ToDtoAsync(post, ct));

        return Result<ScheduledPostListDto>.Success(
            new ScheduledPostListDto(dtos, total, query.Skip, query.Take));
    }

    public async Task<Result<ScheduledPostDto>> GetAsync(string ownerId, string id, CancellationToken ct)
    {
        var post = await posts.FindByIdAsync(id, ct);
        if (post is null || post.OwnerId != ownerId)
            return Result<ScheduledPostDto>.NotFound("Post not found.");
        return Result<ScheduledPostDto>.Success(await ToDtoAsync(post, ct));
    }

    /// <summary>Re-queues the targets that have not already succeeded.</summary>
    public async Task<Result<ScheduledPostDto>> RetryAsync(string ownerId, string id, CancellationToken ct)
    {
        var post = await posts.FindByIdAsync(id, ct);
        if (post is null || post.OwnerId != ownerId)
            return Result<ScheduledPostDto>.NotFound("Post not found.");

        var before = post.Status;
        try { post.PrepareRetry(); }
        catch (DomainException ex) { return Result<ScheduledPostDto>.Invalid(ex.Message); }

        if (!await posts.TryUpdateIfStatusAsync(post, before, ct))
            return Result<ScheduledPostDto>.Conflict(MovedOn);
        return Result<ScheduledPostDto>.Success(await ToDtoAsync(post, ct));
    }

    public async Task<Result<bool>> CancelAsync(string ownerId, string id, CancellationToken ct)
    {
        var post = await posts.FindByIdAsync(id, ct);
        if (post is null || post.OwnerId != ownerId)
            return Result<bool>.NotFound("Post not found.");

        var before = post.Status;
        try { post.Cancel(); }
        catch (DomainException ex) { return Result<bool>.Invalid(ex.Message); }

        if (!await posts.TryUpdateIfStatusAsync(post, before, ct))
            return Result<bool>.Conflict(MovedOn);
        return Result<bool>.Success(true);
    }

    public async Task<Result<ScheduledPostDto>> RescheduleAsync(
        string ownerId, string id, DateTime? scheduledForUtc, CancellationToken ct)
    {
        var post = await posts.FindByIdAsync(id, ct);
        if (post is null || post.OwnerId != ownerId)
            return Result<ScheduledPostDto>.NotFound("Post not found.");

        var before = post.Status;
        try { post.Reschedule(scheduledForUtc); }
        catch (DomainException ex) { return Result<ScheduledPostDto>.Invalid(ex.Message); }

        if (!await posts.TryUpdateIfStatusAsync(post, before, ct))
            return Result<ScheduledPostDto>.Conflict(MovedOn);
        return Result<ScheduledPostDto>.Success(await ToDtoAsync(post, ct));
    }

    /* ---------------------------------------------------------------- *
     * Worker-facing
     * ---------------------------------------------------------------- */

    /// <summary>
    /// Backoff before automatic attempt <c>n + 1</c>, given <c>n</c> attempts so far.
    /// </summary>
    /// <remarks>
    /// Two minutes, then ten. Long enough for a Graph rate-limit window or a
    /// brief outage to pass; short enough that a post queued for 8am is not
    /// still trying at lunchtime.
    /// </remarks>
    public static TimeSpan RetryBackoff(int attemptsSoFar) => attemptsSoFar switch
    {
        <= 1 => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// Publishes one already-claimed post to each of its pending targets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every write is conditional on still holding the lease</b>, and the
    /// lease is pushed out before each account. If a write finds the lease
    /// gone — it lapsed and another instance reclaimed the post, or the
    /// pharmacy cancelled it — this stops immediately and sends nothing more.
    /// The write that marks a target Publishing happens <i>before</i> the
    /// platform call, so that check always runs before anything is sent.
    /// </para>
    /// <para>
    /// Targets are attempted in sequence and each result is written as it
    /// happens. A crash halfway leaves the successful ones Published and the
    /// one in flight Publishing; the next claim marks that one Uncertain rather
    /// than sending it again.
    /// </para>
    /// <para>
    /// Failures are classified three ways. Permanent ones (content refused,
    /// account disconnected) are final. Transient ones go back to Pending and
    /// the post is re-queued after a backoff, up to
    /// <see cref="MaxAutomaticAttempts"/>. Ones where the platform may have
    /// published anyway are Uncertain and wait for a person.
    /// </para>
    /// </remarks>
    public async Task ExecuteAsync(ScheduledPost post, TimeSpan leaseDuration, CancellationToken ct)
    {
        var holder = post.LeaseHolder
            ?? throw new InvalidOperationException("ExecuteAsync needs a claimed post.");

        if (post.ResolveInterruptedAttempts() > 0)
        {
            log.LogWarning(
                "Post {Post} was reclaimed with an attempt in flight; marked uncertain rather than resent",
                post.Id);
        }

        // Re-checked now, not only when the post was queued. A post scheduled
        // for next week belongs to whoever the pharmacy is next week: a
        // deactivated account, a drop to Free, or a poster taken back to draft
        // stops it going out.
        if (await WhyNotNowAsync(post, ct) is { } refusal)
        {
            foreach (var target in post.Targets.Where(t => t.Status == PublishTargetStatus.Pending))
                target.MarkFailed(refusal);

            log.LogInformation("Post {Post} not sent: {Reason}", post.Id, refusal);
            post.RecomputeStatus();
            await SaveAsync(post, holder, CancellationToken.None);
            return;
        }

        var forInstagram = post.Targets.Any(t =>
            t.Status == PublishTargetStatus.Pending
            // X takes the same JPEG: its 5 MB image cap is below a full-size poster PNG.
            && t.Platform is SocialPlatform.InstagramBusiness or SocialPlatform.XAccount);

        var resolved = await media.ResolveAsync(
            post.SourceKind, post.SourceId, post.OwnerId, forInstagram, ct);

        if (resolved is null)
        {
            foreach (var target in post.Targets.Where(t => t.Status == PublishTargetStatus.Pending))
                target.MarkFailed("The poster or video no longer exists.");

            post.RecomputeStatus();
            await SaveAsync(post, holder, CancellationToken.None);
            return;
        }

        foreach (var target in post.Targets)
        {
            if (ct.IsCancellationRequested) break;
            if (target.Status != PublishTargetStatus.Pending) continue;

            var connection = await connections.FindByIdAsync(target.ConnectionId, ct);

            if (connection is null || connection.Status == ConnectionStatus.Revoked)
            {
                target.MarkFailed("That account has been disconnected.");
                if (!await SaveAsync(post, holder, CancellationToken.None)) return;
                continue;
            }

            if (!connection.IsUsable(DateTime.UtcNow) || connection.AccessToken is null)
            {
                target.MarkFailed($"{connection.DisplayName} needs to be reconnected.");
                connection.MarkNeedsReauth("The stored credentials have expired.");
                await connections.UpdateAsync(connection, ct);
                if (!await SaveAsync(post, holder, CancellationToken.None)) return;
                continue;
            }

            if (resolved.IsVideo && !connection.Platform.SupportsVideo())
            {
                target.MarkSkipped($"{connection.Platform.DisplayName()} cannot publish videos.");
                if (!await SaveAsync(post, holder, CancellationToken.None)) return;
                continue;
            }

            if (!resolved.IsVideo && !connection.Platform.SupportsImages())
            {
                target.MarkSkipped($"{connection.Platform.DisplayName()} only takes videos.");
                if (!await SaveAsync(post, holder, CancellationToken.None)) return;
                continue;
            }

            var publisher = publishers.FirstOrDefault(p => p.Platform == connection.Platform);
            if (publisher is null || !publisher.IsConfigured)
            {
                target.MarkFailed(
                    $"{connection.Platform.DisplayName()} publishing is not configured on this server.");
                if (!await SaveAsync(post, holder, CancellationToken.None)) return;
                continue;
            }

            if (resolved.UrlFor(connection.Platform) is not { } mediaUrl)
            {
                // Rendering is ours, not the platform's, and usually transient
                // (storage blip); worth the automatic retries.
                target.MarkPublishing();
                if (target.AttemptCount < MaxAutomaticAttempts)
                    target.MarkRetryPending(
                        $"Could not prepare the {connection.Platform.DisplayName()} version of this poster. Trying again automatically.");
                else
                    target.MarkFailed(
                        $"Could not prepare the {connection.Platform.DisplayName()} version of this poster.");
                if (!await SaveAsync(post, holder, CancellationToken.None)) return;
                continue;
            }

            string accessToken;
            try
            {
                accessToken = cipher.Decrypt(connection.AccessToken);
            }
            catch (Exception ex)
            {
                // A decryption failure is a key problem, not a platform
                // problem, and it will affect every connection encrypted with
                // that key. Said plainly so it is not mistaken for an outage.
                log.LogError(ex,
                    "Could not decrypt credentials for connection {Id}. The encryption key may have changed.",
                    connection.Id);
                target.MarkFailed("Stored credentials could not be read. Reconnect the account.");
                connection.MarkNeedsReauth("Stored credentials could not be decrypted.");
                await connections.UpdateAsync(connection, ct);
                if (!await SaveAsync(post, holder, CancellationToken.None)) return;
                continue;
            }

            // The checkpoint. Lease renewed and "in flight" recorded in one
            // conditional write; nothing is sent unless it lands.
            post.ExtendLease(DateTime.UtcNow.Add(leaseDuration));
            target.MarkPublishing();
            if (!await SaveAsync(post, holder, CancellationToken.None)) return;

            // What this publish signs in with, to tell later whether a refusal
            // was of our credential or of one another worker had already rotated.
            var usedCredential = connection.AccessToken;

            PublishOutcome outcome;
            try
            {
                outcome = await publisher.PublishAsync(
                    new PublishRequest(
                        connection.Platform,
                        connection.ExternalAccountId,
                        accessToken,
                        post.Caption,
                        mediaUrl,
                        resolved.IsVideo,
                        connection.ParentAccountId,
                        post.GbpOptions,
                        post.TikTokOptions,
                        SaveRenewal: async renewed =>
                        {
                            try
                            {
                                var encrypted = cipher.Encrypt(renewed.Credential);
                                connection.RenewCredential(encrypted, renewed.ExpiresAtUtc);
                                await connections.RenewCredentialAsync(
                                    connection.Id, encrypted, renewed.ExpiresAtUtc, CancellationToken.None);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                log.LogError(ex, "Could not save the renewed credential for connection {Id}", connection.Id);
                            }
                        }),
                    ct);
            }
            catch (Exception ex)
            {
                // Adapters classify their own failures, so an exception here is
                // unexpected — including our own shutdown cancelling the call.
                // Either way the request may have reached the platform.
                log.LogError(ex, "Publisher {Platform} threw for post {Post}", connection.Platform, post.Id);
                outcome = new PublishOutcome(false, null, null,
                    "The request was interrupted and may have gone through.", OutcomeUnknown: true);
            }

            // Before anything else: the platform rotated the credential, and
            // the old one may already be dead. Saved whatever the outcome.
            if (outcome.RenewedCredential is { } renewed)
            {
                try
                {
                    var encrypted = cipher.Encrypt(renewed.Credential);
                    connection.RenewCredential(encrypted, renewed.ExpiresAtUtc);
                    await connections.RenewCredentialAsync(connection.Id, encrypted, renewed.ExpiresAtUtc, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Could not save the renewed credential for connection {Id}", connection.Id);
                }
            }

            var platformName = connection.Platform.DisplayName();

            // TikTok and X rotate the refresh token on use. A sign-in refused
            // because another worker (or the daily check) rotated it first is
            // not the account's fault: try again with the new one rather than
            // asking the pharmacy to reconnect.
            if (outcome.IsAuthFailure && outcome.RenewedCredential is null
                && await ConnectionWrites.RotatedElsewhereAsync(connections, connection.Id, usedCredential))
            {
                log.LogInformation(
                    "{Platform} connection {Id} was renewed elsewhere during this publish; retrying instead of asking to reconnect",
                    connection.Platform, connection.Id);
                outcome = outcome with
                {
                    IsAuthFailure = false,
                    IsRetryable = true,
                    Error = $"The {platformName} sign-in was renewed by another task at the same moment.",
                };
            }

            if (outcome.Success && !string.IsNullOrWhiteSpace(outcome.ExternalPostId))
            {
                target.MarkPublished(outcome.ExternalPostId!, outcome.Permalink);
                connection.MarkVerified();
                await ConnectionWrites.SaveStatusAsync(connections, connection);

                log.LogInformation(
                    "Published post {Post} to {Platform} account {Account} as {External}",
                    post.Id, connection.Platform, connection.Id, outcome.ExternalPostId);
            }
            else if (outcome.OutcomeUnknown || outcome.Success)
            {
                // Success without an id lands here too: something was created,
                // and we cannot point at it.
                target.MarkUncertain(
                    $"{platformName} may have published this — check the account before retrying. " +
                    (outcome.Error ?? "It did not confirm the post."));

                log.LogWarning(
                    "Publish to {Platform} account {Account} for post {Post} has an unknown outcome: {Error}",
                    connection.Platform, connection.Id, post.Id, outcome.Error);
            }
            else if (outcome.IsRetryable && !outcome.IsAuthFailure
                     && target.AttemptCount < MaxAutomaticAttempts)
            {
                target.MarkRetryPending(RetryNote(outcome.Error));

                log.LogInformation(
                    "Publish to {Platform} account {Account} for post {Post} will be retried (attempt {Attempt}): {Error}",
                    connection.Platform, connection.Id, post.Id, target.AttemptCount, outcome.Error);
            }
            else
            {
                target.MarkFailed(outcome.Error ?? "The platform rejected the post without saying why.");

                if (outcome.IsAuthFailure)
                {
                    connection.MarkNeedsReauth(outcome.Error ?? "Authentication failed.");
                    await ConnectionWrites.SaveStatusAsync(connections, connection);
                }

                log.LogWarning(
                    "Publish to {Platform} account {Account} failed for post {Post}: {Error}",
                    connection.Platform, connection.Id, post.Id, outcome.Error);
            }

            // Not cancellable: the platform has answered, and losing that
            // answer to a shutdown is how a published target ends up resent.
            if (!await SaveAsync(post, holder, CancellationToken.None)) return;
        }

        var waiting = post.Targets.Where(t => t.Status == PublishTargetStatus.Pending).ToList();

        if (waiting.Count == 0)
        {
            post.RecomputeStatus();
        }
        else if (ct.IsCancellationRequested && waiting.Any(t => t.Error is null))
        {
            // Stopping before reaching some accounts. Straight back to the
            // queue so the next instance carries on without waiting for the
            // lease to lapse.
            post.Requeue();
        }
        else
        {
            var attempts = waiting.Max(t => t.AttemptCount);
            post.ScheduleAutomaticRetry(DateTime.UtcNow.Add(RetryBackoff(attempts)));
        }

        await SaveAsync(post, holder, CancellationToken.None);
    }

    /// <summary>
    /// Why this post may not go out at this moment, or null.
    /// </summary>
    /// <remarks>
    /// A missing poster or video is left to the media resolver, which already
    /// reports it in the pharmacy's terms.
    /// </remarks>
    private async Task<string?> WhyNotNowAsync(ScheduledPost post, CancellationToken ct)
    {
        var owner = await users.FindByIdAsync(post.OwnerId, ct);
        if (owner is null || !owner.IsActive)
            return "The account this post belongs to is no longer active.";
        if (owner.Tier == SubscriptionTier.Free)
            return "Social publishing is available on the Starter plan and above.";

        if (post.SourceKind == PublishSourceKind.Poster)
        {
            var poster = await posters.FindByIdAsync(post.SourceId, ct);
            if (poster is not null && poster.OwnerId == post.OwnerId
                && PosterUsage.WhyCannotSend(poster, "posted to social") is { } cannotPost)
                return cannotPost;
        }

        return null;
    }

    /// <summary>Conditional save; logs and answers false when the lease is gone.</summary>
    /// <remarks>
    /// Callers pass <see cref="CancellationToken.None"/>: these writes are quick,
    /// and one abandoned halfway by a shutdown would leave the in-memory post
    /// saying "in flight" for a call that was never made — which the next
    /// claim would then mark uncertain for no reason.
    /// </remarks>
    private async Task<bool> SaveAsync(ScheduledPost post, string holder, CancellationToken ct)
    {
        if (await posts.TryUpdateClaimedAsync(post, holder, ct)) return true;

        log.LogWarning(
            "Lost the lease on post {Post} (cancelled, or reclaimed after it lapsed); stopping without sending more",
            post.Id);
        return false;
    }

    /* ---------------------------------------------------------------- *
     * Mapping
     * ---------------------------------------------------------------- */

    private async Task<ScheduledPostDto> ToDtoAsync(ScheduledPost post, CancellationToken ct)
    {
        // Account names are resolved for display so history stays readable
        // after a connection is renamed at the platform or disconnected here.
        var targets = new List<PublishTargetDto>(post.Targets.Count);

        foreach (var target in post.Targets)
        {
            var connection = await connections.FindByIdAsync(target.ConnectionId, ct);
            targets.Add(new PublishTargetDto(
                target.ConnectionId,
                target.Platform,
                target.Platform.DisplayName(),
                connection?.DisplayName ?? "(disconnected account)",
                target.Status,
                target.ExternalPostId,
                target.Permalink,
                target.Error,
                target.AttemptCount,
                target.PublishedAtUtc));
        }

        return new ScheduledPostDto(
            post.Id,
            post.SourceKind,
            post.SourceId,
            post.SourceName,
            post.Caption,
            post.ScheduledForUtc,
            post.Status,
            targets,
            post.GbpOptions,
            post.CompletedAtUtc,
            post.CreatedAtUtc,
            CanRetry: post.Status is PublishStatus.Failed or PublishStatus.PartiallyPublished,
            CanCancel: post.Status is PublishStatus.Scheduled or PublishStatus.Draft,
            NextAttemptAtUtc: post.Status == PublishStatus.Scheduled ? post.NextAttemptAtUtc : null,
            SeriesId: post.SeriesId,
            TikTokOptions: post.TikTokOptions);
    }
}
