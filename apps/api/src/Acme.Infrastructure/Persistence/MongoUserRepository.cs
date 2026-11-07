using Acme.Application.Auth;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Acme.Infrastructure.Persistence;

public sealed class MongoUserRepository : IUserRepository
{
    private const string CollectionName = "users";
    private readonly IMongoCollection<UserDocument> _collection;

    public MongoUserRepository(MongoContext ctx)
    {
        _collection = ctx.Collection<UserDocument>(CollectionName);
    }

    public async Task<User?> FindByEmailAsync(Email email, CancellationToken ct)
    {
        var doc = await _collection
            .Find(x => x.Email == email.Value)
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : ToDomain(doc);
    }

    public async Task<User?> FindByIdAsync(string id, CancellationToken ct)
    {
        var doc = await _collection.Find(x => x.Id == id).FirstOrDefaultAsync(ct);
        return doc is null ? null : ToDomain(doc);
    }

    public async Task<User?> FindByStripeCustomerIdAsync(string stripeCustomerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stripeCustomerId)) return null;

        var doc = await _collection
            .Find(x => x.Billing!.StripeCustomerId == stripeCustomerId)
            .FirstOrDefaultAsync(ct);
        return doc is null ? null : ToDomain(doc);
    }

    public Task AddAsync(User user, CancellationToken ct) =>
        _collection.InsertOneAsync(ToDocument(user), cancellationToken: ct);

    public Task UpdateAsync(User user, CancellationToken ct) =>
        _collection.ReplaceOneAsync(
            x => x.Id == user.Id,
            ToDocument(user),
            options: new ReplaceOptions { IsUpsert = false },
            cancellationToken: ct);

    /* ---------------- Admin queries ---------------- */

    public async Task<UserPage> SearchAsync(UserQuery query, CancellationToken ct)
    {
        var filter = BuildFilter(query);

        // Count and page in parallel — the admin table needs both and they're
        // independent round trips.
        var totalTask = _collection.CountDocumentsAsync(filter, cancellationToken: ct);
        var itemsTask = _collection
            .Find(filter)
            .Sort(BuildSort(query.Sort))
            .Skip(Math.Max(0, query.Skip))
            .Limit(Math.Clamp(query.Take, 1, 200))
            .ToListAsync(ct);

        await Task.WhenAll(totalTask, itemsTask);

        return new UserPage(
            itemsTask.Result.Select(ToDomain).ToList(),
            totalTask.Result);
    }

    /// <summary>
    /// Maps a sort option to a Mongo sort definition.
    /// </summary>
    /// <remarks>
    /// Every branch adds CreatedAtUtc as a tiebreaker. Without a total ordering
    /// Mongo may return equal-keyed documents in a different order between
    /// calls, which makes a paged table drop or repeat rows as the user pages —
    /// a bug that only shows up once several accounts share a display name or
    /// tier, so it would not surface in testing.
    /// </remarks>
    private static SortDefinition<UserDocument> BuildSort(UserSort sort)
    {
        var b = Builders<UserDocument>.Sort;
        return sort switch
        {
            UserSort.OldestFirst => b.Ascending(x => x.CreatedAtUtc),
            UserSort.NameAsc => b.Ascending(x => x.DisplayName).Descending(x => x.CreatedAtUtc),
            UserSort.NameDesc => b.Descending(x => x.DisplayName).Descending(x => x.CreatedAtUtc),
            UserSort.EmailAsc => b.Ascending(x => x.Email).Descending(x => x.CreatedAtUtc),
            UserSort.EmailDesc => b.Descending(x => x.Email).Descending(x => x.CreatedAtUtc),
            // Descending puts the most recent first; documents with no
            // LastLoginAtUtc sort last, which is what "least recently active"
            // should mean for an account that has never signed in.
            UserSort.LastLoginDesc => b.Descending(x => x.LastLoginAtUtc).Descending(x => x.CreatedAtUtc),
            UserSort.TierDesc => b.Descending(x => x.Tier).Descending(x => x.CreatedAtUtc),
            _ => b.Descending(x => x.CreatedAtUtc),
        };
    }

    public async Task<IReadOnlyList<User>> ListManagedByAsync(string adminId, CancellationToken ct)
    {
        var docs = await _collection
            .Find(x => x.ManagedByAdminId == adminId)
            .SortBy(x => x.DisplayName)
            .ToListAsync(ct);
        return docs.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<User>> ListChildPharmaciesAsync(
        string parentId, CancellationToken ct)
    {
        var docs = await _collection
            .Find(x => x.ParentPharmacyId == parentId)
            .SortBy(x => x.DisplayName)
            .ToListAsync(ct);
        return docs.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<User>> ListByRoleAsync(UserRole role, CancellationToken ct)
    {
        var docs = await _collection
            .Find(x => x.Role == (int)role)
            .SortBy(x => x.DisplayName)
            .ToListAsync(ct);
        return docs.Select(ToDomain).ToList();
    }

    public async Task<bool> AnyWithRoleAsync(UserRole role, CancellationToken ct) =>
        await _collection.Find(x => x.Role == (int)role).AnyAsync(ct);

    public async Task<UserStats> GetStatsAsync(CancellationToken ct)
    {
        var monthStart = BillingMonth.StartUtc();
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        var f = Builders<UserDocument>.Filter;

        // Eleven counts, issued together. Mongo handles these cheaply against
        // the role/tier indexes, and one round trip per count would make the
        // dashboard noticeably slow over a remote Atlas connection.
        var tasks = new[]
        {
            // Same helpers as the list filters — counting with a plain equality
            // test undercounted every default-valued field, which is how the
            // dashboard's "Pharmacies" card and the Pharmacies page came to
            // disagree with the unfiltered total.
            _collection.CountDocumentsAsync(f.Empty, cancellationToken: ct),
            _collection.CountDocumentsAsync(ActiveIs(true), cancellationToken: ct),
            _collection.CountDocumentsAsync(ActiveIs(false), cancellationToken: ct),
            _collection.CountDocumentsAsync(RoleIs(UserRole.SuperAdmin), cancellationToken: ct),
            _collection.CountDocumentsAsync(RoleIs(UserRole.Admin), cancellationToken: ct),
            _collection.CountDocumentsAsync(RoleIs(UserRole.PharmacyUser), cancellationToken: ct),
            _collection.CountDocumentsAsync(TierIs(SubscriptionTier.Free), cancellationToken: ct),
            _collection.CountDocumentsAsync(TierIs(SubscriptionTier.Starter), cancellationToken: ct),
            _collection.CountDocumentsAsync(TierIs(SubscriptionTier.Pro), cancellationToken: ct),
            _collection.CountDocumentsAsync(f.Gte(x => x.CreatedAtUtc, monthStart), cancellationToken: ct),
            _collection.CountDocumentsAsync(f.Gte(x => x.LastLoginAtUtc, thirtyDaysAgo), cancellationToken: ct),
            _collection.CountDocumentsAsync(IsPharmacyAccount(), cancellationToken: ct),
        };

        var r = await Task.WhenAll(tasks);

        return new UserStats(
            Total: r[0], Active: r[1], Inactive: r[2],
            SuperAdmins: r[3], Admins: r[4], PharmacyUsers: r[5],
            FreeTier: r[6], StarterTier: r[7], ProTier: r[8],
            NewThisMonth: r[9], ActiveLast30Days: r[10],
            PharmacyAccounts: r[11]);
    }

    public async Task<IReadOnlyDictionary<string, int>> CountManagedPerAdminAsync(CancellationToken ct)
    {
        var results = await _collection.Aggregate()
            .Match(x => x.ManagedByAdminId != null)
            .Group(x => x.ManagedByAdminId, g => new { AdminId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return results
            .Where(r => r.AdminId is not null)
            .ToDictionary(r => r.AdminId!, r => r.Count);
    }

    /* ---------------- Filters for late-added fields ---------------- */

    // role, tier and isActive were added after accounts already existed. Each
    // carries [BsonDefaultValue], which makes an old document DESERIALISE with
    // the default — but a query runs in the database, where a document lacking
    // the field does not match an equality test against that default.
    //
    // UserFieldBackfill writes the values in at startup, so after one boot the
    // $exists arm matches nothing. These helpers keep results correct in the
    // window before it runs, on a replica that hasn't caught up, and for any
    // document written by an older build. The cost is one extra clause on an
    // $or; the alternative is a filter that silently omits accounts, which is
    // exactly how the pharmacy list came to show a single row.

    private static FilterDefinition<UserDocument> RoleIs(UserRole role)
    {
        var f = Builders<UserDocument>.Filter;
        var eq = f.Eq(x => x.Role, (int)role);
        return role == UserRole.PharmacyUser
            ? f.Or(eq, f.Exists(x => x.Role, false))
            : eq;
    }

    /// <summary>
    /// Accounts that are a pharmacy, regardless of the platform role attached.
    /// </summary>
    /// <remarks>
    /// Two arms, and both are load-bearing:
    ///
    ///   - <c>RoleIs(PharmacyUser)</c> catches ordinary customers, including
    ///     legacy documents written before the role field existed.
    ///   - The pharmacyName arm catches an account that has been promoted to
    ///     Admin or SuperAdmin but still owns a pharmacy. Role changes never
    ///     clear the profile, so the pharmacy is still there — it had simply
    ///     stopped being listed anywhere.
    ///
    /// The name test is "exists and is not empty", not just "exists". Staff
    /// accounts created by the seeder or by an administrator get
    /// <c>PharmacyProfile.ForStaff()</c>, which writes a profile object with an
    /// empty name. Testing existence alone would pull every administrator onto
    /// the Pharmacies page — the opposite failure, and a more confusing one.
    ///
    /// The name is what separates them, and deliberately not
    /// <c>onboardingCompleted</c>: that is true on a staff account, because
    /// staff have no pharmacy to set up. Keying this off the flag would put
    /// every administrator back on the Pharmacies page.
    /// </remarks>
    private static FilterDefinition<UserDocument> IsPharmacyAccount()
    {
        var f = Builders<UserDocument>.Filter;
        return f.Or(
            RoleIs(UserRole.PharmacyUser),
            f.And(
                f.Exists("profile.pharmacyName", true),
                // Explicit BsonValue: an untyped Ne against BsonNull.Value
                // otherwise infers TField = BsonNull, which is a needlessly
                // narrow serialiser for what should just be `{$ne: null}`.
                f.Ne<BsonValue>("profile.pharmacyName", BsonNull.Value),
                // Both checks are needed. A null value passes `$ne: ""` and an
                // empty string passes `$ne: null`, so neither one alone
                // excludes a staff account carrying PharmacyProfile.Empty().
                f.Ne("profile.pharmacyName", "")));
    }

    private static FilterDefinition<UserDocument> TierIs(SubscriptionTier tier)
    {
        var f = Builders<UserDocument>.Filter;
        var eq = f.Eq(x => x.Tier, (int)tier);
        return tier == SubscriptionTier.Free
            ? f.Or(eq, f.Exists(x => x.Tier, false))
            : eq;
    }

    private static FilterDefinition<UserDocument> ActiveIs(bool active)
    {
        var f = Builders<UserDocument>.Filter;
        var eq = f.Eq(x => x.IsActive, active);
        // Only the `true` side needs the fallback: an absent flag means the
        // account was never deactivated.
        return active ? f.Or(eq, f.Exists(x => x.IsActive, false)) : eq;
    }

    private static FilterDefinition<UserDocument> BuildFilter(UserQuery q)
    {
        var f = Builders<UserDocument>.Filter;
        var clauses = new List<FilterDefinition<UserDocument>>();

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // Escaped so a search for "a.b" can't be injected as a regex. Anchored
            // nowhere, so it matches substrings — which is what an operator
            // typing part of a pharmacy name expects.
            var term = System.Text.RegularExpressions.Regex.Escape(q.Search.Trim());
            var rx = new MongoDB.Bson.BsonRegularExpression(term, "i");
            clauses.Add(f.Or(
                f.Regex(x => x.Email, rx),
                f.Regex(x => x.DisplayName, rx),
                f.Regex("profile.pharmacyName", rx)));
        }

        if (q.Role is { } role) clauses.Add(RoleIs(role));
        if (q.PharmacyAccountsOnly) clauses.Add(IsPharmacyAccount());
        if (q.IsActive is { } active) clauses.Add(ActiveIs(active));
        if (q.Tier is { } tier) clauses.Add(TierIs(tier));

        if (!string.IsNullOrWhiteSpace(q.ManagedByAdminId))
            clauses.Add(f.Eq(x => x.ManagedByAdminId, q.ManagedByAdminId));

        // "Unassigned" is about pharmacies needing an account manager, so it
        // uses the same definition of pharmacy as the page it feeds. Keying it
        // off role would leave a promoted pharmacy permanently invisible here
        // too, even with nobody looking after it.
        if (q.UnassignedOnly)
            clauses.Add(f.And(
                IsPharmacyAccount(),
                f.Eq(x => x.ManagedByAdminId, null)));

        if (q.Onboarded is { } onboarded)
        {
            // String path, matching how profile.pharmacyName is queried above —
            // the flag lives on the nested profile document.
            //
            // Accounts created before onboarding existed have no such field at
            // all, and in Mongo a missing field is not equal to false. Testing
            // "not onboarded" therefore has to mean "false OR absent", or those
            // accounts vanish from both sides of the filter.
            clauses.Add(onboarded
                ? f.Eq("profile.onboardingCompleted", true)
                : f.Ne("profile.onboardingCompleted", true));
        }

        // Same convention as every other list: from inclusive, to exclusive.
        if (q.CreatedFromUtc is { } from)
            clauses.Add(f.Gte(x => x.CreatedAtUtc, from));
        if (q.CreatedToUtc is { } to)
            clauses.Add(f.Lt(x => x.CreatedAtUtc, to));

        return clauses.Count == 0 ? f.Empty : f.And(clauses);
    }

    private static UserDocument ToDocument(User u) => new()
    {
        Id = u.Id,
        Email = u.Email.Value,
        DisplayName = u.DisplayName,
        PasswordHash = u.PasswordHash.Value,
        Tier = (int)u.Tier,
        Role = (int)u.Role,
        ManagedByAdminId = u.ManagedByAdminId,
        IsActive = u.IsActive,
        AccountStatus = (int)u.Status,
        ParentPharmacyId = u.ParentPharmacyId,
        LastLoginAtUtc = u.LastLoginAtUtc,
        EmailVerifiedAtUtc = u.EmailVerifiedAtUtc,
        SessionsValidAfterUtc = u.SessionsValidAfterUtc,
        HeadOfficeGrantedAtUtc = u.HeadOfficeGrantedAtUtc,
        Profile = new PharmacyProfileDocument
        {
            PharmacyName = u.Profile.PharmacyName,
            Address = u.Profile.Address,
            PostCode = u.Profile.PostCode,
            Description = u.Profile.Description,
            ContactName = u.Profile.ContactName,
            ContactPhone = u.Profile.ContactPhone,
            SonarFCode = u.Profile.SonarFCode,
            OnboardingCompleted = u.Profile.OnboardingCompleted,
            // `bookingUrl` is deliberately not written. The value moved to the
            // brand kit; the document field survives only so un-migrated rows
            // still deserialise. See BookingLinkToBrandKitMigration.
        },
        Billing = new BillingProfileDocument
        {
            StripeCustomerId = u.Billing.StripeCustomerId,
            StripeSubscriptionId = u.Billing.StripeSubscriptionId,
            Status = (int)u.Billing.Status,
            CurrentPeriodEndUtc = u.Billing.CurrentPeriodEndUtc,
            CancelAtPeriodEnd = u.Billing.CancelAtPeriodEnd,
            VideoCredits = u.Billing.VideoCredits,
        },
        CreatedAtUtc = u.CreatedAtUtc,
        UpdatedAtUtc = u.UpdatedAtUtc,
    };

    private static User ToDomain(UserDocument d)
    {
        var profile = new PharmacyProfile(
            d.Profile?.PharmacyName ?? string.Empty,
            d.Profile?.Address ?? string.Empty,
            d.Profile?.PostCode ?? string.Empty,
            d.Profile?.Description ?? string.Empty,
            d.Profile?.ContactName ?? string.Empty,
            d.Profile?.ContactPhone ?? string.Empty,
            d.Profile?.SonarFCode,
            d.Profile?.OnboardingCompleted ?? false);

        // Users created before billing shipped have no `billing` sub-document.
        // Treat that as "never purchased" rather than a deserialisation error.
        var billing = d.Billing is null
            ? BillingProfile.Empty()
            : new BillingProfile(
                d.Billing.StripeCustomerId,
                d.Billing.StripeSubscriptionId,
                (SubscriptionStatus)d.Billing.Status,
                d.Billing.CurrentPeriodEndUtc,
                d.Billing.CancelAtPeriodEnd,
                d.Billing.VideoCredits);

        return UserMapper.Hydrate(
            d.Id,
            d.Email,
            d.DisplayName,
            d.PasswordHash,
            (SubscriptionTier)d.Tier,
            profile,
            billing,
            (UserRole)d.Role,
            d.ManagedByAdminId,
            d.IsActive,
            (AccountStatus)d.AccountStatus,
            d.ParentPharmacyId,
            d.LastLoginAtUtc,
            d.EmailVerifiedAtUtc,
            d.HeadOfficeGrantedAtUtc,
            d.CreatedAtUtc,
            d.UpdatedAtUtc,
            d.SessionsValidAfterUtc);
    }
}
