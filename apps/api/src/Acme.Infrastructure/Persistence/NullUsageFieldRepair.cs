using Acme.Application.Common;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

/// <summary>
/// Removes worker-owned fields that were stored as explicit nulls.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fault.</b> <c>VideoDocument</c>'s four worker-owned fields were
/// nullable, and <c>AddAsync</c> inserts the whole document — so every video
/// created before <c>[BsonIgnoreIfNull]</c> carries <c>usage: null</c>,
/// <c>usageAttempts: null</c>, <c>usageTotals: null</c>, <c>facts: null</c>.
/// </para>
/// <para>
/// MongoDB's <c>$push</c> refuses an explicitly null field — "The field
/// 'usageAttempts' must be an array but is of type null" — while creating the
/// array happily when the field is absent. Absent and null are different
/// things, and the repository's rule was "never write these", which turned out
/// to include never writing them as null.
/// </para>
/// <para>
/// <b>Why it froze rather than failed.</b> The usage write runs on the success
/// path and on the failure path. A render that failed could not record that it
/// had failed, so the video stayed <c>Rendering</c>, the lease lapsed, the
/// worker re-claimed it, and it failed the same way again — for ever, with the
/// customer watching a progress bar. The attribute fixes new videos; this
/// fixes the ones already in the database, which are the ones currently stuck.
/// </para>
/// <para>
/// Idempotent: the filter matches only fields that are present and null, so a
/// second run matches nothing. Runs on every boot for the same reason the other
/// repairs do — an environment restored from an old backup repairs itself.
/// </para>
/// </remarks>
public sealed class NullUsageFieldRepair(
    MongoContext ctx,
    ILogger<NullUsageFieldRepair> log) : IStartupTask
{
    /// <summary>
    /// The worker-owned fields. Must match the <c>[BsonIgnoreIfNull]</c> set on
    /// <see cref="VideoDocument"/> — a field added there and missed here is a
    /// video that freezes on its first render.
    /// </summary>
    private static readonly string[] WorkerOwned =
        ["usage", "usageAttempts", "usageTotals", "facts"];

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var videos = ctx.Collection<VideoDocument>("videos");
        var f = Builders<VideoDocument>.Filter;

        var repaired = 0;
        foreach (var field in WorkerOwned)
        {
            // Present AND null. `Eq(null)` alone also matches documents where
            // the field is missing, which is every correctly-written video —
            // so without the `Exists` the update would touch the whole
            // collection on every boot to accomplish nothing.
            var brokenlyNull = f.And(f.Exists(field, true), f.Eq<object?>(field, null));

            var result = await videos.UpdateManyAsync(
                brokenlyNull,
                Builders<VideoDocument>.Update.Unset(field),
                cancellationToken: ct);

            if (result.ModifiedCount > 0)
            {
                repaired += (int)result.ModifiedCount;
                log.LogInformation(
                    "Unset a null '{Field}' on {Count} video(s); $push and $inc "
                    + "can write to them now.", field, result.ModifiedCount);
            }
        }

        if (repaired > 0)
        {
            log.LogInformation(
                "Repaired {Count} null worker-owned field(s) on videos. Any video "
                + "stuck in Rendering because its usage write failed can now be "
                + "retried.", repaired);
        }
    }
}
