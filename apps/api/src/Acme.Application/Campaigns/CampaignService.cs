using System.Net;
using System.Text.RegularExpressions;
using Acme.Application.Audit;
using Acme.Application.Auth;
using Acme.Application.Export;
using Acme.Application.BrandKit;
using Acme.Application.Common;
using Acme.Application.Messaging;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Acme.Application.Videos;
using Acme.Domain.Campaigns;
using Acme.Domain.Common;
using Acme.Domain.Patients;
using Acme.Domain.Videos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Acme.Application.Campaigns;

public sealed class CampaignService(
    ICampaignRepository campaigns,
    IUserRepository users,
    IPosterRepository posters,
    IVideoRepository videos,
    IVideoStorage videoStorage,
    IPatientRepository patients,
    IPatientGroupRepository patientGroups,
    IEmailSender email,
    // Renders the poster and caches it. Normally only the blob key is used: the
    // bytes are served from /api/posters/email-image and transcoded there, so a
    // campaign's request to the mail provider carries no image at all.
    PosterImageService posterImages,
    // Read only when the poster has to be embedded instead of linked, which is
    // the case on a machine whose API has no publicly reachable address. See
    // ImagesTravelAsLinks.
    IPosterImageStore posterImageStore,
    IImageTranscoder imageTranscoder,
    IConfiguration configuration,
    // Read only for the pharmacy's name, when the profile has none. See
    // ResolveSenderNameAsync.
    IBrandKitRepository brandKits,
    // Counts only: the audit row says how many were sent to, never to whom.
    IAuditScope audit,
    ILogger<CampaignService> logger,
    // Addresses that unsubscribed from this pharmacy, and where the links in
    // each email point (security review R2-06).
    IEmailSuppressionRepository suppressions,
    IAuthLinkBuilder links)
{
    // ISmsSender was removed when SMS dispatch was disabled — re-add if SMS
    // comes back online. IVideoStorage is read only when a video thumbnail has
    // to be embedded rather than linked — see ImagesTravelAsLinks. It used to be
    // read on every send, which is what put a `data:` URL in front of Gmail.

    // Base URL for video links embedded in outgoing emails. Read from
    // config (Public__ApiBaseUrl) with a localhost fallback for dev.
    // Recipients follow these links from their inbox, so it MUST be a
    // publicly-reachable URL in production (Front Door / custom domain).
    private string PublicApiBaseUrl =>
        configuration["Public:ApiBaseUrl"]?.TrimEnd('/')
        ?? "http://localhost:5050";

    /// <summary>
    /// Whether the pictures in an email can be linked, or have to travel inside
    /// the message: the poster and every video thumbnail alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A mail client does not fetch images from the reader's browser.</b>
    /// Gmail fetches them from Google's servers, through its own proxy. So a
    /// poster served from <c>http://localhost:5050</c> — the fallback when
    /// <c>Public:ApiBaseUrl</c> is unset, which is every developer machine —
    /// cannot be fetched by anybody, and the reader gets an empty space where
    /// the design should be.
    /// </para>
    /// <para>
    /// It reads as flakiness, which is what makes it worth a named check rather
    /// than a note in a README. Apple Mail and Outlook on that same machine
    /// resolve <c>localhost</c> to it and show the poster perfectly; Gmail in a
    /// browser on the same screen shows nothing. Same message, same moment,
    /// opposite result.
    /// </para>
    /// <para>
    /// So: link it when the address is one the public internet can reach, embed
    /// it when it is not. Embedding is what the campaign did before it was made
    /// to scale, and it is still correct for the small local sends this covers —
    /// it is only at hundreds of recipients that copying the poster per
    /// personalisation becomes the thing that breaks the send.
    /// </para>
    /// <para>
    /// <b>The video thumbnail asks the same question and must get the same
    /// answer.</b> It did not: it stayed a base64 data URL after the poster was
    /// switched to a link, on the belief that data URLs "work everywhere". Gmail
    /// strips them, so a campaign with both arrived showing the poster and an
    /// empty space where the video still should be — while the watch link worked
    /// perfectly, which is what made it look like a styling problem rather than
    /// a sourcing one.
    /// </para>
    /// <para>
    /// Hence the name: this is not a fact about posters, it is a fact about
    /// whether a mail provider's fetcher can reach this API, and everything
    /// pictured in the email depends on it.
    /// </para>
    /// </remarks>
    private bool ImagesTravelAsLinks => IsReachableFromTheInternet(PublicApiBaseUrl);

    /// <summary>Whether a mail provider's image proxy could fetch this.</summary>
    /// <remarks>
    /// <para>
    /// Conservative: anything it cannot positively identify as public is treated
    /// as unreachable. Being wrong in that direction embeds a poster that could
    /// have been linked, which costs bytes on a send that was going to work.
    /// Being wrong the other way silently ships an email with no design in it.
    /// </para>
    /// <para>
    /// Public rather than internal because it is a rule worth testing directly,
    /// and this codebase tests helpers head-on rather than reaching through
    /// <c>InternalsVisibleTo</c> — the same choice <c>PosterImageService.BuildKey</c>
    /// and <c>ContactMask</c> already make.
    /// </para>
    /// </remarks>
    public static bool IsReachableFromTheInternet(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        // localhost, 127.0.0.0/8, ::1
        if (uri.IsLoopback) return false;

        var host = uri.Host;

        // Container and service names — `api`, `acme-api` — resolve inside a
        // Docker network and nowhere else.
        if (!host.Contains('.')) return false;

        // Bonjour/mDNS names are LAN-only.
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)) return false;

        // A LAN address is reachable from the machine and from nothing else,
        // which is the case that looks like it works right up until somebody
        // opens the email off the network.
        if (IPAddress.TryParse(host, out var ip)) return !IsPrivate(ip);

        return true;
    }

    /// <summary>
    /// Where a video's still image comes from in the email HTML.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A `data:` URL is not a universal fallback.</b> It was treated as one —
    /// "works everywhere with zero fetch" — and Gmail strips `data:` from
    /// `img src` and renders nothing. A campaign carrying a poster and a video
    /// arrived with the poster visible and a blank space above the watch button,
    /// while the link itself worked, which made it look like a layout bug.
    /// </para>
    /// <para>
    /// <b>The branch lives here, not at the call site.</b> The first version of
    /// this fix left `if (ImagesTravelAsLinks)` in the send loop and put a URL
    /// builder behind it, which meant the test could not fail if the branch were
    /// wrong — the one shape of test this codebase has twice been bitten by.
    /// Taking the decision means the reachability rule and the source it
    /// produces are one testable thing.
    /// </para>
    /// <para>
    /// <paramref name="loadEmbedded"/> is a delegate and not a value on purpose:
    /// reading and base64-ing the blob is the cost being avoided when the image
    /// can simply be linked, so it must not run to be discarded. A test asserts
    /// it is never invoked on the linked path.
    /// </para>
    /// </remarks>
    public static async Task<string?> ChooseThumbnailSrcAsync(
        bool canLink,
        string apiBase,
        string videoId,
        Func<string> signThumbnailToken,
        Func<Task<string?>> loadEmbedded)
    {
        if (canLink)
        {
            var token = signThumbnailToken();
            if (!string.IsNullOrWhiteSpace(token))
                return $"{apiBase.TrimEnd('/')}/api/videos/{videoId}/public-download?token={token}&thumb=true";
        }

        return await loadEmbedded();
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                                  // 10.0.0.0/8
                172 => b[1] >= 16 && b[1] <= 31,             // 172.16.0.0/12
                192 => b[1] == 168,                          // 192.168.0.0/16
                169 => b[1] == 254,                          // link-local
                _ => false,
            };
        }

        // fc00::/7 unique-local, fe80::/10 link-local.
        return ip.IsIPv6LinkLocal
            || ip.IsIPv6SiteLocal
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public Task<IReadOnlyList<CampaignDto>> ListAsync(string ownerId, CancellationToken ct) =>
        ListAsync(ownerId, ListWindow.Default, ct);

    /// <summary>The Campaigns page, sorted and ranged on sent date (else created) in the database.</summary>
    public async Task<IReadOnlyList<CampaignDto>> ListAsync(string ownerId, ListWindow window, CancellationToken ct)
    {
        var items = await campaigns.ListAsync(ownerId, window, ct);
        logger.LogInformation("Campaigns list for owner={OwnerId} returned {Count}", ownerId, items.Count);
        return items.Select(ToDto).ToList();
    }

    public async Task<Result<CampaignDto>> CreateAndSendAsync(
        string ownerId,
        CreateCampaignRequest request,
        CancellationToken ct)
    {
        // SMS is temporarily disabled — the /pricing page still lists it as a
        // future feature. Return a clear Forbidden so the frontend can hint the
        // right upgrade path.
        if (request.Channel == CampaignChannel.Sms)
            return Result<CampaignDto>.Forbidden(
                "SMS campaigns are temporarily disabled. Please send this campaign as an email instead.");

        // Duplicate-submit guard: if the exact same (name, posterId) was
        // created by this owner in the last 60 seconds, reject with 409.
        // Catches the classic "user double-clicks Send and gets two emails"
        // problem without needing an idempotency-key header from the client.
        var recentDuplicate = await FindRecentDuplicateAsync(ownerId, request, ct);
        if (recentDuplicate is not null)
        {
            logger.LogInformation(
                "Rejected duplicate campaign create for owner={OwnerId} name='{Name}' (matches {Id} from {AgeSec}s ago)",
                ownerId, request.Name, recentDuplicate.Id,
                (int)(DateTime.UtcNow - recentDuplicate.CreatedAtUtc).TotalSeconds);
            return Result<CampaignDto>.Conflict(
                $"You just sent a campaign called '{recentDuplicate.Name}'. If you didn't mean to submit twice, this is safe to ignore.");
        }

        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<CampaignDto>.NotFound("User not found.");

        var poster = await posters.FindByIdAsync(request.PosterId, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Poster not found.");

        // A campaign puts this poster in front of every patient on the list.
        // Until now any poster would do, including one somebody was halfway
        // through editing.
        if (PosterUsage.WhyCannotSend(poster, "sent in a campaign") is { } cannotSend)
            return Result<CampaignDto>.Invalid(cannotSend);

        // Fetch + validate any attached videos. Only Ready videos are usable
        // — Pending/Rendering would produce dead links; Failed ones have no blob.
        var videoEntities = new List<Video>();
        if (request.VideoIds is { Count: > 0 })
        {
            foreach (var vid in request.VideoIds.Distinct())
            {
                var v = await videos.FindByIdAsync(vid, ct);
                if (v is null || v.OwnerId != ownerId)
                    return Result<CampaignDto>.NotFound($"Video {vid} not found.");
                if (v.Status != VideoStatus.Ready || string.IsNullOrWhiteSpace(v.BlobKey))
                    return Result<CampaignDto>.Invalid(
                        $"Video '{v.Name}' is not ready yet. Wait for the render to finish or pick another.");
                videoEntities.Add(v);
            }
        }

        // Expand patient/group sources, then validate recipients per channel.
        var manual = request.Recipients ?? Array.Empty<string>();
        var resolved = await ResolveAudienceContactsAsync(
            ownerId,
            request.Channel,
            request.PatientIds,
            request.GroupIds,
            ct);

        var recipients = NormalizeRecipients(
            manual.Concat(resolved).ToList(),
            request.Channel,
            out var rejectedReason,
            dropInvalid: resolved.Count > 0);
        if (rejectedReason is not null)
            return Result<CampaignDto>.Invalid(rejectedReason);
        if (recipients.Count == 0)
            return Result<CampaignDto>.Invalid("No valid recipients.");

        // People who unsubscribed from this pharmacy are left out before the
        // campaign exists (R2-06), so its counts and plan usage are honest.
        // The send checks again, for anyone who unsubscribes in between.
        var unsubscribed = 0;
        if (request.Channel == CampaignChannel.Email)
        {
            var suppressed = await suppressions.FindSuppressedAsync(ownerId, recipients, ct);
            if (suppressed.Count > 0)
            {
                var before = recipients.Count;
                recipients = recipients.Where(r => !suppressed.Contains(r)).ToList();
                unsubscribed = before - recipients.Count;
            }
            if (recipients.Count == 0)
                return Result<CampaignDto>.Invalid(
                    "Everyone on this list has unsubscribed from your emails, so there is no one to send to.");
        }

        if (recipients.Count > MaxRecipientsPerCampaign)
        {
            return Result<CampaignDto>.Invalid(
                $"A single campaign can go to at most {MaxRecipientsPerCampaign:N0} people. "
                + $"This one has {recipients.Count:N0}. Split it into more than one campaign, "
                + "or narrow the group.");
        }

        // Tier limit
        var plan = Plans.For(user.Tier);
        if (plan.MaxCampaignRecipientsPerMonth != Plans.Unlimited)
        {
            if (plan.MaxCampaignRecipientsPerMonth == 0)
                return Result<CampaignDto>.Forbidden("Campaigns are not available on the Free plan. Upgrade to send.");

            var alreadySent = await campaigns.CountRecipientsThisMonthAsync(ownerId, ct);
            if (alreadySent + recipients.Count > plan.MaxCampaignRecipientsPerMonth)
                return Result<CampaignDto>.Forbidden(
                    $"This send would exceed your {plan.DisplayName} plan limit of {plan.MaxCampaignRecipientsPerMonth} recipients/month. Used: {alreadySent}.");
        }

        Campaign campaign;
        try
        {
            campaign = Campaign.Create(
                ownerId, poster.Id, request.Name, request.Channel, recipients,
                videoIds: videoEntities.Select(v => v.Id));
        }
        catch (DomainException ex) { return Result<CampaignDto>.Invalid(ex.Message); }

        // Queued, not sent. The dispatch worker picks it up within a second or
        // two and this returns immediately.
        //
        // It used to send inline, so the HTTP request stayed open until the last
        // recipient had been handed to the provider. At four recipients that was
        // a few seconds. At a thousand it is minutes of a held connection, a
        // blocked request thread and a spinner with no progress — and Azure App
        // Service cuts an idle request at 230 seconds regardless of how patient
        // the browser is.
        campaign.Queue();
        await campaigns.AddAsync(campaign, ct);

        audit.SetEntity("campaign", campaign.Id);
        audit.AddMeta("recipientCount", recipients.Count);
        if (unsubscribed > 0) audit.AddMeta("unsubscribedLeftOut", unsubscribed);
        audit.AddMeta("channel", request.Channel.ToString());
        audit.AddMeta("videoCount", videoEntities.Count);

        logger.LogInformation(
            "Campaign queued id={CampaignId} owner={OwnerId} name='{Name}' recipients={Count} videos={Videos}",
            campaign.Id, campaign.OwnerId, campaign.Name, recipients.Count, videoEntities.Count);

        return Result<CampaignDto>.Success(ToDto(campaign));
    }

    /// <summary>Retry a previously failed (or partially failed) campaign.</summary>
    public async Task<Result<CampaignDto>> RetryAsync(string id, string ownerId, CancellationToken ct)
    {
        var campaign = await campaigns.FindByIdAsync(id, ct);
        if (campaign is null || campaign.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Campaign not found.");
        // Failed, or Sent with recipients still outstanding.
        //
        // This used to accept `Failed` only, which quietly excluded the case
        // the delivery report exists for: a campaign that reached 700 of 1000
        // is `Sent`, and those 300 could never be retried. It also excluded
        // anything stranded in `Sending`, which before the dispatch worker was
        // every campaign interrupted by a deploy.
        if (campaign.Status is CampaignStatus.Draft or CampaignStatus.Stopped)
            return Result<CampaignDto>.Invalid(
                $"A {campaign.Status.ToString().ToLowerInvariant()} campaign cannot be retried.");

        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<CampaignDto>.NotFound("User not found.");

        var poster = await posters.FindByIdAsync(campaign.PosterId, ct);
        if (poster is null || poster.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Poster no longer available.");

        // Checked again on retry, against the poster's status *now*.
        //
        // A retry is a fresh send, and the poster may have been unpublished or
        // archived since the original attempt — quite likely, in fact, because
        // the campaign failed and somebody went back to work on the poster. The
        // campaign's own history is untouched either way; this only refuses to
        // send it again.
        if (PosterUsage.WhyCannotSend(poster, "sent again") is { } cannotResend)
            return Result<CampaignDto>.Invalid(cannotResend);

        // Only the recipients who have not already received it.
        //
        // <b>This was the bug.</b> Retry dispatched to campaign.Recipients — the
        // whole original list — while the per-recipient delivery report sat
        // beside it, written and never read. A campaign that reached 700 of
        // 1000 and failed would, on Retry, send to all 1000 again: 700 patients
        // receiving the same clinical marketing twice, from a pharmacy with no
        // way to know it had happened.
        //
        // It was invisible at four recipients, where one request either works
        // or does not, and it becomes the normal case at a thousand, where a
        // long send is far likelier to fail partway.
        var outstanding = campaign.Recipients.Count - campaign.DeliveredCount;
        if (outstanding <= 0)
            return Result<CampaignDto>.Invalid("Every recipient has already received this campaign.");

        // The quota covers what this retry will actually send, not the size of
        // the original campaign. Charging a pharmacy again for the 700 that
        // already went out would be wrong twice over.
        var plan = Plans.For(user.Tier);
        if (plan.MaxCampaignRecipientsPerMonth != Plans.Unlimited)
        {
            var alreadySent = await campaigns.CountRecipientsThisMonthAsync(ownerId, ct);
            if (alreadySent + outstanding > plan.MaxCampaignRecipientsPerMonth)
                return Result<CampaignDto>.Forbidden(
                    $"Retrying would exceed your {plan.DisplayName} plan limit of {plan.MaxCampaignRecipientsPerMonth} recipients/month.");
        }

        // Queue() keeps every Sent entry and returns the rest to Pending, so the
        // worker resumes rather than restarts.
        campaign.Queue();
        await campaigns.UpdateAsync(campaign, ct);

        audit.AddMeta("recipientCount", outstanding);

        logger.LogInformation(
            "Campaign {CampaignId} re-queued: {Outstanding} of {Total} recipient(s) still to send",
            campaign.Id, outstanding, campaign.Recipients.Count);

        return Result<CampaignDto>.Success(ToDto(campaign));
    }

    /// <summary>
    /// The name a patient sees this email came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three places in every campaign email carry it: the From name, the
    /// subject, and the "Sent by" footer. It has to be the pharmacy.
    /// </para>
    /// <para>
    /// <b>Never the account holder's own name.</b> <c>PharmacyDisplayName</c>
    /// falls back to <c>DisplayName</c>, which is a person — fine in an admin
    /// list, wrong on mail to a patient, who has no idea who that is and every
    /// reason to treat it as spam. An account that skipped onboarding has an
    /// empty <c>Profile.PharmacyName</c>, so that fallback was not theoretical:
    /// it is what actually went out.
    /// </para>
    /// <para>
    /// The brand kit sits in between. It is the other screen that asks for a
    /// pharmacy name, and somebody who filled it in has plainly told us what
    /// their pharmacy is called — the email had simply never looked. Reading it
    /// here is what makes the two screens agree.
    /// </para>
    /// <para>
    /// A brand-kit read failure is not worth failing a send over, so it falls
    /// through to the generic name instead.
    /// </para>
    /// </remarks>
    private async Task<string> ResolveSenderNameAsync(
        Domain.Users.User user, CancellationToken ct)
    {
        // The order this method used to spell out by hand now lives in
        // PharmacyBrand.Resolve, alongside the identical order posters and video
        // need. It was the same rule written out in three places, and the three
        // had already drifted: this one read the brand kit, posters never did,
        // and video was specified to prefer it.
        Domain.BrandKit.BrandKit? kit = null;
        try
        {
            kit = await brandKits.FindByOwnerAsync(user.Id, ct);
        }
        catch (Exception ex)
        {
            // Not worth failing a send over. Resolution carries on with the
            // profile alone, which is where the name usually is anyway.
            logger.LogWarning(ex,
                "Could not read the brand kit for {OwnerId} while naming the sender", user.Id);
        }

        var brand = Domain.BrandKit.PharmacyBrand.Resolve(user.Profile, kit);

        if (!brand.IsUsable)
        {
            // Deliberately generic rather than personal. "Pharmacy" tells a
            // patient little; the account holder's name would actively mislead
            // them, and that is what used to go out.
            logger.LogWarning(
                "Campaign for {OwnerId} is going out as \"{Name}\" — neither the profile "
                + "nor the brand kit has a pharmacy name", user.Id, brand.Name);
        }
        else if (string.IsNullOrWhiteSpace(user.Profile.PharmacyName))
        {
            logger.LogInformation(
                "Campaign sender name for {OwnerId} came from the brand kit — "
                + "the pharmacy profile has no name", user.Id);
        }

        return brand.Name;
    }

    /// <summary>
    /// Most people one campaign may go to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberate ceiling on a single click, separate from the plan's monthly
    /// allowance. Pro sells 5,000 recipients a month; before this, all 5,000
    /// could leave in one action with nothing between the pharmacist and the
    /// send but a button.
    /// </para>
    /// <para>
    /// The number is a product judgement rather than a technical one — dispatch
    /// is a background worker now and would manage more. It is here because a
    /// mistake at this scale reaches patients and cannot be recalled, and
    /// splitting a very large send into deliberate pieces is a reasonable price.
    /// The interface asks for confirmation above 200 for the same reason.
    /// </para>
    /// </remarks>
    public const int MaxRecipientsPerCampaign = 1_000;

    /// <summary>Above this, the interface asks the sender to confirm.</summary>
    public const int ConfirmAboveRecipients = 200;

    /// <summary>
    /// Sends a campaign the worker has claimed. Not reachable from the API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything it needs is re-read here rather than carried from the request
    /// that queued it. The campaign may have been queued minutes ago, by a
    /// process that has since been replaced, and the poster or the attached
    /// videos may have changed in between.
    /// </para>
    /// <para>
    /// The poster is checked again for the same reason it is checked on retry: a
    /// campaign queued against a published poster that has since been
    /// unpublished must not go out.
    /// </para>
    /// </remarks>
    public async Task DispatchClaimedAsync(Campaign campaign, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(campaign.OwnerId, ct);
        if (user is null)
        {
            campaign.MarkFailed("The account that created this campaign no longer exists.");
            await campaigns.UpdateAsync(campaign, ct);
            return;
        }

        var poster = await posters.FindByIdAsync(campaign.PosterId, ct);
        if (poster is null || poster.OwnerId != campaign.OwnerId)
        {
            campaign.MarkFailed("The poster is no longer available.");
            await campaigns.UpdateAsync(campaign, ct);
            return;
        }

        if (PosterUsage.WhyCannotSend(poster, "sent") is { } cannotSend)
        {
            campaign.MarkFailed(cannotSend);
            await campaigns.UpdateAsync(campaign, ct);
            return;
        }

        var videoEntities = new List<Video>();
        foreach (var vid in campaign.VideoIds)
        {
            var v = await videos.FindByIdAsync(vid, ct);
            if (v is not null && v.OwnerId == campaign.OwnerId && v.Status == VideoStatus.Ready)
                videoEntities.Add(v);
        }

        // PendingRecipients, not Recipients. On a resumed or retried campaign
        // the difference is who has already been emailed.
        await DispatchAndPersistAsync(
            campaign, user, poster, videoEntities,
            campaign.PendingRecipients, campaign.Channel, ct);
    }

    /// <summary>
    /// How many recipients go into one call to the email provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the resume granularity, not a performance knob. Results are
    /// written to the campaign after every group, so a crash loses at most one
    /// group's worth of certainty about who was reached — and those recipients
    /// stay <c>Pending</c>, so they are re-sent rather than skipped.
    /// </para>
    /// <para>
    /// It is also as far as per-recipient truth goes. <c>IEmailSender</c>
    /// reports counts, not which addresses succeeded, so within a group that
    /// partially fails the report is an approximation. Twenty keeps that
    /// approximation small; the alternative — the whole campaign in one call —
    /// made it meaningless, because "700 of 1000 sent" said nothing about
    /// <i>which</i> 700.
    /// </para>
    /// </remarks>
    private const int RecipientsPerGroup = 20;

    /// <summary>
    /// Sends to each group in turn, recording the outcome as it goes, and stops
    /// early if the campaign is stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The campaign is saved after every group. That is the whole reason this
    /// method exists: the previous version made one call for every recipient and
    /// wrote the result once at the end, so anything that interrupted it — a
    /// deploy, a timeout, a killed process — lost the entire record of what had
    /// already been delivered.
    /// </para>
    /// <para>
    /// <b>The save is what checks for a Stop</b>, in the same operation, which is
    /// why it is <see cref="ICampaignRepository.SaveProgressAsync"/> and not
    /// <c>UpdateAsync</c>. Two things were wrong before. The status was never
    /// re-read, so a Stop written while a send was running was never noticed;
    /// and <c>UpdateAsync</c> replaces the document from this in-memory copy,
    /// which still said <c>Sending</c>, so the Stop was erased by the very next
    /// group. The pharmacist got a button that acknowledged the click, briefly
    /// showed Stopped, and then sent to everyone anyway.
    /// </para>
    /// </remarks>
    private async Task<CampaignSendOutcome> SendInGroupsAsync(
        Campaign campaign,
        EmailMessage template,
        IReadOnlyList<string> recipients,
        CancellationToken ct)
    {
        var sent = 0;
        var failed = 0;
        var unsubscribedAtSend = 0;
        string? firstNote = null;

        for (var i = 0; i < recipients.Count; i += RecipientsPerGroup)
        {
            ct.ThrowIfCancellationRequested();

            var group = recipients.Skip(i).Take(RecipientsPerGroup).ToList();

            // Checked again at send: someone may have unsubscribed since the
            // campaign was created, or between its groups (R2-06).
            var suppressed = await suppressions.FindSuppressedAsync(campaign.OwnerId, group, ct);
            var toSend = group.Where(a => !suppressed.Contains(a)).ToList();
            var leftOut = group.Where(a => suppressed.Contains(a)).ToList();

            var result = toSend.Count == 0
                ? new EmailDispatchResult(0, 0, null)
                : await email.SendAsync(template with
                {
                    To = toSend,
                    Unsubscribe = toSend
                        .Select(a => a.Trim().ToLowerInvariant())
                        .Distinct()
                        .ToDictionary(a => a, a => LinksFor(campaign.OwnerId, a)),
                }, ct);

            sent += result.Sent;
            failed += result.Failed;
            unsubscribedAtSend += leftOut.Count;
            firstNote ??= result.Note;

            var now = DateTime.UtcNow;
            campaign.RecordResults(toSend.Select((address, index) => new RecipientResult
            {
                Recipient = address,
                // Within a group the provider gives counts rather than
                // addresses. A group that wholly succeeded or wholly failed —
                // which is the overwhelming majority — is recorded exactly.
                Status = index < result.Sent ? RecipientStatus.Sent : RecipientStatus.Failed,
                Reason = index < result.Sent ? null : (result.Note ?? "Delivery failed"),
                AtUtc = now,
            }).Concat(leftOut.Select(address => new RecipientResult
            {
                Recipient = address,
                Status = RecipientStatus.Failed,
                Reason = UnsubscribedReason,
                AtUtc = now,
            })));

            // Renewed per group, so a slow send does not lose its claim
            // half way and get picked up by a second worker.
            campaign.RenewLease(DateTime.UtcNow.Add(LeaseDuration));

            var status = await campaigns.SaveProgressAsync(campaign, ct);

            if (recipients.Count > RecipientsPerGroup)
            {
                logger.LogInformation(
                    "Campaign {CampaignId}: {Done}/{Total} recipients processed",
                    campaign.Id, Math.Min(i + RecipientsPerGroup, recipients.Count),
                    recipients.Count);
            }

            // Checked after the write, never before it: whatever this group did
            // is recorded first, so the recipients already reached are not sent
            // to again if the campaign is later resumed.
            //
            // This read is also the check for the *next* group — nothing happens
            // between here and its send. So the guarantee is one group, not
            // instant: a Stop written after this read lands while the next group
            // is already with the provider, and at twenty per group that is at
            // most twenty more people. Closing that window means holding a
            // transaction across a network call to Mailjet, which is a worse
            // trade than twenty.
            // Anything but Sending, and this worker no longer owns the send.
            //
            // Stopped is the case this was built for. The others are worth the
            // same treatment rather than a special case each: null means the
            // campaign was deleted underneath us, and any other status means
            // somebody or something else has taken it over — a lapsed lease
            // reclaimed by a second instance, most plausibly — where carrying on
            // would mean two workers sending the same campaign.
            if (status is not CampaignStatus.Sending)
            {
                logger.LogInformation(
                    "Campaign {CampaignId} left this worker after {Done} of {Total} recipient(s); "
                    + "it is now {Status}",
                    campaign.Id, Math.Min(i + RecipientsPerGroup, recipients.Count),
                    recipients.Count, status?.ToString() ?? "gone");

                return new CampaignSendOutcome(sent, failed, WithUnsubscribed(firstNote, unsubscribedAtSend), Stopped: true);
            }
        }

        return new CampaignSendOutcome(sent, failed, WithUnsubscribed(firstNote, unsubscribedAtSend), Stopped: false);
    }

    /// <summary>Why a recipient on the list was not sent to.</summary>
    public const string UnsubscribedReason = "Unsubscribed — not sent";

    private static string? WithUnsubscribed(string? note, int count) =>
        count == 0 ? note
        : $"{count} recipient(s) had unsubscribed and were not sent to. {note}".Trim();

    /// <summary>
    /// One recipient's unsubscribe links (R2-06): the page in the web app, and
    /// the API address mail clients POST to for one-click unsubscribe.
    /// </summary>
    private UnsubscribeLinks LinksFor(string ownerId, string email)
    {
        var token = Uri.EscapeDataString(UnsubscribeToken.Sign(ownerId, email));
        return new UnsubscribeLinks(
            PageUrl: links.BuildAppLink($"/unsubscribe?token={token}"),
            OneClickUrl: $"{PublicApiBaseUrl}/api/unsubscribe?token={token}");
    }

    /// <param name="Stopped">
    /// The send ended early because the campaign is no longer this worker's to
    /// send. The caller must not <c>Complete()</c> it: whatever the database
    /// says now is truer than what this worker was about to write. In the case
    /// that matters, doing so would relabel a campaign the pharmacist stopped as
    /// <c>Sent</c> — false, and the exact thing they pressed the button to
    /// prevent.
    /// </param>
    private sealed record CampaignSendOutcome(
        int Sent, int Failed, string? Note, bool Stopped);

    /// <summary>
    /// How long a worker's claim lasts before another may take the campaign.
    /// </summary>
    /// <remarks>
    /// Long enough that a slow group does not lose the lease, short enough that
    /// a killed worker's campaign is not stranded for long. Renewed after every
    /// group, so the only way it lapses is the process actually stopping.
    /// </remarks>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    private async Task DispatchAndPersistAsync(
        Campaign campaign,
        Domain.Users.User user,
        Domain.Posters.Poster poster,
        IReadOnlyList<Video> videosToAttach,
        IReadOnlyList<string> recipients,
        CampaignChannel channel,
        CancellationToken ct)
    {
        try
        {
            CampaignSendOutcome? emailResult = null;

            if (channel == CampaignChannel.Email)
            {
                // The design, as an address rather than as bytes.
                //
                // It used to be read here and embedded in the message. Mailjet's
                // Send v3.1 puts an inline attachment inside *each*
                // personalisation, so the poster travelled once per recipient:
                // 977,045 bytes for four recipients in the failure that led to
                // this, 244 KB each. A thousand recipients is a quarter of a
                // gigabyte of upload, and four recipients was eighty seconds —
                // long enough for the provider to close the connection
                // mid-upload, which is what it did.
                //
                // Rendering still happens here, and is still cached by canvas
                // content, so resending does not rasterise twice. What changed
                // is that only the blob key leaves this method, signed. The
                // recipient's mail client fetches the image, and the request to
                // Mailjet carries a few kilobytes of HTML instead.
                //
                // A failure does not stop the send. A campaign that goes out
                // with the poster missing is a bad email; one that does not go
                // out is a bad day, and the recipients still get the message and
                // the video links.
                CampaignEmail.CampaignPosterImage? posterImage = null;
                byte[]? embeddedPoster = null;
                var embeddedPosterMime = "image/png";

                try
                {
                    var rendered = await posterImages.EnsureRenderedAsync(poster.Id, user.Id, ct);
                    if (rendered.Kind == ResultKind.Success)
                    {
                        var blobKey = rendered.Value!.BlobKey;

                        if (ImagesTravelAsLinks)
                        {
                            var token = PosterImageToken.SignForEmail(blobKey);
                            var url = $"{PublicApiBaseUrl}/api/posters/email-image?token={Uri.EscapeDataString(token)}";
                            posterImage = new CampaignEmail.CampaignPosterImage(url, url);
                        }
                        else
                        {
                            // Nobody could fetch the link, so the design goes in
                            // the message instead. See ImagesTravelAsLinks.
                            await using var img = await posterImageStore.OpenReadAsync(blobKey, ct);
                            using var ms = new MemoryStream();
                            await img.CopyToAsync(ms, ct);
                            embeddedPoster = ms.ToArray();

                            // Null means the re-encode did not help or could not
                            // run, and the original goes as it is — a poster that
                            // is too large beats no poster.
                            var smaller = imageTranscoder.ToEmailJpeg(embeddedPoster);
                            if (smaller is not null)
                            {
                                embeddedPoster = smaller;
                                embeddedPosterMime = "image/jpeg";
                            }

                            posterImage = new CampaignEmail.CampaignPosterImage(
                                $"cid:{CampaignEmail.PosterContentId}", LinkUrl: null);

                            logger.LogWarning(
                                "Campaign {CampaignId}: Public:ApiBaseUrl is '{BaseUrl}', which no mail "
                                + "provider can fetch from, so the poster ({Bytes:N0} bytes) is being "
                                + "embedded in every recipient's copy instead of linked. Fine for a test "
                                + "send; at scale this is what makes a campaign time out mid-upload. Set "
                                + "Public__ApiBaseUrl to a publicly reachable address.",
                                campaign.Id, PublicApiBaseUrl, embeddedPoster.Length);
                        }
                    }
                    else
                    {
                        logger.LogWarning(
                            "Could not render poster {PosterId} for campaign {CampaignId}: {Error}",
                            poster.Id, campaign.Id, rendered.Error);
                    }
                }
                catch (Exception exc)
                {
                    // Both cleared together. Half a poster is worse than none:
                    // a `cid:` reference with no attachment behind it is a
                    // broken-image icon, and an attachment nothing references is
                    // bytes on every recipient's copy that nobody ever sees.
                    posterImage = null;
                    embeddedPoster = null;

                    logger.LogWarning(exc,
                        "Could not prepare the poster image for campaign {CampaignId} — " +
                        "the email will go out without the design",
                        campaign.Id);
                }

                // For each video: build a public-download URL against our own
                // API using an HMAC-signed token (48h). Recipients follow the
                // link from their inbox — no auth cookie — but the token binds
                // them to just this video for just this window. Direct MP4
                // attachments would blow past Mailjet's 15MB per-message
                // ceiling for anything longer than ~30s.
                var videoLinks = new List<VideoEmailLink>();
                var apiBase = PublicApiBaseUrl;
                foreach (var v in videosToAttach)
                {
                    if (string.IsNullOrWhiteSpace(v.BlobKey)) continue;
                    var videoToken = VideoService.BuildPublicToken(v.Id, thumbnail: false, TimeSpan.FromHours(48));
                    var videoUrl = $"{apiBase}/api/videos/{v.Id}/public-download?token={videoToken}";

                    // The still that fronts the link.
                    //
                    // <b>Linked when the internet can reach us, embedded only
                    // when it cannot.</b> This used to be a base64 data URL
                    // unconditionally, on the reasoning that "data URLs work
                    // everywhere with zero fetch". They do not: <b>Gmail strips
                    // `data:` from `img src` outright</b> and renders nothing.
                    // So a campaign carrying a poster and a video arrived with
                    // the poster visible and an empty space above the watch
                    // button — the poster had been moved to a signed link and
                    // the thumbnail was left behind.
                    //
                    // The two now answer the same question the same way, which
                    // is the point: it is one question — can a mail provider's
                    // fetcher reach this API? — and it was being answered twice,
                    // differently, forty lines apart.
                    var hasStill = !string.IsNullOrWhiteSpace(v.ThumbnailBlobKey);
                    var thumbSrc = !hasStill ? null : await ChooseThumbnailSrcAsync(
                        ImagesTravelAsLinks,
                        apiBase,
                        v.Id,
                        // A much longer window than the 48 hours the MP4 gets.
                        // The clip is the valuable thing and is meant to expire;
                        // the still is a picture in an email somebody may open
                        // next month, and an image that vanishes turns a fine
                        // email into a broken-looking one. Its own token, too:
                        // the signature binds the thumbnail flag, so the video's
                        // token will not open the still.
                        signThumbnailToken: () => VideoService.BuildPublicToken(
                            v.Id, thumbnail: true, PosterImageToken.EmailLinkValidity),
                        loadEmbedded: async () =>
                        {
                            // Only reached on a machine the internet cannot
                            // resolve. Gmail cannot fetch that either way, and
                            // the desktop clients that can reach localhost do
                            // render data URLs.
                            try
                            {
                                var blob = await videoStorage.OpenReadAsync(v.ThumbnailBlobKey!, ct);
                                if (blob is null) return null;

                                using var ms = new MemoryStream();
                                await blob.Content.CopyToAsync(ms, ct);
                                return $"data:{blob.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
                            }
                            catch (Exception exc)
                            {
                                logger.LogWarning(exc,
                                    "Failed to fetch thumbnail for video {VideoId} — email will render without preview",
                                    v.Id);
                                return null;
                            }
                        });

                    videoLinks.Add(new VideoEmailLink(v.Name, videoUrl, thumbSrc));
                }

                var pharmacyName = await ResolveSenderNameAsync(user, ct);

                // Built once. The poster, the video links and the pharmacy name
                // are the same for every recipient, and rebuilding them per
                // group would make a thousand-recipient campaign a thousand
                // renders.
                //
                // Normally nothing binary goes in, and that is the line that
                // keeps a campaign's request size flat as the audience grows —
                // the poster used to travel as a PDF nobody opened, then as an
                // inline image copied into every personalisation. The transcode
                // moved to the endpoint that serves the image, so a reader
                // downloads a 600px JPEG rather than a 1240px print render, once
                // per reader who looks instead of once per recipient regardless.
                //
                // The exception is a deployment whose API has no address the
                // outside world can reach, where a link would reach nobody and
                // the poster is embedded instead.
                var template = new EmailMessage(
                    FromAddress: string.Empty,
                    FromName: pharmacyName,
                    To: Array.Empty<string>(),
                    Subject: $"{campaign.Name} · {pharmacyName}",
                    HtmlBody: CampaignEmail.BuildHtml(
                        campaign.Name, poster.Name, pharmacyName,
                        posterImage, videoLinks, UnsubscribeLinks.Placeholder),
                    TextBody: CampaignEmail.BuildText(
                        campaign.Name, poster.Name, pharmacyName,
                        posterImage?.LinkUrl, videoLinks, UnsubscribeLinks.Placeholder),
                    // The poster no longer travels as a PDF either way.
                    Attachments: Array.Empty<EmailAttachment>(),
                    InlineImages: embeddedPoster is null
                        ? Array.Empty<EmailInlineImage>()
                        : new[]
                        {
                            new EmailInlineImage(
                                CampaignEmail.PosterContentId,
                                embeddedPosterMime,
                                embeddedPoster),
                        });

                emailResult = await SendInGroupsAsync(campaign, template, recipients, ct);
            }

            var sent = emailResult?.Sent ?? 0;
            var failed = emailResult?.Failed ?? 0;
            var note = emailResult?.Note;

            if (emailResult is { Stopped: true })
            {
                // Nothing more is written here, and that is the point. The last
                // group's results are already durable — SaveProgressAsync wrote
                // them, which is how the stop was noticed — and the status in
                // the database is the pharmacist's, not this worker's.
                //
                // Falling through would call Complete(), which sets Sent, and
                // then UpdateAsync at the end of this method would replace the
                // document with it. A campaign somebody deliberately stopped
                // would be filed as one that went out.
                logger.LogInformation(
                    "Campaign {CampaignId} halted: sent={Sent} failed={Failed} before the stop",
                    campaign.Id, sent, failed);
                return;
            }

            // No report is built here any more. SendInGroupsAsync has already
            // written one entry per recipient as it went, which is both more
            // accurate and the thing that survives a crash. Rebuilding it from
            // the totals would overwrite what actually happened with a guess.
            var combinedNote = failed > 0
                ? $"{failed} recipient(s) failed. {note}".Trim()
                : note;

            campaign.Complete(string.IsNullOrWhiteSpace(combinedNote) ? null : combinedNote);

            if (campaign.Status == CampaignStatus.Failed)
            {
                logger.LogWarning(
                    "Campaign {CampaignId} failed: sent=0 failed={Failed} note={Note}",
                    campaign.Id, failed, note);
            }
            else
            {
                logger.LogInformation(
                    "Campaign {CampaignId} dispatched: sent={Sent} failed={Failed} videos={Videos}",
                    campaign.Id, sent, failed, videosToAttach.Count);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not failure — and the difference decides whether the
            // campaign finishes.
            //
            // Rethrown so the worker's own handler deals with it. Falling into
            // the catch below would mark a deploy "A task was canceled" and set
            // the campaign to Failed, which is not claimable: it would sit there
            // until somebody noticed and pressed Retry, having sent to some
            // unknown fraction of the list. Left as it is, the lease lapses, the
            // next instance claims it, and it resumes from the delivery report.
            //
            // The UpdateAsync below would throw anyway. Its token is this one.
            logger.LogInformation(
                "Campaign {CampaignId} interrupted mid-send; it will resume when its lease lapses",
                campaign.Id);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Campaign {CampaignId} threw during dispatch", campaign.Id);
            campaign.MarkFailed(ex.Message);
        }

        await campaigns.UpdateAsync(campaign, ct);
    }


    /// <summary>Return an existing campaign that looks like a duplicate of
    /// the incoming request (same owner, same name, same poster, created in
    /// the last 60 seconds). Cheap in-memory scan — an owner rarely has more
    /// than a few dozen campaigns, and the same-window check bounds the
    /// scope. Real-key idempotency (Idempotency-Key header) would be the
    /// next step if this proves insufficient.</summary>
    private async Task<Campaign?> FindRecentDuplicateAsync(
        string ownerId, CreateCampaignRequest request, CancellationToken ct)
    {
        var all = await campaigns.ListByOwnerAsync(ownerId, ct);
        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        return all.FirstOrDefault(c =>
            c.CreatedAtUtc >= cutoff
            && c.PosterId == request.PosterId
            && string.Equals(c.Name.Trim(), (request.Name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<Result<CampaignDto>> StopAsync(string id, string ownerId, CancellationToken ct)
    {
        var campaign = await campaigns.FindByIdAsync(id, ct);
        if (campaign is null || campaign.OwnerId != ownerId)
            return Result<CampaignDto>.NotFound("Campaign not found.");

        try { campaign.Stop(); }
        catch (DomainException ex) { return Result<CampaignDto>.Invalid(ex.Message); }

        await campaigns.UpdateAsync(campaign, ct);
        return Result<CampaignDto>.Success(ToDto(campaign));
    }

    private static IReadOnlyList<string> NormalizeRecipients(
        IReadOnlyList<string> input,
        CampaignChannel channel,
        out string? rejectedReason,
        bool dropInvalid = false)
    {
        var trimmed = input
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (channel == CampaignChannel.Email)
        {
            if (dropInvalid)
            {
                trimmed = trimmed.Where(r => EmailRegex.IsMatch(r)).ToList();
            }
            else
            {
                var bad = trimmed.Where(r => !EmailRegex.IsMatch(r)).Take(3).ToList();
                if (bad.Count > 0)
                {
                    rejectedReason = $"Invalid email(s): {string.Join(", ", bad)}";
                    return Array.Empty<string>();
                }
            }
        }
        else
        {
            // Very light phone normalisation — strip whitespace, keep +/digits.
            trimmed = trimmed
                .Select(r => new string(r.Where(c => c == '+' || char.IsDigit(c)).ToArray()))
                .Where(r => r.Length >= 7)
                .ToList();
        }

        rejectedReason = null;
        return trimmed;
    }

    private async Task<List<string>> ResolveAudienceContactsAsync(
        string ownerId,
        CampaignChannel channel,
        IReadOnlyList<string>? patientIds,
        IReadOnlyList<string>? groupIds,
        CancellationToken ct)
    {
        var contacts = new List<string>();

        if (patientIds is { Count: > 0 })
        {
            var picked = await patients.FindManyByIdsAsync(ownerId, patientIds, ct);
            contacts.AddRange(picked.Select(p => SelectContact(p, channel)).Where(c => c is not null)!);
        }

        if (groupIds is { Count: > 0 })
        {
            foreach (var gid in groupIds.Distinct())
            {
                var group = await patientGroups.FindByIdAsync(gid, ct);

                // AllowsMembershipFrom, not ownership. A global group is
                // selectable by every pharmacy, and an ownership test here
                // would skip it silently — the campaign would report a healthy
                // send to nobody, which is the worst possible failure for this
                // feature because it looks like success.
                if (group is null || !group.AllowsMembershipFrom(ownerId)) continue;

                // Still owner-scoped: this returns only THIS pharmacy's
                // patients, so a shared group never leaks members between
                // pharmacies.
                var members = await patients.ListByGroupAsync(ownerId, gid, ct);
                contacts.AddRange(members.Select(p => SelectContact(p, channel)).Where(c => c is not null)!);
            }
        }

        return contacts;
    }

    private static string? SelectContact(Patient p, CampaignChannel channel) =>
        channel == CampaignChannel.Email ? p.Email : p.Phone;

    public async Task<Result<CampaignAudiencePreview>> PreviewAudienceAsync(
        string ownerId,
        CampaignAudienceRequest request,
        CancellationToken ct)
    {
        var manual = request.Recipients ?? Array.Empty<string>();
        var resolved = await ResolveAudienceContactsAsync(
            ownerId, request.Channel, request.PatientIds, request.GroupIds, ct);

        var manualNormalized = NormalizeRecipients(manual, request.Channel, out _, dropInvalid: false);
        var resolvedNormalized = NormalizeRecipients(resolved, request.Channel, out _, dropInvalid: true);

        var combined = manualNormalized
            .Concat(resolvedNormalized)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The same people the send will leave out (R2-06), so the number the
        // pharmacist confirms is the number that will be sent to.
        var unsubscribed = 0;
        if (request.Channel == CampaignChannel.Email && combined.Count > 0)
        {
            var suppressed = await suppressions.FindSuppressedAsync(ownerId, combined, ct);
            unsubscribed = combined.Count(c => suppressed.Contains(c));
            if (unsubscribed > 0) combined = combined.Where(c => !suppressed.Contains(c)).ToList();
        }

        var invalidFromResolved = resolved.Count - resolvedNormalized.Count;
        var sample = combined.Take(5).ToList();

        return Result<CampaignAudiencePreview>.Success(new CampaignAudiencePreview(
            TotalUnique: combined.Count,
            FromManual: manualNormalized.Count,
            FromPatients: request.PatientIds?.Count ?? 0,
            FromGroups: request.GroupIds?.Count ?? 0,
            InvalidCount: invalidFromResolved,
            SampleRecipients: sample,
            UnsubscribedCount: unsubscribed));
    }


    private static CampaignDto ToDto(Campaign c) => new(
        c.Id,
        c.OwnerId,
        c.PosterId,
        c.VideoIds,
        c.Name,
        c.Channel,
        c.Status,
        c.Recipients.Count,
        c.SentCount,
        c.SentAtUtc,
        c.Note,
        c.DeliveryReport
            .Select(r => new RecipientResultDto(r.Recipient, r.Status, r.Reason, r.AtUtc))
            .ToList(),
        c.CreatedAtUtc);
}
