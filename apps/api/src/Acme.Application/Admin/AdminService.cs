using Acme.Application.Auth;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Acme.Application.Templates;
using Acme.Application.Videos;
using Acme.Domain.Admin;
using Acme.Domain.Common;
using Acme.Domain.Subscriptions;
using Acme.Domain.Users;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Admin;

/// <summary>
/// The admin surface, and the only place row-level access control lives.
///
/// The controller's policies answer "may this person enter the admin area?".
/// They cannot answer "may this caller perform *this* action on *this* user?",
/// because that depends on data. So every method here takes an
/// <see cref="AdminContext"/> describing the caller — built from the validated
/// JWT, never from the request body — and re-derives what they may do.
///
/// The rules, in three lines:
///   - Admins may read and manage every PHARMACY account, platform-wide.
///   - Admins may not act on staff at all. Creating, editing, and re-roling
///     administrators is SuperAdmin-only.
///   - SuperAdmins may do everything, except demote the last active SuperAdmin
///     or change their own role — both of which are one-way locks.
///
/// ManagedByAdminId still exists but is no longer an access boundary. It now
/// records which administrator owns the customer relationship, for reporting
/// and account management. Nothing keys authorisation off it.
/// </summary>
public sealed class AdminService(
    IUserRepository users,
    IVideoRepository videos,
    IAdminAuditRepository audit,
    IPasswordHasher hasher,
    IRefreshTokenRepository refreshTokens,
    ITemplateGenerator generator,
    ITemplateAssetService assets,
    IPosterRepository posters,
    ICampaignRepository campaigns,
    IPlatformAnalyticsRepository analytics,
    ILogger<AdminService> log,
    ISessionGuard sessions)
{
    /* ---------------------------------------------------------------- *
     * Reads
     * ---------------------------------------------------------------- */

    public async Task<Result<AdminListDto>> ListUsersAsync(
        AdminContext actor, UserQuery query, CancellationToken ct)
    {
        // No scope narrowing: every administrator sees the full user base.
        // The managedBy filter in the query is now a convenience for browsing
        // by account manager, not a restriction being enforced.
        var page = await users.SearchAsync(query, ct);
        var adminNames = await BuildAdminNameLookupAsync(ct);

        return Result<AdminListDto>.Success(new AdminListDto(
            page.Items.Select(u => ToDto(u, adminNames)).ToList(),
            page.Total,
            query.Skip,
            query.Take));
    }

    public async Task<Result<AdminUserDto>> GetUserAsync(
        AdminContext actor, string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<AdminUserDto>.NotFound("User not found.");

        if (!CanActOn(actor, user, out var denial))
            return Result<AdminUserDto>.Forbidden(denial);

        var adminNames = await BuildAdminNameLookupAsync(ct);
        return Result<AdminUserDto>.Success(ToDto(user, adminNames));
    }

    /// <summary>
    /// Every admin, with their managed-pharmacy counts.
    ///
    /// Readable by any staff member: admins need this to populate the
    /// "account manager" picker and to see who owns a relationship. Creating
    /// and editing administrators remains SuperAdmin-only.
    /// </summary>
    public async Task<Result<IReadOnlyList<AdminSummaryDto>>> ListAdminsAsync(
        AdminContext actor, CancellationToken ct)
    {
        var admins = await users.ListByRoleAsync(UserRole.Admin, ct);
        var counts = await users.CountManagedPerAdminAsync(ct);

        return Result<IReadOnlyList<AdminSummaryDto>>.Success(
            admins.Select(a => new AdminSummaryDto(
                a.Id,
                a.Email.Value,
                a.DisplayName,
                a.IsActive,
                counts.TryGetValue(a.Id, out var n) ? n : 0,
                a.LastLoginAtUtc,
                a.CreatedAtUtc)).ToList());
    }

    /// <summary>
    /// Platform-wide figures for every administrator. Previously an Admin saw
    /// only their own portfolio; now that they manage the whole user base,
    /// scoped totals would just be a less useful view of the same data.
    /// </summary>
    public async Task<Result<AdminStatsDto>> GetStatsAsync(
        AdminContext actor, CancellationToken ct)
    {
        // Issued together rather than sequentially — the dashboard loads on
        // every admin page view, and serialising a dozen counts over a remote
        // Atlas connection is the difference between instant and sluggish.
        var statsTask = users.GetStatsAsync(ct);
        var totalVideosTask = videos.CountAllAsync(ct);
        var videosThisMonthTask = videos.CountAllThisMonthAsync(ct);
        var postersTask = posters.CountAllAsync(ct);
        var campaignsTask = campaigns.GetPlatformTotalsAsync(ct);

        await Task.WhenAll(
            statsTask, totalVideosTask, videosThisMonthTask, postersTask, campaignsTask);

        var stats = statsTask.Result;
        var totalVideos = totalVideosTask.Result;
        var videosThisMonth = videosThisMonthTask.Result;
        var (totalPosters, postersThisMonth) = postersTask.Result;
        var campaignTotals = campaignsTask.Result;

        var mrr =
            stats.StarterTier * Plans.For(SubscriptionTier.Starter).MonthlyPriceGbp
            + stats.ProTier * Plans.For(SubscriptionTier.Pro).MonthlyPriceGbp;

        return Result<AdminStatsDto>.Success(new AdminStatsDto(
            stats.Total, stats.Active, stats.Inactive,
            stats.SuperAdmins, stats.Admins, stats.PharmacyUsers,
            stats.PharmacyAccounts,
            stats.FreeTier, stats.StarterTier, stats.ProTier,
            stats.NewThisMonth, stats.ActiveLast30Days,
            videosThisMonth, totalVideos,
            postersThisMonth, totalPosters,
            campaignTotals.SentThisMonth, campaignTotals.SentTotal,
            campaignTotals.RecipientsReached,
            // Estimate, deliberately: it prices every paid user at list rate and
            // ignores annual plans, promo codes, proration, refunds, and failed
            // payments. Stripe is the source of truth for actual revenue — this
            // is a health signal, not an accounting figure.
            EstimatedMonthlyRevenueGbp: mrr,
            EstimatedAnnualRunRateGbp: mrr * 12));
    }

    /// <summary>
    /// Platform-wide activity over a rolling window.
    /// </summary>
    /// <remarks>
    /// Days is clamped rather than rejected: this feeds a range picker, and an
    /// out-of-range value should show the nearest sensible chart instead of an
    /// error the operator can do nothing about.
    /// </remarks>
    public async Task<Result<AdminAnalyticsDto>> GetAnalyticsAsync(
        AdminContext actor, int days, CancellationToken ct)
    {
        _ = actor; // staff-only by controller policy; no per-actor scoping
        var window = Math.Clamp(days, 7, 90);

        // Midnight UTC so the first bucket is a whole day. Anchoring to "now
        // minus N days" would make today's partial bucket look like a slump.
        var since = DateTime.UtcNow.Date.AddDays(-(window - 1));

        var postersTask = analytics.PostersPerDayAsync(since, ct);
        var videosTask = analytics.VideosPerDayAsync(since, ct);
        var campaignsTask = analytics.CampaignsPerDayAsync(since, ct);
        var ownersTask = analytics.TopOwnersAsync(since, 10, ct);

        await Task.WhenAll(postersTask, videosTask, campaignsTask, ownersTask);

        var posters = postersTask.Result.ToDictionary(d => d.Date, d => d.Count);
        var videos = videosTask.Result.ToDictionary(d => d.Date, d => d.Count);
        var campaigns = campaignsTask.Result.ToDictionary(d => d.Date, d => d.Count);

        // Every day in the window gets a point, including empty ones. Mongo only
        // returns days that have documents, and plotting that directly draws a
        // line straight from one active day to the next — which reads as steady
        // activity across a gap where there was none.
        var series = new List<AnalyticsPoint>(window);
        for (var i = 0; i < window; i++)
        {
            var date = since.AddDays(i).ToString("yyyy-MM-dd");
            series.Add(new AnalyticsPoint(
                date,
                posters.GetValueOrDefault(date),
                videos.GetValueOrDefault(date),
                campaigns.GetValueOrDefault(date)));
        }

        var top = await ResolveTopPharmaciesAsync(ownersTask.Result, ct);

        return Result<AdminAnalyticsDto>.Success(new AdminAnalyticsDto(
            window,
            series,
            top,
            series.Sum(p => p.Posters),
            series.Sum(p => p.Videos),
            series.Sum(p => p.Campaigns)));
    }

    /// <summary>Attaches names to the owner ids the aggregation returned.</summary>
    private async Task<IReadOnlyList<TopPharmacyDto>> ResolveTopPharmaciesAsync(
        IReadOnlyList<OwnerActivity> owners, CancellationToken ct)
    {
        var result = new List<TopPharmacyDto>(owners.Count);

        foreach (var owner in owners)
        {
            var user = await users.FindByIdAsync(owner.OwnerId, ct);

            // An account can be deleted while its output remains. Showing the
            // activity with a placeholder is more honest than dropping the row,
            // which would make the totals disagree with the chart above.
            result.Add(new TopPharmacyDto(
                owner.OwnerId,
                user?.Profile.PharmacyName is { Length: > 0 } name
                    ? name
                    : user?.DisplayName ?? "(deleted account)",
                user?.Email.Value ?? "—",
                owner.Posters,
                owner.Videos,
                owner.Campaigns,
                owner.Total));
        }

        return result;
    }

    public async Task<Result<AuditListDto>> ListAuditAsync(
        AdminContext actor, AuditQuery query, CancellationToken ct)
    {
        // The legacy staff log, now SuperAdmin-only. It has no tenant column,
        // so it cannot honour "an Admin sees only their linked pharmacies";
        // the audit console (AuditEventService) can, and is what Admins use.
        // Kept for one release so existing links and scripts keep working.
        if (!actor.IsSuperAdmin)
            return Result<AuditListDto>.Forbidden(
                "The legacy audit log is SuperAdmin-only. Use the audit console.");

        var page = await audit.SearchAsync(query, ct);

        return Result<AuditListDto>.Success(new AuditListDto(
            page.Items.Select(e => new AuditEntryDto(
                e.Id, e.ActorEmail, e.Action, e.TargetEmail, e.Detail, e.CreatedAtUtc)).ToList(),
            page.Total,
            query.Skip,
            query.Take));
    }

    /* ---------------------------------------------------------------- *
     * Admin management — SuperAdmin only
     * ---------------------------------------------------------------- */

    public async Task<Result<AdminSummaryDto>> CreateAdminAsync(
        AdminContext actor, CreateAdminRequest req, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<AdminSummaryDto>.Forbidden("Only a SuperAdmin can create administrators.");

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 12)
            return Result<AdminSummaryDto>.Invalid(
                "Administrator passwords must be at least 12 characters.");

        Email email;
        try { email = Email.Create(req.Email); }
        catch (DomainException ex) { return Result<AdminSummaryDto>.Invalid(ex.Message); }

        if (await users.FindByEmailAsync(email, ct) is not null)
            return Result<AdminSummaryDto>.Conflict("That email address is already registered.");

        User admin;
        try
        {
            admin = User.CreateStaff(
                email, req.DisplayName, PasswordHash.FromHash(hasher.Hash(req.Password)),
                UserRole.Admin);
        }
        catch (DomainException ex) { return Result<AdminSummaryDto>.Invalid(ex.Message); }

        await users.AddAsync(admin, ct);
        await RecordAsync(actor, AdminAction.CreateAdmin, admin, null, ct);

        log.LogInformation("SuperAdmin {Actor} created admin {AdminEmail}", PersonalData.Mask(actor.Email), PersonalData.Mask(admin.Email.Value));

        return Result<AdminSummaryDto>.Success(new AdminSummaryDto(
            admin.Id, admin.Email.Value, admin.DisplayName, admin.IsActive, 0, null, admin.CreatedAtUtc));
    }

    public async Task<Result<AdminSummaryDto>> UpdateAdminAsync(
        AdminContext actor, string adminId, UpdateAdminRequest req, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<AdminSummaryDto>.Forbidden("Only a SuperAdmin can edit administrators.");

        var admin = await users.FindByIdAsync(adminId, ct);
        if (admin is null || admin.Role != UserRole.Admin)
            return Result<AdminSummaryDto>.NotFound("Administrator not found.");

        if (!string.IsNullOrWhiteSpace(req.DisplayName))
        {
            try { admin.Rename(req.DisplayName); }
            catch (DomainException ex) { return Result<AdminSummaryDto>.Invalid(ex.Message); }
        }

        if (req.IsActive is { } active)
        {
            if (active) admin.Reactivate();
            else
            {
                admin.Deactivate();
                // Kill live sessions immediately. Deactivation that leaves a
                // valid access token in play isn't deactivation.
                await refreshTokens.RevokeAllForUserAsync(admin.Id, ct);
            }
        }

        await users.UpdateAsync(admin, ct);
        // Saved first, then forgotten: the next request re-reads the new state (R2-09).
        sessions.Forget(admin.Id);
        await RecordAsync(actor, AdminAction.UpdateAdmin, admin,
            req.IsActive is { } a ? (a ? "reactivated" : "deactivated") : "details updated", ct);

        var counts = await users.CountManagedPerAdminAsync(ct);
        return Result<AdminSummaryDto>.Success(new AdminSummaryDto(
            admin.Id, admin.Email.Value, admin.DisplayName, admin.IsActive,
            counts.TryGetValue(admin.Id, out var n) ? n : 0,
            admin.LastLoginAtUtc, admin.CreatedAtUtc));
    }

    public async Task<Result<bool>> ChangeRoleAsync(
        AdminContext actor, string userId, UserRole role, CancellationToken ct)
    {
        if (!actor.IsSuperAdmin)
            return Result<bool>.Forbidden("Only a SuperAdmin can change roles.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<bool>.NotFound("User not found.");

        if (user.Id == actor.UserId)
            return Result<bool>.Invalid("You cannot change your own role.");

        // Guard the last SuperAdmin. Demoting the only one locks everybody out
        // of admin permanently, with no in-app way back.
        if (user.Role == UserRole.SuperAdmin && role != UserRole.SuperAdmin)
        {
            var others = (await users.ListByRoleAsync(UserRole.SuperAdmin, ct))
                .Count(u => u.Id != user.Id && u.IsActive);
            if (others == 0)
                return Result<bool>.Invalid("You cannot demote the last active SuperAdmin.");
        }

        // Demoting an admin who still owns pharmacy relationships would leave
        // those accounts with no named owner. Only blocks when there actually
        // are some — an admin with an empty book demotes cleanly.
        if (user.Role == UserRole.Admin && role == UserRole.PharmacyUser)
        {
            var owned = await users.ListManagedByAsync(user.Id, ct);
            if (owned.Count > 0)
                return Result<bool>.Invalid(
                    $"{user.DisplayName} is still the account manager for {owned.Count} " +
                    $"{(owned.Count == 1 ? "pharmacy" : "pharmacies")}. Reassign them first.");
        }

        var previous = user.Role;
        try { user.ChangeRole(role); }
        catch (DomainException ex) { return Result<bool>.Invalid(ex.Message); }

        await users.UpdateAsync(user, ct);
        await RecordAsync(actor, AdminAction.ChangeRole, user, $"{previous} → {role}", ct);
        return Result<bool>.Success(true);
    }

    /* ---------------------------------------------------------------- *
     * User management — Admin (own users) or SuperAdmin (all)
     * ---------------------------------------------------------------- */

    public async Task<Result<bool>> SetUserActiveAsync(
        AdminContext actor, string userId, bool active, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<bool>.NotFound("User not found.");

        if (user.Id == actor.UserId)
            return Result<bool>.Invalid("You cannot deactivate your own account.");

        if (!CanActOn(actor, user, out var denial))
            return Result<bool>.Forbidden(denial);

        if (active) user.Reactivate();
        else
        {
            user.Deactivate();
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
        }

        await users.UpdateAsync(user, ct);
        sessions.Forget(user.Id); // R2-09: applies to the next request, not in 30 s

        await RecordAsync(
            actor,
            active ? AdminAction.ReactivateUser : AdminAction.DeactivateUser,
            user, null, ct);

        return Result<bool>.Success(true);
    }

    public async Task<Result<bool>> AssignUserAsync(
        AdminContext actor, string userId, string adminId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<bool>.NotFound("User not found.");

        // Assignment now records who owns the relationship rather than who is
        // permitted to act, so any administrator can reassign any pharmacy to
        // any colleague. The audit log captures who moved what.
        if (!CanActOn(actor, user, out var denial))
            return Result<bool>.Forbidden(denial);

        var admin = await users.FindByIdAsync(adminId, ct);
        if (admin is null || admin.Role != UserRole.Admin)
            return Result<bool>.Invalid("Target administrator not found.");
        if (!admin.IsActive)
            return Result<bool>.Invalid("Cannot assign pharmacies to a deactivated administrator.");

        try { user.AssignToAdmin(adminId); }
        catch (DomainException ex) { return Result<bool>.Invalid(ex.Message); }

        await users.UpdateAsync(user, ct);
        await RecordAsync(actor, AdminAction.AssignUserToAdmin, user, $"→ {admin.Email.Value}", ct);
        return Result<bool>.Success(true);
    }

    public async Task<Result<bool>> UnassignUserAsync(
        AdminContext actor, string userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<bool>.NotFound("User not found.");

        if (!CanActOn(actor, user, out var denial))
            return Result<bool>.Forbidden(denial);

        user.UnassignFromAdmin();
        await users.UpdateAsync(user, ct);
        await RecordAsync(actor, AdminAction.UnassignUserFromAdmin, user, null, ct);
        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Manually sets a subscription tier, bypassing Stripe.
    ///
    /// Intended for comped accounts, goodwill, and support recovery. It does
    /// NOT create or modify a Stripe subscription, so if the user has one, the
    /// next webhook will overwrite this. That's the correct precedence —
    /// Stripe owns paid state — but it makes this a blunt instrument, which is
    /// why the reason is recorded in the audit log.
    /// </summary>
    public async Task<Result<bool>> OverrideTierAsync(
        AdminContext actor, string userId, SubscriptionTier tier, string? reason, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<bool>.NotFound("User not found.");

        if (!CanActOn(actor, user, out var denial))
            return Result<bool>.Forbidden(denial);

        var previous = user.Tier;
        user.ChangeTier(tier);
        await users.UpdateAsync(user, ct);

        var detail = $"{previous} → {tier}"
            + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.Trim()})")
            + (user.Billing.StripeSubscriptionId is not null
                ? " [warning: user has an active Stripe subscription that will override this]"
                : "");

        await RecordAsync(actor, AdminAction.OverrideSubscriptionTier, user, detail, ct);
        log.LogWarning("Tier override by {Actor} on {Target}: {Detail}", PersonalData.Mask(actor.Email), PersonalData.Mask(user.Email.Value), detail);

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Grants or withdraws head-office status: the ability to run branches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Granted by staff rather than assumed by the pharmacy, because creating a
    /// branch creates a real login for another pharmacy and emails a credential
    /// to whatever address was typed. That is not something an account should be
    /// able to give itself.
    /// </para>
    /// <para>
    /// Withdrawing leaves existing branches attached. Only the ability to add
    /// more is removed — silently orphaning a group would be a far worse outcome
    /// than a head office that can no longer expand, and detaching a branch is
    /// already a separate, deliberate action.
    /// </para>
    /// </remarks>
    public async Task<Result<bool>> SetHeadOfficeAsync(
        AdminContext actor, string userId, bool granted, string? reason, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<bool>.NotFound("User not found.");

        if (!CanActOn(actor, user, out var denial))
            return Result<bool>.Forbidden(denial);

        // Head office is a pharmacy-side capability. Handing it to a platform
        // staff account would create an administrator who also runs branches,
        // which muddles the one boundary the whole permission model rests on.
        if (user.Role != UserRole.PharmacyUser)
            return Result<bool>.Invalid("Only a pharmacy account can be a head office.");

        if (user.IsChildPharmacy)
            return Result<bool>.Invalid(
                "This pharmacy sits under another one, so it cannot run branches of its own.");

        if (user.IsHeadOffice == granted)
            return Result<bool>.Success(true);

        var branches = await users.ListChildPharmaciesAsync(user.Id, ct);

        if (granted) user.GrantHeadOffice();
        else user.RevokeHeadOffice();

        await users.UpdateAsync(user, ct);

        // The branch count goes in the detail because it is the fact that makes
        // a withdrawal worth a second look: taking the status from a pharmacy
        // that runs six branches is a different act from taking it from one that
        // runs none.
        var detail = (granted ? "granted head office" : "withdrew head office")
            + $" ({branches.Count} existing branch(es))"
            + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.Trim()})");

        await RecordAsync(actor, AdminAction.SetHeadOffice, user, detail, ct);
        log.LogWarning(
            "Head office {Change} by {Actor} on {Target}: {Detail}",
            granted ? "granted" : "withdrawn", PersonalData.Mask(actor.Email), PersonalData.Mask(user.Email.Value), detail);

        return Result<bool>.Success(true);
    }

    public async Task<Result<bool>> GrantCreditsAsync(
        AdminContext actor, string userId, int credits, string? reason, CancellationToken ct)
    {
        if (credits is <= 0 or > 1000)
            return Result<bool>.Invalid("Credits must be between 1 and 1000.");

        var user = await users.FindByIdAsync(userId, ct);
        if (user is null) return Result<bool>.NotFound("User not found.");

        if (!CanActOn(actor, user, out var denial))
            return Result<bool>.Forbidden(denial);

        user.AddVideoCredits(credits);
        await users.UpdateAsync(user, ct);
        await RecordAsync(actor, AdminAction.GrantVideoCredits, user,
            $"+{credits}" + (string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason.Trim()})"), ct);

        return Result<bool>.Success(true);
    }

    /* ---------------------------------------------------------------- *
     * Helpers
     * ---------------------------------------------------------------- */

    /// <summary>
    /// The single access-control predicate. Everything that mutates or reveals
    /// a specific user goes through here so the rule exists once.
    ///
    /// Admins have platform-wide reach over pharmacy accounts — they are not
    /// limited to the ones assigned to them. ManagedByAdminId still records
    /// who owns the relationship, but it is organisational metadata now, not
    /// an access boundary.
    ///
    /// The one limit that remains, and must: an Admin cannot act on staff.
    /// Without it any Admin could deactivate a peer or the SuperAdmin, which
    /// would make the role hierarchy meaningless and give every Admin an
    /// irreversible path to locking everyone else out.
    /// </summary>
    private static bool CanActOn(AdminContext actor, User target, out string denial)
    {
        denial = string.Empty;

        if (actor.IsSuperAdmin) return true;

        if (target.Role.IsStaff())
        {
            denial = "Only a SuperAdmin can manage administrator accounts.";
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildAdminNameLookupAsync(CancellationToken ct)
    {
        var admins = await users.ListByRoleAsync(UserRole.Admin, ct);
        return admins.ToDictionary(a => a.Id, a => a.DisplayName);
    }

    private static AdminUserDto ToDto(User u, IReadOnlyDictionary<string, string> adminNames) => new(
        u.Id,
        u.Email.Value,
        u.DisplayName,
        u.Role,
        u.Tier,
        u.IsActive,
        u.ManagedByAdminId,
        u.ManagedByAdminId is { } id && adminNames.TryGetValue(id, out var name) ? name : null,
        u.Profile.PharmacyName,
        u.Profile.PostCode,
        u.Profile.OnboardingCompleted,
        u.IsHeadOffice,
        u.IsChildPharmacy,
        u.Billing.VideoCredits,
        u.Billing.Status.ToString(),
        u.Billing.CurrentPeriodEndUtc,
        u.LastLoginAtUtc,
        u.CreatedAtUtc);


    /* ---------------------------------------------------------------- *
     * Template authoring
     * ---------------------------------------------------------------- */

    /// <summary>
    /// Generate a poster template from a brief, and record that it happened.
    /// </summary>
    /// <remarks>
    /// Nothing is persisted to the gallery here. Generation produces a
    /// candidate the administrator previews and explicitly publishes, because
    /// a template is shared platform-wide and a model should not be able to
    /// put a design in front of every pharmacy without a human looking at it.
    ///
    /// The audit entry is written on success only — a failed generation
    /// created nothing, and filling the log with attempts would bury the
    /// entries that record real change.
    /// </remarks>
    public async Task<Result<GeneratedTemplate>> GenerateTemplateAsync(
        AdminContext actor,
        GenerateTemplateRequest request,
        string bearerToken,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Trim().Length < 8)
        {
            return Result<GeneratedTemplate>.Invalid(
                "Describe the poster you want in a sentence or two.");
        }

        // An uploaded or previewed photo is already in template storage; the
        // generator reads it from there. Anything else claiming to be one is
        // refused rather than fetched.
        if (request.Photo?.Problem(assets.IsAssetUrl) is { } photoProblem)
            return Result<GeneratedTemplate>.Invalid(photoProblem);

        var result = await generator.GenerateAsync(request, bearerToken, ct);
        if (!result.IsSuccess) return result;

        // Notes are reserved for repair rounds — the UI renders them as
        // "this design needed correcting, look closer". Adding informational
        // entries here would make every AI-illustrated poster look suspect.
        var design = result.Value!;

        var imageDetail = request.Photo is null or { Mode: PosterPhotoMode.None }
            ? ""
            : $" with a {request.Photo.Mode.ToString().ToLowerInvariant()} photo";
        await audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId,
                actor.Email,
                AdminAction.GenerateTemplate,
                detail: $"Generated \"{design.Name}\"{imageDetail} from: {Shorten(request.Prompt, 140)}"),
            ct);

        log.LogInformation(
            "{Actor} generated template {Name}", PersonalData.Mask(actor.Email), design.Name);

        return Result<GeneratedTemplate>.Success(design);
    }

    /// <summary>
    /// Produce a photograph for a poster and store it, ready to design around.
    /// </summary>
    /// <remarks>
    /// The provider is chosen by ai-service: an AI model when one is
    /// configured, stock photography otherwise. The caller asked for "an
    /// image", not for a particular vendor, and making the UI branch on which
    /// credentials a deployment happens to hold would dead-end anyone who
    /// hasn't set up an image model.
    /// </remarks>
    public async Task<Result<PosterImage>> CreatePosterImageAsync(
        AdminContext actor,
        string description,
        string? aspect,
        int offset,
        string bearerToken,
        CancellationToken ct,
        string? provider = null)
    {
        // "ai" is AI or an error — the admin chose the AI mode and should not
        // be handed a stock photo instead. Anything else keeps the old "auto".
        var source = provider?.Trim().ToLowerInvariant() switch
        {
            "ai" => "ai",
            "stock" => "stock",
            _ => "auto",
        };

        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length < 4)
            return Result<PosterImage>.Invalid("Describe the photograph you want.");

        var produced = await generator.GenerateImageAsync(
            description.Trim(), aspect, offset, bearerToken, ct, source);

        if (!produced.IsSuccess)
        {
            return produced.Kind == ResultKind.Forbidden
                ? Result<PosterImage>.Forbidden(produced.Error!)
                : Result<PosterImage>.Invalid(produced.Error!);
        }

        var image = produced.Value!;
        await using var stream = new MemoryStream(image.Data);
        var stored = await assets.UploadAsync(stream, image.ContentType, string.Empty, ct);

        // AI providers return the aspect that was asked for but not always the
        // exact pixel size, so fall back to the nominal size for that slot.
        // Only the RATIO matters downstream — it's what stops the designer
        // stretching the picture.
        var (width, height) = image.Width > 0 && image.Height > 0
            ? (image.Width, image.Height)
            : ImageAspectSize(aspect);

        log.LogInformation(
            "{Actor} produced a poster image via {Source} ({Kb} KB)",
            PersonalData.Mask(actor.Email), image.Source, image.Data.Length / 1024);

        return Result<PosterImage>.Success(new PosterImage(
            stored.Url, width, height,
            image.Description ?? description.Trim(),
            image.Source, image.Remaining));
    }

    /// <summary>Search stock photography for the poster picker.</summary>
    public Task<Result<IReadOnlyList<StockPhoto>>> SearchStockPhotosAsync(
        AdminContext actor,
        string query,
        string? aspect,
        string bearerToken,
        CancellationToken ct)
    {
        _ = actor; // staff-only by controller policy; nothing per-actor to scope
        return generator.SearchStockPhotosAsync(query, aspect, bearerToken, ct);
    }

    /// <summary>
    /// Copy a chosen stock photo into our own storage and return it as a
    /// template asset.
    /// </summary>
    /// <remarks>
    /// The photo is copied rather than hot-linked. A canvas stores this URL
    /// forever and the PDF exporter re-fetches it at print time, so depending
    /// on a third-party CDN would mean every poster using that photo breaks
    /// the day the provider moves or removes it.
    /// </remarks>
    public async Task<Result<TemplateImage>> UseStockPhotoAsync(
        AdminContext actor,
        string photoId,
        string bearerToken,
        CancellationToken ct)
    {
        var fetched = await generator.FetchStockPhotoAsync(photoId, bearerToken, ct);
        if (!fetched.IsSuccess)
        {
            return fetched.Kind == ResultKind.Forbidden
                ? Result<TemplateImage>.Forbidden(fetched.Error!)
                : Result<TemplateImage>.Invalid(fetched.Error!);
        }

        var photo = fetched.Value!;
        await using var stream = new MemoryStream(photo.Data);
        var stored = await assets.UploadAsync(
            stream, photo.ContentType, string.Empty, ct);

        log.LogInformation(
            "{Actor} selected stock photo {PhotoId} ({Kb} KB)",
            PersonalData.Mask(actor.Email), photoId, photo.Data.Length / 1024);

        // Returns TemplateImage rather than StoredTemplateAsset because the
        // caller's next step is to generate a poster around it, and that needs
        // the natural dimensions (to avoid stretching) and the description.
        return Result<TemplateImage>.Success(new TemplateImage(
            stored.Url, photo.Width, photo.Height, photo.Description));
    }

    /// <summary>
    /// Nominal pixel size of a generated image, by requested aspect.
    /// </summary>
    /// <remarks>
    /// Flux returns whatever resolution it likes for a given aspect ratio, and
    /// decoding the bytes server-side just to read a header would pull an image
    /// library into the Application layer for no benefit. Only the RATIO
    /// matters here: the designer uses it to avoid stretching the picture, and
    /// the validator compares ratios rather than absolute sizes. These match
    /// the ratios in poster_imagery.ASPECT_RATIOS.
    /// </remarks>
    private static (double Width, double Height) ImageAspectSize(string? aspect) =>
        (aspect ?? "hero").ToLowerInvariant() switch
        {
            "banner" => (1600, 900),    // 16:9
            "portrait" => (1200, 1600), // 3:4
            "full" => (900, 1600),      // 9:16
            _ => (1600, 1200),          // 4:3 "hero"
        };

    private static string Shorten(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private Task RecordAsync(
        AdminContext actor, AdminAction action, User target, string? detail, CancellationToken ct) =>
        audit.AddAsync(
            AdminAuditEntry.Record(
                actor.UserId, actor.Email, action, target.Id, target.Email.Value, detail),
            ct);
}

/// <summary>
/// The authenticated caller, built by the controller from validated JWT claims.
/// Deliberately a small immutable record so no service can mutate its own
/// permissions mid-request.
/// </summary>
public sealed record AdminContext(string UserId, string Email, UserRole Role)
{
    public bool IsSuperAdmin => Role == UserRole.SuperAdmin;
}
