using Acme.Domain.Common;

namespace Acme.Domain.Campaigns;

public enum CampaignChannel { Email = 0, Sms = 1 }

/// <summary>
/// <c>Queued</c> was added when dispatch moved off the request thread. It means
/// accepted and durable, not yet claimed by a worker. <c>Sending</c> now means
/// a worker holds a lease on it.
/// </summary>
public enum CampaignStatus { Draft = 0, Sending = 1, Sent = 2, Stopped = 3, Failed = 4, Queued = 5 }

public enum RecipientStatus { Pending = 0, Sent = 1, Failed = 2 }

/// <summary>
/// One recipient's outcome. <b>This is the record of who has been sent to</b>,
/// not a report written afterwards.
/// </summary>
/// <remarks>
/// It used to be the latter: a report produced at the end of a send, written and
/// never read back. <c>RetryAsync</c> therefore re-sent to the whole original
/// list, so a campaign that delivered 700 of 1000 and failed would deliver those
/// 700 a second time when somebody pressed Retry. At four recipients the bug is
/// invisible, because one request either works or does not.
/// <para>
/// Dispatch now sends only to entries that are <see cref="RecipientStatus.Pending"/>.
/// That one rule makes retry, crash recovery and a duplicated worker all safe
/// without any of them being handled separately.
/// </para>
/// </remarks>
public sealed class RecipientResult
{
    public string Recipient { get; init; } = default!;
    public RecipientStatus Status { get; init; }
    public string? Reason { get; init; }
    public DateTime AtUtc { get; init; }
}

public sealed class Campaign : Entity
{
    public string OwnerId  { get; private set; } = default!;
    public string PosterId { get; private set; } = default!;
    /// <summary>Optional list of video ids to attach alongside the poster PDF.
    /// Empty for legacy poster-only campaigns.</summary>
    public IReadOnlyList<string> VideoIds { get; private set; } = Array.Empty<string>();

    public string Name { get; private set; } = default!;
    public CampaignChannel Channel { get; private set; }
    public CampaignStatus Status { get; private set; } = CampaignStatus.Draft;
    public IReadOnlyList<string> Recipients { get; private set; } = Array.Empty<string>();

    /// <summary>Per-recipient outcome, one entry per Recipients element.</summary>
    public IReadOnlyList<RecipientResult> DeliveryReport { get; private set; } = Array.Empty<RecipientResult>();

    public int SentCount { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public string? Note { get; private set; }

    /// <summary>Which worker holds this campaign, and until when.</summary>
    /// <remarks>
    /// A lease rather than a lock. A worker that crashes cannot release anything,
    /// so the only way a campaign comes back is for the claim to expire on its
    /// own. Before this existed, <c>Sending</c> was set and never read by
    /// anything: a restart mid-send left the campaign in that state permanently,
    /// unable to finish and — because Retry only accepts <c>Failed</c> — unable
    /// to be retried either.
    /// </remarks>
    public string? LeaseOwner { get; private set; }
    public DateTime? LeaseExpiresAtUtc { get; private set; }

    /// <summary>Recipients still to be sent to.</summary>
    /// <remarks>
    /// Derived, never stored. A stored copy is a second source of truth that can
    /// disagree with the delivery report, and the disagreement would be silent.
    /// </remarks>
    public IReadOnlyList<string> PendingRecipients =>
        DeliveryReport.Count == 0
            ? Recipients
            : DeliveryReport
                .Where(r => r.Status == RecipientStatus.Pending)
                .Select(r => r.Recipient)
                .ToList();

    public int DeliveredCount =>
        DeliveryReport.Count(r => r.Status == RecipientStatus.Sent);

    private Campaign() { }

    public static Campaign Create(
        string ownerId,
        string posterId,
        string name,
        CampaignChannel channel,
        IEnumerable<string> recipients,
        IEnumerable<string>? videoIds = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new DomainException("OwnerId is required.");
        if (string.IsNullOrWhiteSpace(posterId))
            throw new DomainException("Poster id is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Campaign name is required.");

        var list = recipients
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (list.Count == 0)
            throw new DomainException("At least one recipient is required.");

        var vids = (videoIds ?? Array.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)   // per-campaign safety cap; email size limit anyway
            .ToList();

        return new Campaign
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = ownerId,
            PosterId = posterId,
            VideoIds = vids,
            Name = name.Trim(),
            Channel = channel,
            Recipients = list,
            Status = CampaignStatus.Draft,
        };
    }

    /// <summary>
    /// Accepted and durable, waiting for a worker.
    /// </summary>
    /// <remarks>
    /// Every recipient is written as <see cref="RecipientStatus.Pending"/> here,
    /// so the list of who still needs sending exists before the first message
    /// goes out. A crash one second later is then indistinguishable from a
    /// crash halfway through, which is the property that makes recovery
    /// uninteresting.
    /// </remarks>
    public void Queue()
    {
        // Refused on what is outstanding, not on the status.
        //
        // A campaign that reached 700 of 1000 is `Sent` — correctly, 700 people
        // did receive it — and retrying it is the entire point of the delivery
        // report. Gating on `Status == Sent` would have made the one case this
        // was built for impossible, which is what a simulation of these rules
        // showed before it shipped.
        if (Status is CampaignStatus.Stopped)
            throw new DomainException("A stopped campaign cannot be sent.");

        if (DeliveryReport.Count > 0 && DeliveredCount >= Recipients.Count)
            throw new DomainException("Every recipient has already received this campaign.");

        var now = DateTime.UtcNow;
        var done = DeliveryReport
            .Where(r => r.Status == RecipientStatus.Sent)
            .ToDictionary(r => r.Recipient, StringComparer.OrdinalIgnoreCase);

        // Anything already delivered keeps its result. Everything else — never
        // attempted, or attempted and failed — goes back to Pending.
        DeliveryReport = Recipients
            .Select(r => done.TryGetValue(r, out var sent)
                ? sent
                : new RecipientResult
                {
                    Recipient = r,
                    Status = RecipientStatus.Pending,
                    AtUtc = now,
                })
            .ToList();

        Status = CampaignStatus.Queued;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        Note = null;
        Touch();
    }

    /// <summary>Taken by a worker until <paramref name="until"/>.</summary>
    public void Claim(string worker, DateTime until)
    {
        Status = CampaignStatus.Sending;
        LeaseOwner = worker;
        LeaseExpiresAtUtc = until;
        Touch();
    }

    /// <summary>Extends the claim while a long send is still making progress.</summary>
    public void RenewLease(DateTime until)
    {
        LeaseExpiresAtUtc = until;
        Touch();
    }

    /// <summary>
    /// Records what happened to a batch, mid-send.
    /// </summary>
    /// <remarks>
    /// Called after every batch rather than once at the end. That is the
    /// difference between a campaign that resumes and one that starts again:
    /// whatever is durable at the moment of a crash is what will not be sent
    /// twice.
    /// </remarks>
    public void RecordResults(IEnumerable<RecipientResult> results)
    {
        var updates = results.ToDictionary(r => r.Recipient, StringComparer.OrdinalIgnoreCase);
        if (updates.Count == 0) return;

        DeliveryReport = DeliveryReport
            .Select(existing => updates.TryGetValue(existing.Recipient, out var u) ? u : existing)
            .ToList();

        SentCount = DeliveredCount;
        Touch();
    }

    /// <summary>Nothing left to attempt.</summary>
    public void Complete(string? note = null)
    {
        var failed = DeliveryReport.Count(r => r.Status == RecipientStatus.Failed);

        SentCount = DeliveredCount;
        SentAtUtc = DateTime.UtcNow;
        Note = note;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;

        // Failed if nothing at all got through; otherwise Sent, with the report
        // carrying the detail. A campaign that reached 998 of 1000 is not a
        // failure, and calling it one would hide the 998 from the pharmacist.
        Status = SentCount == 0 && failed > 0 ? CampaignStatus.Failed : CampaignStatus.Sent;
        Touch();
    }

    /// <summary>
    /// Replaced by <see cref="Queue"/> and <see cref="Claim"/>.
    /// </summary>
    /// <remarks>
    /// Kept only so an old campaign can still be read. Setting <c>Sending</c>
    /// without a lease is precisely the state that used to strand a campaign
    /// forever: nothing read it, and Retry refuses anything that is not
    /// <c>Failed</c>. Call <c>Queue</c> to ask for a send and let a worker
    /// <c>Claim</c> it.
    /// </remarks>
    [Obsolete("Use Queue() to request a send; the dispatch worker calls Claim().")]
    public void MarkSending() { Status = CampaignStatus.Sending; Touch(); }

    public void MarkSent(int sentCount, string? note = null, IReadOnlyList<RecipientResult>? report = null)
    {
        SentCount = sentCount;
        SentAtUtc = DateTime.UtcNow;
        Note = note;
        if (report is not null) DeliveryReport = report;
        Status = CampaignStatus.Sent;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    public void MarkFailed(string reason, IReadOnlyList<RecipientResult>? report = null)
    {
        Status = CampaignStatus.Failed;
        Note = reason;
        if (report is not null) DeliveryReport = report;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }

    /// <summary>
    /// Cancels the rest of the send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whoever is sending finds out at the next group boundary, by reading this
    /// status back as part of writing that group's results — see
    /// <c>ICampaignRepository.SaveProgressAsync</c>. Recipients already reached
    /// keep their entries in the delivery report and are not sent to again.
    /// </para>
    /// <para>
    /// <b>The worker used to read none of this.</b> It never re-read the status,
    /// and it saved progress by replacing the whole document from a copy taken
    /// before the send began — so a Stop was overwritten by the next group and
    /// the campaign went out in full, then reported itself <c>Sent</c>. Setting
    /// the status here was only ever half of stopping; the other half is that
    /// nothing overwrites it.
    /// </para>
    /// </remarks>
    public void Stop()
    {
        if (Status is CampaignStatus.Sent or CampaignStatus.Stopped)
            throw new DomainException("Campaign cannot be stopped from its current state.");
        Status = CampaignStatus.Stopped;

        // Released, so the campaign is not left looking claimed by a worker that
        // has been told to leave it alone.
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
        Touch();
    }
}
