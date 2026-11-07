using Acme.Application.Common;
using Acme.Domain.Campaigns;

namespace Acme.Application.Campaigns;

public interface ICampaignRepository
{
    Task<IReadOnlyList<Campaign>> ListByOwnerAsync(string ownerId, CancellationToken ct);

    /// <summary>
    /// The Campaigns page: one owner's campaigns, sorted and ranged on when each
    /// was sent — or, for one never sent (queued, stopped, failed), when it was
    /// created.
    /// </summary>
    /// <remarks>
    /// The default body exists so test doubles keep compiling; the Mongo
    /// repository overrides it with a database query.
    /// </remarks>
    async Task<IReadOnlyList<Campaign>> ListAsync(string ownerId, ListWindow window, CancellationToken ct)
    {
        var all = await ListByOwnerAsync(ownerId, ct);
        var inRange = all.Where(c => window.Contains(ListedAt(c)));
        return (window.Ascending
                ? inRange.OrderBy(ListedAt).ThenBy(c => c.Id, StringComparer.Ordinal)
                : inRange.OrderByDescending(ListedAt).ThenByDescending(c => c.Id, StringComparer.Ordinal))
            .ToList();
    }

    /// <summary>The date a campaign is listed, filtered and sorted by.</summary>
    static DateTime ListedAt(Campaign c) => c.SentAtUtc ?? c.CreatedAtUtc;
    Task<Campaign?> FindByIdAsync(string id, CancellationToken ct);
    Task AddAsync(Campaign campaign, CancellationToken ct);
    Task UpdateAsync(Campaign campaign, CancellationToken ct);
    Task<int> CountRecipientsThisMonthAsync(string ownerId, CancellationToken ct);

    /// <summary>
    /// Writes what a group of recipients did, and reports the campaign's status
    /// as the database now has it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exists because Stop did not work.</b> <c>UpdateAsync</c> replaces the
    /// whole document from the worker's in-memory copy, and that copy was read
    /// before the send began — so it still says <c>Sending</c>. A pharmacist who
    /// pressed Stop during a thousand-recipient campaign had the button
    /// acknowledged, saw the status change, and then had it overwritten by the
    /// next group's write. The send continued to the end and the campaign
    /// finished as <c>Sent</c>.
    /// </para>
    /// <para>
    /// This writes only what the send itself produced — the per-recipient
    /// report, the sent count, the renewed lease — and never the status. The
    /// status is then read back, so the caller learns about a Stop written by
    /// anyone else and can halt at the group boundary, which is what the comment
    /// on <c>Campaign.Stop</c> always claimed happened.
    /// </para>
    /// <para>
    /// A group boundary, not an instant: a Stop written just after this read is
    /// seen at the next one, by which time that group has gone. The guarantee is
    /// therefore at most one further group — twenty people — not zero.
    /// </para>
    /// <para>
    /// One round trip: the write and the read are the same operation, so there
    /// is no window between them for a Stop to slip through unseen.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The campaign's status after the write, or null if the campaign is gone.
    /// </returns>
    Task<CampaignStatus?> SaveProgressAsync(Campaign campaign, CancellationToken ct);

    /// <summary>
    /// Takes one campaign that needs sending, or returns null if there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The claim has to be atomic, and it has to be the database that does
    /// it.</b> Read-then-write would let two workers — two app-service
    /// instances, or one mid-deploy overlapping its replacement — both decide
    /// the same campaign was theirs, and every recipient would be emailed twice.
    /// </para>
    /// <para>
    /// It also picks up a campaign whose lease has expired. A worker that is
    /// killed cannot release anything, so expiry is the only way a half-sent
    /// campaign comes back. Resuming is safe because dispatch sends only to
    /// recipients still marked <c>Pending</c>, and those were written after
    /// every batch.
    /// </para>
    /// </remarks>
    /// <param name="worker">Identifies the claimer, for the log and for support.</param>
    /// <param name="leaseUntilUtc">When the claim lapses if not renewed.</param>
    Task<Campaign?> ClaimNextForDispatchAsync(
        string worker, DateTime leaseUntilUtc, CancellationToken ct);

    /// <summary>
    /// Platform-wide campaign totals for the admin dashboard.
    /// </summary>
    /// <remarks>
    /// "Sent" means the campaign actually went out (SentAtUtc is set), not that
    /// it exists. A drafted campaign that was never sent is not activity, and
    /// counting it would inflate the headline number the whole dashboard is
    /// judged by.
    /// </remarks>
    Task<CampaignTotals> GetPlatformTotalsAsync(CancellationToken ct);
}

/// <param name="RecipientsReached">
/// Sum of per-campaign sent counts — how many messages actually left, which is
/// not the same as the number of patient records on file.
/// </param>
public sealed record CampaignTotals(
    long SentTotal,
    long SentThisMonth,
    long RecipientsReached);
