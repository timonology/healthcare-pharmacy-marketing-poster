using Acme.Application.Social;
using Acme.Domain.Social;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoSocialConnectionRepository(MongoContext ctx) : ISocialConnectionRepository
{
    private IMongoCollection<SocialConnectionDocument> Col =>
        ctx.Collection<SocialConnectionDocument>("social_connections");

    public async Task<IReadOnlyList<SocialConnection>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.OwnerId == ownerId)
            .SortBy(x => x.Platform).ThenBy(x => x.DisplayName)
            .ToListAsync(ct);
        return docs.Select(SocialMapper.Hydrate).ToList();
    }

    public async Task<SocialConnection?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : SocialMapper.Hydrate(doc);
    }

    public async Task<SocialConnection?> FindByExternalAsync(
        string ownerId, SocialPlatform platform, string externalAccountId, CancellationToken ct)
    {
        var fb = Builders<SocialConnectionDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId)
                   & fb.Eq(x => x.Platform, (int)platform)
                   & fb.Eq(x => x.ExternalAccountId, externalAccountId);

        var doc = await Col.Find(filter).FirstOrDefaultAsync(ct);
        return doc is null ? null : SocialMapper.Hydrate(doc);
    }

    public Task AddAsync(SocialConnection connection, CancellationToken ct) =>
        Col.InsertOneAsync(SocialMapper.ToDocument(connection), cancellationToken: ct);

    public Task UpdateAsync(SocialConnection connection, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == connection.Id,
            SocialMapper.ToDocument(connection),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<IReadOnlyList<SocialConnection>> ListByAuthorizerAsync(
        IReadOnlyCollection<SocialPlatform> platforms, string userId, CancellationToken ct)
    {
        var fb = Builders<SocialConnectionDocument>.Filter;
        var filter = fb.Eq(x => x.AuthorizedByUserId, userId)
                   & fb.In(x => x.Platform, platforms.Select(p => (int)p));

        var docs = await Col.Find(filter).ToListAsync(ct);
        return docs.Select(SocialMapper.Hydrate).ToList();
    }

    public Task DeleteAsync(string id, CancellationToken ct) =>
        Col.DeleteOneAsync(x => x.Id == id, ct);

    public async Task<SocialConnection?> ClaimForHealthCheckAsync(
        IReadOnlyCollection<SocialPlatform> platforms, DateTime checkedBeforeUtc, DateTime nowUtc,
        CancellationToken ct)
    {
        var fb = Builders<SocialConnectionDocument>.Filter;
        var filter = fb.Eq(x => x.Status, (int)ConnectionStatus.Active)
                   & fb.In(x => x.Platform, platforms.Select(p => (int)p))
                   & fb.Or(
                         fb.Eq(x => x.LastHealthCheckAtUtc, null),
                         fb.Lt(x => x.LastHealthCheckAtUtc, checkedBeforeUtc));

        var doc = await Col.FindOneAndUpdateAsync(
            filter,
            Builders<SocialConnectionDocument>.Update.Set(x => x.LastHealthCheckAtUtc, nowUtc),
            new FindOneAndUpdateOptions<SocialConnectionDocument>
            {
                ReturnDocument = ReturnDocument.After,
                // Longest-unchecked first, so a backlog is worked oldest-first.
                Sort = Builders<SocialConnectionDocument>.Sort.Ascending(x => x.LastHealthCheckAtUtc),
            },
            ct);

        return doc is null ? null : SocialMapper.Hydrate(doc);
    }

    public Task RecordHealthAsync(string id, bool healthy, string? error, DateTime nowUtc, CancellationToken ct)
    {
        var fb = Builders<SocialConnectionDocument>.Filter;
        var filter = fb.Eq(x => x.Id, id) & fb.Eq(x => x.Status, (int)ConnectionStatus.Active);

        var update = healthy
            ? Builders<SocialConnectionDocument>.Update
                .Set(x => x.LastVerifiedAtUtc, nowUtc)
                .Set(x => x.UpdatedAtUtc, nowUtc)
            : Builders<SocialConnectionDocument>.Update
                .Set(x => x.Status, (int)ConnectionStatus.NeedsReauth)
                .Set(x => x.LastError, error is { Length: > 500 } ? error[..500] : error)
                .Set(x => x.UpdatedAtUtc, nowUtc);

        return Col.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public Task RenewCredentialAsync(string id, EncryptedSecret credential, DateTime? expiresAtUtc, CancellationToken ct)
    {
        // Targeted, and not on a revoked connection: a rotated token arriving
        // after the pharmacy disconnected must not bring the credential back.
        // Status is left alone — renewing proves nothing about Page roles.
        var fb = Builders<SocialConnectionDocument>.Filter;
        var filter = fb.Eq(x => x.Id, id) & fb.Ne(x => x.Status, (int)ConnectionStatus.Revoked);
        var update = Builders<SocialConnectionDocument>.Update
            .Set(x => x.AccessToken, SocialMapper.ToSecretDocument(credential))
            .Set(x => x.ExpiresAtUtc, expiresAtUtc)
            .Set(x => x.UpdatedAtUtc, DateTime.UtcNow);
        return Col.UpdateOneAsync(filter, update, cancellationToken: ct);
    }

    public async Task<bool> GrantInUseAsync(string fingerprint, string exceptId, CancellationToken ct)
    {
        var fb = Builders<SocialConnectionDocument>.Filter;
        var filter = fb.Eq(x => x.GrantFingerprint, fingerprint)
                   & fb.Ne(x => x.Id, exceptId)
                   & fb.Ne(x => x.Status, (int)ConnectionStatus.Revoked);
        return await Col.Find(filter).Limit(1).AnyAsync(ct);
    }

    public async Task<int> CountActiveAsync(string ownerId, CancellationToken ct)
    {
        var fb = Builders<SocialConnectionDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, ownerId)
                   & fb.Eq(x => x.Status, (int)ConnectionStatus.Active);
        return (int)await Col.CountDocumentsAsync(filter, cancellationToken: ct);
    }
}

public sealed class MongoScheduledPostRepository(MongoContext ctx) : IScheduledPostRepository
{
    private IMongoCollection<ScheduledPostDocument> Col =>
        ctx.Collection<ScheduledPostDocument>("scheduled_posts");

    private static FilterDefinition<ScheduledPostDocument> BuildFilter(ScheduledPostQuery q)
    {
        var fb = Builders<ScheduledPostDocument>.Filter;
        var filter = fb.Eq(x => x.OwnerId, q.OwnerId);
        if (q.Status is { } status) filter &= fb.Eq(x => x.Status, (int)status);
        if (q.AnyOf is { Count: > 0 } anyOf) filter &= fb.In(x => x.Status, anyOf.Select(s => (int)s));
        return filter;
    }

    public async Task<IReadOnlyList<ScheduledPost>> ListAsync(ScheduledPostQuery query, CancellationToken ct)
    {
        var docs = await Col.Find(BuildFilter(query))
            // Newest first by intent-time. CreatedAtUtc is the tiebreaker so
            // paging is stable — several posts scheduled for the same minute is
            // the normal case when someone queues a week's worth at once, and
            // without a total order they shuffle between pages.
            .SortByDescending(x => x.ScheduledForUtc).ThenByDescending(x => x.CreatedAtUtc)
            .Skip(query.Skip)
            .Limit(query.Take)
            .ToListAsync(ct);

        return docs.Select(SocialMapper.Hydrate).ToList();
    }

    public Task<long> CountAsync(ScheduledPostQuery query, CancellationToken ct) =>
        Col.CountDocumentsAsync(BuildFilter(query), cancellationToken: ct);

    public async Task<ScheduledPost?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : SocialMapper.Hydrate(doc);
    }

    public Task AddAsync(ScheduledPost post, CancellationToken ct) =>
        Col.InsertOneAsync(SocialMapper.ToDocument(post), cancellationToken: ct);

    public Task UpdateAsync(ScheduledPost post, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == post.Id,
            SocialMapper.ToDocument(post),
            new ReplaceOptions { IsUpsert = true },
            ct);

    public async Task<bool> TryUpdateClaimedAsync(ScheduledPost post, string leaseHolder, CancellationToken ct)
    {
        var fb = Builders<ScheduledPostDocument>.Filter;
        var filter = fb.Eq(x => x.Id, post.Id) & fb.Eq(x => x.LeaseHolder, leaseHolder);

        // No upsert: a post that has gone, or is no longer ours, must not be
        // recreated by a worker that has lost track of it.
        var result = await Col.ReplaceOneAsync(filter, SocialMapper.ToDocument(post), new ReplaceOptions(), ct);
        return result.MatchedCount == 1;
    }

    public async Task<bool> TryUpdateIfStatusAsync(
        ScheduledPost post, PublishStatus expectedStatus, CancellationToken ct)
    {
        var fb = Builders<ScheduledPostDocument>.Filter;
        var filter = fb.Eq(x => x.Id, post.Id) & fb.Eq(x => x.Status, (int)expectedStatus);

        var result = await Col.ReplaceOneAsync(filter, SocialMapper.ToDocument(post), new ReplaceOptions(), ct);
        return result.MatchedCount == 1;
    }

    /// <summary>
    /// Atomically claims one due post for this worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This single findAndModify is the concurrency boundary for the whole
    /// publishing feature.</b> Mongo guarantees findAndModify is atomic on one
    /// document: of two instances issuing this at the same instant, exactly one
    /// matches and updates, and the other's filter no longer matches so it gets
    /// nothing back. Reading a candidate and then writing a claim would not be
    /// equivalent — both would read, both would write, and the pharmacy's
    /// followers would see the post twice. There is no retraction for that.
    /// </para>
    /// <para>Two kinds of post are claimable:</para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>Waiting</b> — Scheduled, due (its own time and any retry backoff
    ///     have passed) and not leased.
    ///   </item>
    ///   <item>
    ///     <b>Abandoned</b> — Publishing with a lease that has lapsed. The
    ///     worker that held it crashed or was stopped. A live worker renews its
    ///     lease before every account, so a lapsed lease is not one in use.
    ///   </item>
    /// </list>
    /// <para>
    /// An abandoned post may have a target that was mid-call when the worker
    /// died; <see cref="ScheduledPost.ResolveInterruptedAttempts"/> marks that
    /// one uncertain rather than sending it again.
    /// </para>
    /// </remarks>
    public async Task<ScheduledPost?> ClaimNextDueAsync(
        string leaseHolder, TimeSpan leaseDuration, DateTime nowUtc, CancellationToken ct)
    {
        var fb = Builders<ScheduledPostDocument>.Filter;

        var due = fb.Or(
            fb.Eq(x => x.ScheduledForUtc, null),
            fb.Lte(x => x.ScheduledForUtc, nowUtc));

        var backoffOver = fb.Or(
            fb.Eq(x => x.NextAttemptAtUtc, null),
            fb.Lte(x => x.NextAttemptAtUtc, nowUtc));

        var unleased = fb.Or(
            fb.Eq(x => x.LeaseHolder, null),
            fb.Lt(x => x.LeaseExpiresAtUtc, nowUtc));

        var waiting = fb.Eq(x => x.Status, (int)PublishStatus.Scheduled) & due & backoffOver & unleased;

        // "No holder at all" covers posts stranded by the previous worker
        // version, which released the lease on an exception but left the
        // status at Publishing.
        var abandoned = fb.Eq(x => x.Status, (int)PublishStatus.Publishing)
                      & fb.Or(
                            fb.Lt(x => x.LeaseExpiresAtUtc, nowUtc),
                            fb.Eq(x => x.LeaseHolder, null));

        var filter = fb.Or(waiting, abandoned);

        var update = Builders<ScheduledPostDocument>.Update
            .Set(x => x.Status, (int)PublishStatus.Publishing)
            .Set(x => x.LeaseHolder, leaseHolder)
            .Set(x => x.LeaseExpiresAtUtc, nowUtc.Add(leaseDuration))
            .Set(x => x.UpdatedAtUtc, nowUtc);

        var options = new FindOneAndUpdateOptions<ScheduledPostDocument>
        {
            // The claimed document, so the caller can publish it without a
            // second read that might see someone else's later write.
            ReturnDocument = ReturnDocument.After,
            // Oldest due first. Otherwise a backlog is served in arbitrary
            // order and the post someone scheduled for 9am can sit behind one
            // queued at noon.
            Sort = Builders<ScheduledPostDocument>.Sort
                .Ascending(x => x.ScheduledForUtc)
                .Ascending(x => x.CreatedAtUtc),
        };

        var doc = await Col.FindOneAndUpdateAsync(filter, update, options, ct);
        return doc is null ? null : SocialMapper.Hydrate(doc);
    }

    public async Task<int> CountPendingForConnectionAsync(string connectionId, CancellationToken ct)
    {
        var fb = Builders<ScheduledPostDocument>.Filter;
        var filter = fb.ElemMatch(x => x.Targets, t => t.ConnectionId == connectionId)
                   & fb.In(x => x.Status, new[]
                   {
                       (int)PublishStatus.Scheduled,
                       (int)PublishStatus.Publishing,
                   });

        return (int)await Col.CountDocumentsAsync(filter, cancellationToken: ct);
    }
}

public sealed class MongoGoogleAdsCampaignRepository(MongoContext ctx) : IGoogleAdsCampaignRepository
{
    private IMongoCollection<GoogleAdsCampaignDocument> Col =>
        ctx.Collection<GoogleAdsCampaignDocument>("google_ads_campaigns");

    public async Task<IReadOnlyList<GoogleAdsCampaign>> ListByOwnerAsync(string ownerId, CancellationToken ct)
    {
        var docs = await Col.Find(x => x.OwnerId == ownerId)
            .SortByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return docs.Select(SocialMapper.Hydrate).ToList();
    }

    public async Task<GoogleAdsCampaign?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await Col.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : SocialMapper.Hydrate(doc);
    }

    public Task AddAsync(GoogleAdsCampaign campaign, CancellationToken ct) =>
        Col.InsertOneAsync(SocialMapper.ToDocument(campaign), cancellationToken: ct);

    public Task DeleteAsync(string id, CancellationToken ct) =>
        Col.DeleteOneAsync(x => x.Id == id, ct);

    public Task UpdateAsync(GoogleAdsCampaign campaign, CancellationToken ct) =>
        Col.ReplaceOneAsync(
            x => x.Id == campaign.Id,
            SocialMapper.ToDocument(campaign),
            new ReplaceOptions { IsUpsert = true },
            ct);
}
