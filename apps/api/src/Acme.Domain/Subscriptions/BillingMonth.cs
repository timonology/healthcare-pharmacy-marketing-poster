namespace Acme.Domain.Subscriptions;

/// <summary>
/// When the allowance month begins. One definition, used by every quota.
/// </summary>
/// <remarks>
/// <para>
/// <b>This was written out by hand in seven places</b> — the poster, video,
/// campaign, user and AI-generation counters, plus analytics — each computing
/// the same <c>new DateTime(UtcNow.Year, UtcNow.Month, 1, …, Utc)</c>. They
/// agreed, which is the only reason nobody noticed. The failure mode if one had
/// drifted is not an exception: it is a pharmacy told it has used 4 of 5 videos
/// by one screen and 0 of 5 by another, or an allowance that resets on a
/// different day from the one the invoice covers.
/// </para>
/// <para>
/// <b>Calendar month, UTC.</b> Not a rolling 30 days and not the subscription's
/// own anniversary. Both alternatives are defensible and one of them may
/// eventually be right — Stripe bills on the anniversary, so a pharmacy that
/// subscribes on the 20th currently gets a partial first allowance. That is a
/// product decision with a migration attached, and the point of this type is
/// that making it later means changing one line rather than finding seven.
/// </para>
/// <para>
/// UTC and not local time, deliberately: "this month" has to mean the same
/// thing on a server in Dublin and one in Sydney, or the allowance a pharmacy
/// sees depends on which instance answered.
/// </para>
/// </remarks>
public static class BillingMonth
{
    /// <summary>Midnight UTC on the first of the current month.</summary>
    public static DateTime StartUtc() => StartUtcFor(DateTime.UtcNow);

    /// <summary>
    /// Midnight UTC on the first of the month containing <paramref name="moment"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="StartUtc"/> so it can be tested without waiting
    /// for a month to turn over.
    /// </remarks>
    public static DateTime StartUtcFor(DateTime moment)
    {
        var utc = moment.Kind == DateTimeKind.Utc ? moment : moment.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
