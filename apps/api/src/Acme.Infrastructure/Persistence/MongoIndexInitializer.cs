using Acme.Application.Common;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoIndexInitializer(MongoContext ctx) : IStartupTask
{
    private const int IndexNotFound = 27;

    /// <summary>
    /// Removes an index so it can be recreated with different options. Mongo
    /// rejects CreateIndex when the name matches an existing index with a
    /// different definition, so redefining one requires an explicit drop.
    /// </summary>
    private static async Task DropIndexIfExistsAsync<T>(
        IMongoCollection<T> col, string name, CancellationToken ct)
    {
        try
        {
            await col.Indexes.DropOneAsync(name, ct);
        }
        catch (MongoCommandException ex) when (ex.Code == IndexNotFound)
        {
            // Fresh database, or already dropped by a previous run.
        }
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        await CreateUserIndexes(ct);
        await CreateRefreshTokenIndexes(ct);
        await CreateBrandKitIndexes(ct);
        await CreateTemplateIndexes(ct);
        await CreatePosterIndexes(ct);
        await CreateCampaignIndexes(ct);
        await CreatePatientIndexes(ct);
        await CreatePatientGroupIndexes(ct);
        await CreateVideoIndexes(ct);
        await CreateProcessedWebhookIndexes(ct);
        await CreateAiGenerationIndexes(ct);
        await CreateAdminAuditIndexes(ct);
        await CreateInvitationIndexes(ct);
        await CreateAuthTokenIndexes(ct);
        await CreateMembershipIndexes(ct);
        await CreateAccessRequestIndexes(ct);
        await CreateSocialConnectionIndexes(ct);
        await CreateScheduledPostIndexes(ct);
        await CreateRecurringSeriesIndexes(ct);
        await CreateOAuthSessionIndexes(ct);
        await CreateGoogleAdsCampaignIndexes(ct);
        await CreateDisplayIndexes(ct);
        await CreateEmailSuppressionIndexes(ct);
        await CreateAuditEventIndexes(Acme.Application.Audit.AuditStream.Audit, ct);
        await CreateAuditEventIndexes(Acme.Application.Audit.AuditStream.Activity, ct);
    }

    /// <summary>
    /// The same four lookups on both streams, and a TTL on activity only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each index answers one filter the admin view offers, newest first:
    /// by actor, by pharmacy, by entity, and by kind (staff / pharmacy /
    /// system). Without them every filtered page is a collection scan of a log
    /// that only grows.
    /// </para>
    /// <para>
    /// <b>TTL on activity_events only.</b> "Opened a poster" is worth 90 days;
    /// who deleted a user is not something to expire, so audit_events keeps a
    /// plain time index and nothing removes rows from it.
    /// </para>
    /// </remarks>
    private async Task CreateAuditEventIndexes(Acme.Application.Audit.AuditStream stream, CancellationToken ct)
    {
        var col = ctx.Collection<Acme.Infrastructure.Audit.AuditEventDocument>(
            stream == Acme.Application.Audit.AuditStream.Audit
                ? Acme.Infrastructure.Audit.MongoAuditEventStore.AuditCollection
                : Acme.Infrastructure.Audit.MongoAuditEventStore.ActivityCollection);
        var k = Builders<Acme.Infrastructure.Audit.AuditEventDocument>.IndexKeys;

        var models = new List<CreateIndexModel<Acme.Infrastructure.Audit.AuditEventDocument>>
        {
            new(k.Ascending(x => x.ActorId).Descending(x => x.AtUtc),
                new CreateIndexOptions { Name = "actor_time" }),
            new(k.Ascending(x => x.PharmacyId).Descending(x => x.AtUtc),
                new CreateIndexOptions { Name = "pharmacy_time" }),
            new(k.Ascending(x => x.EntityType).Ascending(x => x.EntityId).Descending(x => x.AtUtc),
                new CreateIndexOptions { Name = "entity_time" }),
            new(k.Ascending(x => x.ActorKind).Descending(x => x.AtUtc),
                new CreateIndexOptions { Name = "kind_time" }),
        };

        if (stream == Acme.Application.Audit.AuditStream.Activity)
        {
            // Doubles as the newest-first index for unfiltered pages.
            models.Add(new(k.Ascending(x => x.AtUtc), new CreateIndexOptions
            {
                Name = "time_ttl",
                ExpireAfter = TimeSpan.FromDays(90),
            }));
        }
        else
        {
            models.Add(new(k.Descending(x => x.AtUtc), new CreateIndexOptions { Name = "time_desc" }));
        }

        await col.Indexes.CreateManyAsync(models, ct);
    }

    private async Task CreateAccessRequestIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PharmacyAccessRequestDocument>("pharmacy_access_requests");

        // "Does this person already have an open request for this pharmacy" —
        // checked on every request, and what stops a double click filling the
        // review queue with the same question twice.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PharmacyAccessRequestDocument>(
                Builders<PharmacyAccessRequestDocument>.IndexKeys
                    .Ascending(x => x.UserId)
                    .Ascending(x => x.PharmacyId)
                    .Ascending(x => x.Status),
                new CreateIndexOptions { Name = "user_pharmacy_status" }),
            cancellationToken: ct);

        // The review queue: pending first, newest within that.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PharmacyAccessRequestDocument>(
                Builders<PharmacyAccessRequestDocument>.IndexKeys
                    .Ascending(x => x.Status)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "status_created" }),
            cancellationToken: ct);
    }

    private async Task CreateMembershipIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PharmacyMembershipDocument>("pharmacy_memberships");

        // Unique on the pair. This is the authorisation record, and a duplicate
        // would mean one person holding two different roles over the same
        // pharmacy with no rule about which wins. It also makes the backfill
        // safe to run repeatedly: re-inserting an existing pair bounces.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PharmacyMembershipDocument>(
                Builders<PharmacyMembershipDocument>.IndexKeys
                    .Ascending(x => x.UserId)
                    .Ascending(x => x.PharmacyId),
                new CreateIndexOptions { Name = "user_pharmacy_unique", Unique = true }),
            cancellationToken: ct);

        // "Everyone who may act as this pharmacy" — the membership list a head
        // office reads, and the reverse of the lookup above.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PharmacyMembershipDocument>(
                Builders<PharmacyMembershipDocument>.IndexKeys.Ascending(x => x.PharmacyId),
                new CreateIndexOptions { Name = "pharmacy" }),
            cancellationToken: ct);
    }

    private async Task CreateEmailSuppressionIndexes(CancellationToken ct)
    {
        // The id is "{ownerId}|{email}", so lookups at send time are point reads
        // already. This index serves a pharmacy listing its own suppressions.
        var col = ctx.Collection<EmailSuppressionDocument>(MongoEmailSuppressionRepository.CollectionName);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<EmailSuppressionDocument>(
                Builders<EmailSuppressionDocument>.IndexKeys.Ascending(x => x.OwnerId),
                new CreateIndexOptions { Name = "owner" }),
            cancellationToken: ct);
    }

    private async Task CreateAuthTokenIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<AuthTokenDocument>("auth_tokens");

        // Unique on the token hash, for the same reasons as invitations: every
        // reset and verification attempt is an unauthenticated lookup by this
        // field, so it must be an indexed point read rather than a collection
        // scan anyone on the internet can trigger. Unique because a collision
        // would mean the CSPRNG had repeated itself and should fail loudly.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<AuthTokenDocument>(
                Builders<AuthTokenDocument>.IndexKeys.Ascending(x => x.TokenHash),
                new CreateIndexOptions { Name = "token_hash_unique", Unique = true }),
            cancellationToken: ct);

        // Supports superseding: "every outstanding token of this purpose for
        // this account", which runs on every reset request.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<AuthTokenDocument>(
                Builders<AuthTokenDocument>.IndexKeys
                    .Ascending(x => x.UserId)
                    .Ascending(x => x.Purpose)
                    .Ascending(x => x.ExpiresAtUtc),
                new CreateIndexOptions { Name = "user_purpose_expiry" }),
            cancellationToken: ct);

        // Spent tokens have no value and should not accumulate. A TTL index on
        // expiry lets Mongo remove them; 30 days after expiry rather than at
        // expiry, so "that link was already used" stays answerable for support
        // for a reasonable window.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<AuthTokenDocument>(
                Builders<AuthTokenDocument>.IndexKeys.Ascending(x => x.ExpiresAtUtc),
                new CreateIndexOptions
                {
                    Name = "expiry_ttl",
                    ExpireAfter = TimeSpan.FromDays(30),
                }),
            cancellationToken: ct);
    }

    private async Task CreateInvitationIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PharmacyInvitationDocument>("pharmacy_invitations");

        // Unique on the token hash. Every activation attempt is a lookup by
        // this field, and it runs unauthenticated — so it must be an indexed
        // point read rather than a scan that anyone on the internet can
        // trigger. Unique because two invitations sharing a hash would mean the
        // random token generator had collided, which should fail loudly.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PharmacyInvitationDocument>(
                Builders<PharmacyInvitationDocument>.IndexKeys.Ascending(x => x.TokenHash),
                new CreateIndexOptions { Name = "token_hash_unique", Unique = true }),
            cancellationToken: ct);

        // Supports "every invitation for this account, newest first", which the
        // admin list and every resend both walk.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PharmacyInvitationDocument>(
                Builders<PharmacyInvitationDocument>.IndexKeys
                    .Ascending(x => x.UserId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "user_created" }),
            cancellationToken: ct);
    }

    private async Task CreateSocialConnectionIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<SocialConnectionDocument>("social_connections");

        // Unique on the account itself, not on (owner, platform): a pharmacy
        // group legitimately holds several Facebook Pages. This is also what
        // makes re-authentication update in place rather than accumulating a
        // duplicate connection every time someone reconnects.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<SocialConnectionDocument>(
                Builders<SocialConnectionDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.Platform)
                    .Ascending(x => x.ExternalAccountId),
                new CreateIndexOptions { Name = "owner_platform_account_unique", Unique = true }),
            cancellationToken: ct);

        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<SocialConnectionDocument>(
                Builders<SocialConnectionDocument>.IndexKeys.Ascending(x => x.OwnerId),
                new CreateIndexOptions { Name = "owner" }),
            cancellationToken: ct);

        // Meta's deauthorize and data-deletion callbacks look connections up
        // by the Facebook user who granted them. Sparse: older connections
        // have no value until they are reconnected.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<SocialConnectionDocument>(
                Builders<SocialConnectionDocument>.IndexKeys.Ascending(x => x.AuthorizedByUserId),
                new CreateIndexOptions { Name = "authorized_by", Sparse = true }),
            cancellationToken: ct);

        // Disconnect asks "does anyone else still use this Google grant?"
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<SocialConnectionDocument>(
                Builders<SocialConnectionDocument>.IndexKeys.Ascending(x => x.GrantFingerprint),
                new CreateIndexOptions { Name = "grant_fingerprint", Sparse = true }),
            cancellationToken: ct);
    }

    private async Task CreateScheduledPostIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<ScheduledPostDocument>("scheduled_posts");

        // The claim index. Every poll issues findAndModify against exactly
        // these three fields in this order, once per instance per interval,
        // for ever. Without it that is a collection scan on a table which only
        // grows, and the scan holds a write lock while it runs.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<ScheduledPostDocument>(
                Builders<ScheduledPostDocument>.IndexKeys
                    .Ascending(x => x.Status)
                    .Ascending(x => x.ScheduledForUtc)
                    .Ascending(x => x.LeaseExpiresAtUtc),
                new CreateIndexOptions { Name = "due_claim" }),
            cancellationToken: ct);

        // The pharmacy's own list, newest first.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<ScheduledPostDocument>(
                Builders<ScheduledPostDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.ScheduledForUtc),
                new CreateIndexOptions { Name = "owner_schedule" }),
            cancellationToken: ct);

        // Supports "does anything still target this connection?" when a
        // pharmacy disconnects an account.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<ScheduledPostDocument>(
                Builders<ScheduledPostDocument>.IndexKeys.Ascending("targets.connectionId"),
                new CreateIndexOptions { Name = "target_connection" }),
            cancellationToken: ct);

        // A series' own history, and its unstarted occurrences on pause and
        // edit. Sparse: one-off posts have no series id.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<ScheduledPostDocument>(
                Builders<ScheduledPostDocument>.IndexKeys
                    .Ascending(x => x.SeriesId)
                    .Descending(x => x.OccurrenceAtUtc),
                new CreateIndexOptions { Name = "series_occurrences", Sparse = true }),
            cancellationToken: ct);

        // The series notifier asks every 20 seconds, on every instance, for
        // occurrences finished in the last day. Without this it scans every
        // post ever made; with it, only yesterday's.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<ScheduledPostDocument>(
                Builders<ScheduledPostDocument>.IndexKeys.Ascending(x => x.CompletedAtUtc),
                new CreateIndexOptions { Name = "completed" }),
            cancellationToken: ct);
    }

    private async Task CreateRecurringSeriesIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<RecurringSeriesDocument>(MongoRecurringSeriesRepository.CollectionName);

        // What the scheduler polls: active series that have fallen due.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<RecurringSeriesDocument>(
                Builders<RecurringSeriesDocument>.IndexKeys
                    .Ascending(x => x.Status)
                    .Ascending(x => x.NextOccurrenceUtc),
                new CreateIndexOptions { Name = "due" }),
            cancellationToken: ct);

        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<RecurringSeriesDocument>(
                Builders<RecurringSeriesDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.Status)
                    .Ascending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "owner_status" }),
            cancellationToken: ct);
    }

    private async Task CreateOAuthSessionIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<Acme.Infrastructure.Social.OAuthSessionDocument>(
            Acme.Infrastructure.Social.MongoOAuthSessionStore.CollectionName);

        // Deletes each flow as soon as it expires (within the TTL monitor's
        // minute). These documents can hold encrypted platform tokens for
        // accounts nobody chose to connect; they should not outlive the
        // ten minutes the pharmacy had to choose.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<Acme.Infrastructure.Social.OAuthSessionDocument>(
                Builders<Acme.Infrastructure.Social.OAuthSessionDocument>.IndexKeys.Ascending(x => x.ExpiresAtUtc),
                new CreateIndexOptions { Name = "expiry_ttl", ExpireAfter = TimeSpan.Zero }),
            cancellationToken: ct);
    }

    private async Task CreateGoogleAdsCampaignIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<GoogleAdsCampaignDocument>("google_ads_campaigns");

        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<GoogleAdsCampaignDocument>(
                Builders<GoogleAdsCampaignDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "owner_created" }),
            cancellationToken: ct);
    }

    private async Task CreateAdminAuditIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<AdminAuditDocument>("admin_audit");

        // Every audit view sorts newest-first; without this the log
        // collection-scans and sorts in memory, which degrades badly as the
        // trail grows. Deliberately no TTL — an audit log you can outlive
        // isn't one, and this collection stays small relative to its value.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<AdminAuditDocument>(
                Builders<AdminAuditDocument>.IndexKeys.Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_created_desc" }),
            cancellationToken: ct);

        // An Admin's own feed is filtered by actor, then sorted.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<AdminAuditDocument>(
                Builders<AdminAuditDocument>.IndexKeys
                    .Ascending(x => x.ActorId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_actor_created" }),
            cancellationToken: ct);
    }

    private Task CreateProcessedWebhookIndexes(CancellationToken ct)
    {
        // The ledger only needs to outlive Stripe's retry window (~3 days).
        // A 30-day TTL keeps it bounded without any cleanup job.
        var col = ctx.Collection<ProcessedWebhookDocument>("processed_webhooks");
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<ProcessedWebhookDocument>(
                Builders<ProcessedWebhookDocument>.IndexKeys.Ascending(x => x.ProcessedAtUtc),
                new CreateIndexOptions
                {
                    Name = "ttl_processedAt",
                    ExpireAfter = TimeSpan.FromDays(30),
                }),
            cancellationToken: ct);
    }

    private async Task CreateVideoIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<VideoDocument>("videos");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<VideoDocument>(
                Builders<VideoDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_owner_created" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<VideoDocument>(
                Builders<VideoDocument>.IndexKeys
                    .Ascending(x => x.Status)
                    .Ascending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_status_created" }),
            cancellationToken: ct);

        // What the render worker claims on, now that the claim also asks
        // whether a Rendering row's lease has lapsed. `by_status_created` above
        // still serves the plain status counts; this one carries the lease
        // predicate as well, so neither branch of the claim's $or falls back to
        // a scan as the videos collection grows.
        //
        // Deliberately the same shape as the campaign queue's `dispatch_queue`:
        // status first because it is the selective term, then the lease, then
        // creation date to order what is left.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<VideoDocument>(
                Builders<VideoDocument>.IndexKeys
                    .Ascending(x => x.Status)
                    .Ascending(x => x.LeaseExpiresAtUtc)
                    .Ascending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "render_queue" }),
            cancellationToken: ct);
    }

    private async Task CreatePatientIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PatientDocument>("patients");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientDocument>(
                Builders<PatientDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.FullName),
                new CreateIndexOptions { Name = "by_owner_name" }),
            cancellationToken: ct);
        // The Patients page: date added, newest or oldest first, with the id
        // tiebreaker — exactly the sort ListWindowMongo builds, so a big list
        // pages from the index instead of sorting every patient in memory.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientDocument>(
                Builders<PatientDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.CreatedAtUtc)
                    .Descending(x => x.Id),
                new CreateIndexOptions { Name = "by_owner_created" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientDocument>(
                Builders<PatientDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.GroupIds),
                new CreateIndexOptions { Name = "by_owner_group" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientDocument>(
                Builders<PatientDocument>.IndexKeys
                    .Text(x => x.FullName)
                    .Text(x => x.Email)
                    .Text(x => x.Phone)
                    .Text(x => x.Notes),
                new CreateIndexOptions { Name = "text_search" }),
            cancellationToken: ct);
    }

    private Task CreatePatientGroupIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PatientGroupDocument>("patient_groups");
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<PatientGroupDocument>(
                Builders<PatientGroupDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Ascending(x => x.Name),
                new CreateIndexOptions { Name = "by_owner_name" }),
            cancellationToken: ct);
    }

    private async Task CreateCampaignIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<CampaignDocument>("campaigns");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<CampaignDocument>(
                Builders<CampaignDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_owner_created" }),
            cancellationToken: ct);

        // The dispatch worker's claim, which runs every few seconds for the life
        // of the process. Without this it is a collection scan per poll, growing
        // with every campaign ever sent — the cost of the feature would rise
        // forever while the work stayed the same.
        //
        // Status first: it is the selective term, and all but a handful of rows
        // are Sent. The lease and creation date order what is left.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<CampaignDocument>(
                Builders<CampaignDocument>.IndexKeys
                    .Ascending(x => x.Status)
                    .Ascending(x => x.LeaseExpiresAtUtc)
                    .Ascending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "dispatch_queue" }),
            cancellationToken: ct);
    }

    private async Task CreateUserIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<UserDocument>("users");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDocument>(
                Builders<UserDocument>.IndexKeys.Ascending(u => u.Email),
                new CreateIndexOptions { Unique = true, Name = "uniq_email" }),
            cancellationToken: ct);

        // Every Stripe webhook resolves the user by customer id, so this must
        // be unique — two users sharing a Stripe customer would cross-grant
        // subscriptions.
        //
        // It has to be a PARTIAL index, not a sparse one. Sparse only skips
        // documents where the field is *absent*; ToDocument always writes the
        // billing sub-document, so every non-paying user carries an explicit
        // `stripeCustomerId: null` and they all collided on it. Filtering to
        // documents where the value is actually a string indexes only real
        // customers.
        await DropIndexIfExistsAsync(col, "uniq_stripeCustomer", ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDocument>(
                Builders<UserDocument>.IndexKeys.Ascending("billing.stripeCustomerId"),
                new CreateIndexOptions<UserDocument>
                {
                    Name = "uniq_stripeCustomer",
                    Unique = true,
                    PartialFilterExpression = Builders<UserDocument>.Filter.Type(
                        "billing.stripeCustomerId", BsonType.String),
                }),
            cancellationToken: ct);

        // Admin user list: filtered by role and status, sorted newest first.
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDocument>(
                Builders<UserDocument>.IndexKeys
                    .Ascending(x => x.Role)
                    .Ascending(x => x.IsActive)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_role_active_created" }),
            cancellationToken: ct);

        // "Users managed by this admin" — the hot path for every Admin-scoped
        // query, and the aggregation behind the per-admin counts.
        //
        // Partial for the same reason as above: unassigned users carry an
        // explicit null, which a sparse index would happily index anyway.
        // Filtering to string values keeps this to the assigned users the
        // queries actually look for.
        await DropIndexIfExistsAsync(col, "by_managing_admin", ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDocument>(
                Builders<UserDocument>.IndexKeys.Ascending(x => x.ManagedByAdminId),
                new CreateIndexOptions<UserDocument>
                {
                    Name = "by_managing_admin",
                    PartialFilterExpression = Builders<UserDocument>.Filter.Type(
                        "managedByAdminId", BsonType.String),
                }),
            cancellationToken: ct);

        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDocument>(
                Builders<UserDocument>.IndexKeys.Ascending(x => x.Tier),
                new CreateIndexOptions { Name = "by_tier" }),
            cancellationToken: ct);

        // "Branches under this pharmacy" — read on every load of the
        // sub-pharmacies page and, more importantly, on every manage-as request,
        // where it is the check that decides whether one account may act as
        // another. Partial for the same reason as the admin index: standalone
        // pharmacies carry an explicit null and would otherwise dominate it.
        await DropIndexIfExistsAsync(col, "by_parent_pharmacy", ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<UserDocument>(
                Builders<UserDocument>.IndexKeys
                    .Ascending(x => x.ParentPharmacyId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions<UserDocument>
                {
                    Name = "by_parent_pharmacy",
                    PartialFilterExpression = Builders<UserDocument>.Filter.Type(
                        "parentPharmacyId", BsonType.String),
                }),
            cancellationToken: ct);
    }

    private async Task CreateRefreshTokenIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<RefreshTokenDocument>("refresh_tokens");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<RefreshTokenDocument>(
                Builders<RefreshTokenDocument>.IndexKeys.Ascending(t => t.TokenHash),
                new CreateIndexOptions { Unique = true, Name = "uniq_tokenHash" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<RefreshTokenDocument>(
                Builders<RefreshTokenDocument>.IndexKeys.Ascending(t => t.UserId),
                new CreateIndexOptions { Name = "by_userId" }),
            cancellationToken: ct);
    }

    /// <summary>
    /// Every allowance question is "how many for this owner since a date", so
    /// the index is owner first, time second. Without it the count is a
    /// collection scan on every generate — on the request that is already the
    /// slowest thing a pharmacist does.
    /// </summary>
    private Task CreateAiGenerationIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<AiGenerationDocument>(
            MongoAiGenerationRepository.CollectionName);
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<AiGenerationDocument>(
                Builders<AiGenerationDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.CreatedAtUtc),
                new CreateIndexOptions { Name = "by_owner_created" }),
            cancellationToken: ct);
    }

    private Task CreateBrandKitIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<BrandKitDocument>(MongoBrandKitRepository.CollectionName);
        return col.Indexes.CreateOneAsync(
            new CreateIndexModel<BrandKitDocument>(
                Builders<BrandKitDocument>.IndexKeys.Ascending(x => x.OwnerId),
                new CreateIndexOptions { Unique = true, Name = "uniq_owner" }),
            cancellationToken: ct);
    }

    private async Task CreateTemplateIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<TemplateDocument>("templates");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<TemplateDocument>(
                Builders<TemplateDocument>.IndexKeys.Ascending(x => x.Category),
                new CreateIndexOptions { Name = "by_category" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<TemplateDocument>(
                Builders<TemplateDocument>.IndexKeys
                    .Text(x => x.Name)
                    .Text(x => x.Description)
                    .Text(x => x.Tags),
                new CreateIndexOptions { Name = "text_search" }),
            cancellationToken: ct);
    }

    private async Task CreatePosterIndexes(CancellationToken ct)
    {
        var col = ctx.Collection<PosterDocument>("posters");
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PosterDocument>(
                Builders<PosterDocument>.IndexKeys
                    .Ascending(x => x.OwnerId)
                    .Descending(x => x.UpdatedAtUtc),
                new CreateIndexOptions { Name = "by_owner_updated" }),
            cancellationToken: ct);
        await col.Indexes.CreateOneAsync(
            new CreateIndexModel<PosterDocument>(
                Builders<PosterDocument>.IndexKeys.Text(x => x.Name),
                new CreateIndexOptions { Name = "text_search" }),
            cancellationToken: ct);
    }

    private async Task CreateDisplayIndexes(CancellationToken ct)
    {
        var screens = ctx.Collection<DisplayScreenDocument>(
            MongoDisplayScreenRepository.CollectionName);

        await screens.Indexes.CreateOneAsync(
            new CreateIndexModel<DisplayScreenDocument>(
                Builders<DisplayScreenDocument>.IndexKeys.Ascending(x => x.OwnerId),
                new CreateIndexOptions { Name = "by_owner" }),
            cancellationToken: ct);

        // The kiosk's hot path. Every screen in the estate hits this every
        // thirty seconds forever, so it is the one index here that has to
        // exist — a collection scan per poll would be the whole cost of the
        // feature.
        await screens.Indexes.CreateOneAsync(
            new CreateIndexModel<DisplayScreenDocument>(
                Builders<DisplayScreenDocument>.IndexKeys.Ascending("credentials.tokenHash"),
                new CreateIndexOptions { Name = "by_credential_hash" }),
            cancellationToken: ct);

        // A live pairing code must be unique, or two installers could type the
        // same six characters and one would be paired to the wrong shop.
        //
        // PARTIAL, not sparse — the same distinction as `uniq_stripeCustomer`
        // above, and this index got it wrong in exactly the same way. Sparse
        // skips documents where the field is *absent*, but `pairingCode` has no
        // [BsonIgnoreIfNull], so ToDocument always writes it and a screen whose
        // code has been cleared carries an explicit `pairingCode: null`.
        //
        // The effect was not subtle and was not per-pharmacy. Pairing clears
        // the code, so the *second screen ever paired in the database* wrote a
        // second null, collided, and got a 500 — from an endpoint whose whole
        // job is the one action an installer performs. One screen worked, which
        // is why it survived a demo.
        //
        // Filtering to documents where the value is actually a string indexes
        // only codes that exist.
        await DropIndexIfExistsAsync(screens, "uniq_pairing_code", ct);
        await screens.Indexes.CreateOneAsync(
            new CreateIndexModel<DisplayScreenDocument>(
                Builders<DisplayScreenDocument>.IndexKeys.Ascending(x => x.PairingCode),
                new CreateIndexOptions<DisplayScreenDocument>
                {
                    Name = "uniq_pairing_code",
                    Unique = true,
                    // The element name, matching the three partial indexes
                    // above. `pairingCode` is the [BsonElement] on
                    // DisplayScreenDocument.PairingCode.
                    PartialFilterExpression = Builders<DisplayScreenDocument>.Filter.Type(
                        "pairingCode", BsonType.String),
                }),
            cancellationToken: ct);

        var playlists = ctx.Collection<PlaylistDocument>(
            MongoPlaylistRepository.CollectionName);

        await playlists.Indexes.CreateOneAsync(
            new CreateIndexModel<PlaylistDocument>(
                Builders<PlaylistDocument>.IndexKeys.Ascending(x => x.OwnerId),
                new CreateIndexOptions { Name = "by_owner" }),
            cancellationToken: ct);
    }
}
