using Acme.Domain.Common;

namespace Acme.Domain.Social;

/// <summary>
/// One target of a publish request, and what happened to it.
/// </summary>
/// <remarks>
/// Mutable, unlike most value objects here, because the worker updates a single
/// target's outcome without touching its siblings. Making it immutable would
/// mean rebuilding the whole list on every attempt, and a concurrent write to
/// two targets of the same post would silently drop one of the results.
/// </remarks>
public sealed class PublishTarget
{
    public string ConnectionId { get; private set; } = default!;
    public SocialPlatform Platform { get; private set; }
    public PublishTargetStatus Status { get; private set; } = PublishTargetStatus.Pending;

    /// <summary>The platform's id for the created post. Our proof it exists.</summary>
    public string? ExternalPostId { get; private set; }

    /// <summary>A link the pharmacy can open to see the live post, when the platform gives one.</summary>
    public string? Permalink { get; private set; }

    public string? Error { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime? LastAttemptAtUtc { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }

    private PublishTarget() { }

    public static PublishTarget For(string connectionId, SocialPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new DomainException("A publish target needs a connection.");

        return new PublishTarget { ConnectionId = connectionId, Platform = platform };
    }

    public void MarkPublishing()
    {
        Status = PublishTargetStatus.Publishing;
        AttemptCount++;
        LastAttemptAtUtc = DateTime.UtcNow;
    }

    public void MarkPublished(string externalPostId, string? permalink)
    {
        Status = PublishTargetStatus.Published;
        ExternalPostId = externalPostId;
        Permalink = permalink;
        PublishedAtUtc = DateTime.UtcNow;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        Status = PublishTargetStatus.Failed;
        Error = Truncate(error, 1000);
    }

    public void MarkSkipped(string reason)
    {
        Status = PublishTargetStatus.Skipped;
        Error = Truncate(reason, 1000);
    }

    /// <summary>
    /// A transient failure that the worker will try again on its own.
    /// </summary>
    /// <remarks>
    /// Back to Pending, with the reason kept so the pharmacy can see why the
    /// post is late rather than just that it is.
    /// </remarks>
    public void MarkRetryPending(string error)
    {
        if (Status == PublishTargetStatus.Published) return;
        Status = PublishTargetStatus.Pending;
        Error = Truncate(error, 1000);
    }

    /// <summary>
    /// The platform may or may not have published this.
    /// </summary>
    /// <remarks>
    /// A timeout after the request was sent, a crash mid-call, or a platform
    /// that accepted the request but returned no id. Retrying automatically
    /// risks a second copy on the pharmacy's feed, which cannot be retracted,
    /// so this stops and asks a person to look first.
    /// </remarks>
    public void MarkUncertain(string message)
    {
        if (Status == PublishTargetStatus.Published) return;
        Status = PublishTargetStatus.Uncertain;
        Error = Truncate(message, 1000);
    }

    /// <summary>
    /// True when this target may be attempted again.
    /// </summary>
    /// <remarks>
    /// Anything already Published is excluded, which is the rule that stops a
    /// retry double-posting. The attempt ceiling applies to automatic retries
    /// only — a person can still retry by hand (which resets the count),
    /// because "the page was temporarily unpublished and now isn't" is a real
    /// situation and no attempt count should permanently close the door on it.
    /// </remarks>
    public bool CanRetry(int maxAttempts) =>
        Status is PublishTargetStatus.Failed or PublishTargetStatus.Pending
        && AttemptCount < maxAttempts;

    /// <summary>Clears a failure so a manual retry starts clean.</summary>
    /// <remarks>
    /// The attempt count starts again too: a person pressing Retry is a new
    /// decision, and leaving the count at the automatic ceiling would make the
    /// worker skip the target and leave the post in Publishing for ever.
    /// Skipped targets stay skipped — the media still does not suit them.
    /// </remarks>
    public void ResetForManualRetry()
    {
        if (Status is PublishTargetStatus.Published or PublishTargetStatus.Skipped) return;
        Status = PublishTargetStatus.Pending;
        Error = null;
        AttemptCount = 0;
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s[..max];
}

/// <summary>Extra fields Google Business Profile needs for non-Update posts.</summary>
public sealed record GbpPostOptions(
    GbpPostType PostType,
    string? CouponCode = null,
    string? RedeemOnlineUrl = null,
    string? TermsConditions = null,
    string? EventTitle = null,
    DateTime? EventStartUtc = null,
    DateTime? EventEndUtc = null,
    string? CallToActionType = null,
    string? CallToActionUrl = null);

/// <summary>
/// What the pharmacy chose for a TikTok post, as TikTok's Content Sharing
/// Guidelines require: who can see it (from the options TikTok offers that
/// account), whether others may comment, Duet or Stitch, and whether it
/// promotes a business.
/// </summary>
/// <remarks>
/// Chosen per post, never defaulted on the pharmacy's behalf — TikTok's
/// review checks exactly that, and a privacy level the account does not
/// offer is refused at publish time.
/// </remarks>
public sealed record TikTokPostOptions(
    string PrivacyLevel,
    bool AllowComments = false,
    bool AllowDuet = false,
    bool AllowStitch = false,
    bool PromotesOwnBusiness = false,
    bool IsPaidPartnership = false)
{
    public static readonly string[] PrivacyLevels =
    {
        "PUBLIC_TO_EVERYONE", "MUTUAL_FOLLOW_FRIENDS", "FOLLOWER_OF_CREATOR", "SELF_ONLY",
    };

    /// <summary>Null when valid, else what is wrong, in the pharmacy's words.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(PrivacyLevel) || !PrivacyLevels.Contains(PrivacyLevel)
            ? "Choose who can see the TikTok post."
            // TikTok's own rule: branded (paid partnership) content cannot be private.
            : IsPaidPartnership && PrivacyLevel == "SELF_ONLY"
                ? "A paid partnership on TikTok cannot be private. Choose another audience."
                : null;
}

/// <summary>
/// A request to publish one poster or video to one or more connected accounts,
/// now or at a stated time.
/// </summary>
/// <remarks>
/// <para>
/// The unit of work is the request, not the individual post, because a pharmacy
/// composes once and sends to three places. Each target carries its own status
/// so a partial failure is legible — "Facebook went out, Instagram rejected the
/// aspect ratio" — rather than collapsing to a single red cross.
/// </para>
/// <para>
/// <b>The lease is the important part.</b> Two API instances polling the same
/// collection will both see the same due post. Without an atomic claim they
/// both publish it, and the pharmacy's followers see it twice — an error we
/// cannot retract from Meta and cannot apologise our way out of. The lease
/// fields here are what the repository's findAndModify claims against.
/// </para>
/// </remarks>
public sealed class ScheduledPost : Entity
{
    public string OwnerId { get; private set; } = default!;

    public PublishSourceKind SourceKind { get; private set; }

    /// <summary>Poster or Video id. Resolved to media at publish time, not now.</summary>
    public string SourceId { get; private set; } = default!;

    /// <summary>Shown to the pharmacy so history is readable after the source is renamed.</summary>
    public string SourceName { get; private set; } = string.Empty;

    public string Caption { get; private set; } = string.Empty;

    /// <summary>
    /// When this should go out. Null means immediately.
    /// </summary>
    /// <remarks>
    /// Always UTC. The UI collects a local date and time and converts, because
    /// storing wall-clock time would move every scheduled post by an hour when
    /// the clocks change — and a flu-jab campaign going out at 07:00 instead of
    /// 08:00 is exactly the sort of thing nobody notices until a customer does.
    /// </remarks>
    public DateTime? ScheduledForUtc { get; private set; }

    public PublishStatus Status { get; private set; } = PublishStatus.Draft;

    private readonly List<PublishTarget> _targets = new();
    public IReadOnlyList<PublishTarget> Targets => _targets;

    public GbpPostOptions? GbpOptions { get; private set; }

    /// <summary>Set when a target is a TikTok account; required for one.</summary>
    public TikTokPostOptions? TikTokOptions { get; private set; }

    /* ---------------- worker lease ---------------- */

    /// <summary>Identifies the instance currently working this post. Null when unclaimed.</summary>
    public string? LeaseHolder { get; private set; }

    /// <summary>
    /// When the claim lapses. A worker that crashes mid-publish leaves this in
    /// the past, and the post becomes claimable again rather than being stuck
    /// in Publishing for ever.
    /// </summary>
    public DateTime? LeaseExpiresAtUtc { get; private set; }

    /// <summary>
    /// Not before this instant, when an automatic retry is waiting out its
    /// backoff. Separate from <see cref="ScheduledForUtc"/>, which is the
    /// pharmacy's choice and must still read as what they chose.
    /// </summary>
    public DateTime? NextAttemptAtUtc { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    /// <summary>The recurring series this is one occurrence of. Null for a one-off post.</summary>
    public string? SeriesId { get; private set; }

    /// <summary>The series' instant for this occurrence. Null for a one-off post.</summary>
    public DateTime? OccurrenceAtUtc { get; private set; }

    private ScheduledPost() { }

    /// <summary>
    /// One occurrence of a recurring series, as an ordinary scheduled post.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The id is not random.</b> It is the series id plus the occurrence
    /// instant (<see cref="RecurringSeries.OccurrenceId"/>), so two schedulers
    /// working out the same occurrence write the same document and the
    /// database keeps one. From then on it is a post like any other: the
    /// publish worker claims it with the same lease, and its retries, Uncertain
    /// targets and manual Retry all reuse this one id.
    /// </para>
    /// <para>
    /// No "must be in the future" check, unlike <see cref="Create"/>: an
    /// occurrence is created when it falls due, which is now or a moment ago.
    /// The series has already decided it is fresh enough to send.
    /// </para>
    /// </remarks>
    public static ScheduledPost CreateOccurrence(
        RecurringSeries series,
        DateTime occurrenceAtUtc,
        IEnumerable<(string ConnectionId, SocialPlatform Platform)> targets)
    {
        var list = targets?.ToList() ?? new List<(string, SocialPlatform)>();
        if (list.Count == 0)
            throw new DomainException("This series has no accounts left to post to.");

        var at = DateTime.SpecifyKind(occurrenceAtUtc, DateTimeKind.Utc);
        var post = new ScheduledPost
        {
            Id = series.OccurrenceId(at),
            OwnerId = series.OwnerId,
            SourceKind = series.SourceKind,
            SourceId = series.SourceId,
            SourceName = series.SourceName,
            Caption = series.Caption,
            ScheduledForUtc = at,
            Status = PublishStatus.Scheduled,
            SeriesId = series.Id,
            OccurrenceAtUtc = at,
        };

        foreach (var (connectionId, platform) in list.DistinctBy(t => t.ConnectionId))
            post._targets.Add(PublishTarget.For(connectionId, platform));

        return post;
    }

    public static ScheduledPost Create(
        string ownerId,
        PublishSourceKind sourceKind,
        string sourceId,
        string sourceName,
        string caption,
        DateTime? scheduledForUtc,
        IEnumerable<(string ConnectionId, SocialPlatform Platform)> targets,
        GbpPostOptions? gbpOptions = null,
        TikTokPostOptions? tikTokOptions = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new DomainException("Choose a poster or video to publish.");

        var list = targets?.ToList() ?? new List<(string, SocialPlatform)>();
        if (list.Count == 0)
            throw new DomainException("Choose at least one account to publish to.");

        // Captions are capped well below the shortest platform limit rather
        // than at it. Instagram allows 2200 characters; being refused at 2201
        // after composing is a worse experience than being stopped while typing.
        if (caption.Length > 2200)
            throw new DomainException("Caption must be 2200 characters or fewer.");

        if (list.Any(t => t.Platform == SocialPlatform.TikTokAccount))
        {
            if (tikTokOptions is null)
                throw new DomainException("Choose who can see the TikTok post, and its settings.");
            if (tikTokOptions.Problem() is { } problem)
                throw new DomainException(problem);
        }

        // X counts differently (links 23, emoji 2) and allows far fewer.
        if (list.Any(t => t.Platform == SocialPlatform.XAccount) && !XText.Fits(caption?.Trim()))
            throw new DomainException(
                $"The caption is too long for X: {XText.WeightedLength(caption?.Trim())} of {XText.MaxWeightedLength} " +
                "(X counts each link as 23 and each emoji as 2). Shorten it, or post to X separately.");

        if (scheduledForUtc is { } when)
        {
            if (when <= DateTime.UtcNow.AddMinutes(-1))
                throw new DomainException("Choose a time in the future, or publish now.");
            if (when > DateTime.UtcNow.AddYears(1))
                throw new DomainException("Scheduled posts cannot be more than a year ahead.");
        }

        var post = new ScheduledPost
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            SourceKind = sourceKind,
            SourceId = sourceId,
            SourceName = (sourceName ?? string.Empty).Trim(),
            Caption = (caption ?? string.Empty).Trim(),
            ScheduledForUtc = scheduledForUtc,
            GbpOptions = gbpOptions,
            TikTokOptions = list.Any(t => t.Platform == SocialPlatform.TikTokAccount) ? tikTokOptions : null,
            // Scheduled either way. "Publish now" is not a separate code path —
            // it is a scheduled post with no time, which the worker treats as
            // already due. One path means the immediate case gets the same
            // lease, retry and status handling as everything else, instead of
            // being the one that publishes twice under load.
            Status = PublishStatus.Scheduled,
        };

        foreach (var (connectionId, platform) in list.DistinctBy(t => t.ConnectionId))
            post._targets.Add(PublishTarget.For(connectionId, platform));

        return post;
    }

    /// <summary>Claims this post for a worker instance.</summary>
    /// <remarks>
    /// Called only after the repository has won the atomic claim. This method
    /// records the outcome; it does not itself provide mutual exclusion, and
    /// calling it without the database claim would defeat the whole mechanism.
    /// </remarks>
    public void MarkClaimed(string leaseHolder, DateTime leaseExpiresAtUtc)
    {
        LeaseHolder = leaseHolder;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        Status = PublishStatus.Publishing;
        Touch();
    }

    public void ReleaseLease()
    {
        LeaseHolder = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    /// <summary>
    /// Pushes the lease out before a long step. Persisted with a write that is
    /// conditional on still holding the lease, so it never steals one back.
    /// </summary>
    public void ExtendLease(DateTime leaseExpiresAtUtc)
    {
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        Touch();
    }

    /// <summary>
    /// Targets a previous worker left mid-call are marked uncertain.
    /// </summary>
    /// <remarks>
    /// A target still in Publishing when the post is claimed means the last
    /// worker died between sending to the platform and recording the answer.
    /// It may well be live. Sending again would be the duplicate this whole
    /// design exists to prevent, so it waits for a person instead.
    /// </remarks>
    /// <returns>How many targets were affected.</returns>
    public int ResolveInterruptedAttempts()
    {
        var count = 0;
        foreach (var target in _targets.Where(t => t.Status == PublishTargetStatus.Publishing))
        {
            target.MarkUncertain(
                $"Publishing to {target.Platform.DisplayName()} was interrupted. " +
                "It may have gone out — check the account before retrying.");
            count++;
        }
        if (count > 0) Touch();
        return count;
    }

    /// <summary>
    /// Hands the post back to the queue for an automatic retry after a backoff.
    /// </summary>
    public void ScheduleAutomaticRetry(DateTime notBeforeUtc)
    {
        Status = PublishStatus.Scheduled;
        NextAttemptAtUtc = notBeforeUtc;
        LeaseHolder = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    /// <summary>
    /// Hands the post straight back, due now — used when the worker is
    /// stopping with targets it had not reached yet.
    /// </summary>
    public void Requeue()
    {
        Status = PublishStatus.Scheduled;
        NextAttemptAtUtc = null;
        LeaseHolder = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    public PublishTarget? TargetFor(string connectionId) =>
        _targets.FirstOrDefault(t => t.ConnectionId == connectionId);

    /// <summary>
    /// Recomputes the overall status from the targets and closes the post if
    /// nothing is outstanding.
    /// </summary>
    public void RecomputeStatus()
    {
        if (Status == PublishStatus.Cancelled) return;

        var outstanding = _targets.Any(t =>
            t.Status is PublishTargetStatus.Pending or PublishTargetStatus.Publishing);

        if (outstanding)
        {
            Status = PublishStatus.Publishing;
            Touch();
            return;
        }

        var published = _targets.Count(t => t.Status == PublishTargetStatus.Published);
        // Uncertain counts with the failures: it needs a person, and Retry is
        // how they act on it once they have checked.
        var failed = _targets.Count(t =>
            t.Status is PublishTargetStatus.Failed or PublishTargetStatus.Uncertain);

        // Skipped targets count as neither. A request whose only non-published
        // target was skipped is a success — nothing went wrong, one platform
        // simply could not carry that media.
        Status = published switch
        {
            0 when failed > 0 => PublishStatus.Failed,
            > 0 when failed > 0 => PublishStatus.PartiallyPublished,
            > 0 => PublishStatus.Published,
            _ => PublishStatus.Failed,
        };

        CompletedAtUtc = DateTime.UtcNow;
        NextAttemptAtUtc = null;
        LeaseHolder = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    /// <summary>
    /// Prepares a failed or partially published post for another attempt.
    /// </summary>
    /// <remarks>
    /// Only unpublished targets are reset. This is the single place that
    /// guarantees a retry cannot repost to somewhere it already succeeded, and
    /// it is enforced in <see cref="PublishTarget.ResetForManualRetry"/> as
    /// well — twice, because the consequence of getting it wrong is visible to
    /// the pharmacy's customers.
    /// </remarks>
    public void PrepareRetry()
    {
        if (Status is PublishStatus.Publishing)
            throw new DomainException("This post is currently publishing. Wait for it to finish.");
        if (Status is PublishStatus.Published)
            throw new DomainException("Every account already received this post.");
        if (Status is PublishStatus.Cancelled)
            throw new DomainException("This post was cancelled.");
        // Only something that has finished, badly. Retry makes a post due
        // now, so allowing it on a Scheduled one would publish next week's
        // post this minute; and a Draft has nothing to retry.
        if (Status is not (PublishStatus.Failed or PublishStatus.PartiallyPublished))
            throw new DomainException("Only a failed or partly published post can be retried.");

        foreach (var target in _targets) target.ResetForManualRetry();

        Status = PublishStatus.Scheduled;
        ScheduledForUtc = null;   // due immediately
        NextAttemptAtUtc = null;
        CompletedAtUtc = null;
        LeaseHolder = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    public void Cancel()
    {
        if (Status is PublishStatus.Published or PublishStatus.PartiallyPublished)
            throw new DomainException("This post has already gone out and cannot be cancelled.");
        if (Status == PublishStatus.Publishing)
            throw new DomainException("This post is publishing right now and cannot be cancelled.");

        // Waiting on an automatic retry after some accounts already have it:
        // stop the rest, but do not pretend nothing went out. The post closes
        // as what it is, with the stopped accounts shown as skipped.
        if (_targets.Any(t => t.Status == PublishTargetStatus.Published))
        {
            foreach (var target in _targets.Where(t =>
                         t.Status is PublishTargetStatus.Pending or PublishTargetStatus.Failed))
                target.MarkSkipped("Cancelled before it was sent.");
            RecomputeStatus();
            return;
        }

        Status = PublishStatus.Cancelled;
        NextAttemptAtUtc = null;
        LeaseHolder = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    public void Reschedule(DateTime? scheduledForUtc)
    {
        if (Status is not (PublishStatus.Scheduled or PublishStatus.Draft))
            throw new DomainException("Only a post that has not gone out can be rescheduled.");
        if (scheduledForUtc is { } when && when <= DateTime.UtcNow)
            throw new DomainException("Choose a time in the future.");

        ScheduledForUtc = scheduledForUtc;
        Touch();
    }

    /// <summary>True when the worker should pick this up.</summary>
    public bool IsDue(DateTime nowUtc) =>
        Status == PublishStatus.Scheduled
        && (ScheduledForUtc is null || ScheduledForUtc <= nowUtc)
        && (NextAttemptAtUtc is null || NextAttemptAtUtc <= nowUtc);
}
