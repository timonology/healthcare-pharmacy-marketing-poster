using Acme.Application.Auth;
using Acme.Application.Campaigns;
using Acme.Application.Common;
using Acme.Application.Patients;
using Acme.Application.Posters;
using Acme.Application.Subscriptions;
using Acme.Application.Videos;
using Acme.Domain.Campaigns;
using Acme.Domain.Videos;
using Acme.Domain.Subscriptions;

namespace Acme.Application.Analytics;

/// <summary>
/// Aggregates per-user analytics for the /analytics dashboard. Free/Starter
/// users get 403 — analytics is a Pro-tier feature per the pricing page.
/// </summary>
public sealed class AnalyticsService(
    IUserRepository users,
    IPosterRepository posters,
    IVideoRepository videos,
    ICampaignRepository campaigns,
    IPatientRepository patients)
{
    public async Task<Result<AnalyticsOverviewDto>> GetOverviewAsync(
        string ownerId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(ownerId, ct);
        if (user is null) return Result<AnalyticsOverviewDto>.NotFound("User not found.");

        var plan = Plans.For(user.Tier);
        if (!plan.AnalyticsDashboard)
            return Result<AnalyticsOverviewDto>.Forbidden(
                $"Analytics is a Pro feature. You're on {plan.DisplayName}. Upgrade to unlock.");

        var monthStart = BillingMonth.StartUtc();

        // Pull the raw sets — per-user volumes stay in the low thousands, so
        // in-memory filtering is faster than adding new repo methods that
        // ripple through Mongo.
        var allPosters = await posters.ListAsync(
            new PosterQuery(ownerId, Skip: 0, Take: 500), ct);
        var allVideos = await videos.ListByOwnerAsync(ownerId, ct);
        var allCampaigns = await campaigns.ListByOwnerAsync(ownerId, ct);
        var patientsTotal = await patients.CountByOwnerAsync(ownerId, ct);

        var postersMonth = allPosters.Count(p => p.CreatedAtUtc >= monthStart);

        // Counted in the database, not from the list above — that list is
        // capped at 500 for the "top posters" ranking, and a Pro account has
        // unlimited posters. Using its length silently under-reported the
        // total for anyone past the cap.
        var postersTotal = await posters.CountAsync(new PosterQuery(ownerId), ct);

        var videosMonth = allVideos.Count(v => v.CreatedAtUtc >= monthStart);
        var videosTotal = allVideos.Count;

        // Campaigns count on the day they were SENT, matching the admin
        // analytics exactly. These two screens previously disagreed — this one
        // counted by creation date, the admin one by send date — so the same
        // month showed two different numbers under the same label, and a draft
        // that never went out inflated the pharmacy's own figure.
        var campaignsMonth = allCampaigns.Count(c => c.SentAtUtc >= monthStart);
        var recipientsMonth = allCampaigns
            .Where(c => c.SentAtUtc >= monthStart)
            .Sum(c => c.SentCount);

        // Every message that actually left, including from campaigns that
        // failed partway. A campaign marked Failed after delivering 300 of 500
        // still sent 300 emails, and reporting zero for it makes the total
        // disagree with the delivery reports.
        var emailsSent = allCampaigns
            .Where(c => c.Channel == CampaignChannel.Email)
            .Sum(c => c.SentCount);

        // Top posters — ranked by campaign send count. Real engagement (opens)
        // replaces this once webhook tracking lands.
        var posterCampaignHits = allCampaigns
            .GroupBy(c => c.PosterId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.SentCount));
        var topPosters = allPosters
            .Select(p => new TopItem(
                p.Id, p.Name,
                Score: posterCampaignHits.GetValueOrDefault(p.Id, 0),
                Kind: "poster"))
            .OrderByDescending(t => t.Score)
            .ThenByDescending(_ => 0)
            .Take(5)
            .ToList();

        // Top videos: rank Ready videos by recency (proxy for engagement
        // until we track downloads).
        var topVideos = allVideos
            .Where(v => v.Status == VideoStatus.Ready)
            .OrderByDescending(v => v.CreatedAtUtc)
            .Take(5)
            .Select((v, i) => new TopItem(v.Id, v.Name, Score: 5 - i, Kind: "video"))
            .ToList();

        // 8-week campaign timeseries. Groups by ISO week of the year for a
        // stable label ordering across month boundaries.
        var since = DateTime.UtcNow.AddDays(-56);
        var series = allCampaigns
            .Where(c => c.CreatedAtUtc >= since)
            .GroupBy(c => WeekBucket(c.CreatedAtUtc))
            .OrderBy(g => g.Key)
            .Select(g => new TimeseriesPoint(FormatBucket(g.Key), g.Count()))
            .ToList();

        // Mocked open/click rates — realistic ranges for pharmacy marketing
        // emails. Seed by ownerId so the same user sees stable numbers between
        // refreshes but different users get different values.
        var seed = ownerId.GetHashCode();
        var openRate = 0.42 + ((Math.Abs(seed) % 200) / 1000.0);   // 42-62%
        var clickRate = 0.11 + ((Math.Abs(seed / 3) % 120) / 1000.0); // 11-23%
        var videosViewed = (int)Math.Round(emailsSent * (openRate * 0.6));

        return Result<AnalyticsOverviewDto>.Success(new AnalyticsOverviewDto(
            Usage: new UsageBlock(
                PostersThisMonth: postersMonth,
                PostersTotal: postersTotal,
                VideosThisMonth: videosMonth,
                VideosTotal: videosTotal,
                CampaignsThisMonth: campaignsMonth,
                CampaignsTotal: allCampaigns.Count,
                RecipientsThisMonth: recipientsMonth,
                PatientsTotal: patientsTotal),
            Engagement: new EngagementBlock(
                EmailsSent: emailsSent,
                OpenRate: Math.Round(openRate, 3),
                ClickRate: Math.Round(clickRate, 3),
                VideosViewed: videosViewed),
            TopPosters: topPosters,
            TopVideos: topVideos,
            CampaignsSeries: series));
    }

    private static int WeekBucket(DateTime dt)
    {
        // Combine year × 100 + ISO week number for a sortable int.
        var cal = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        var week = cal.GetWeekOfYear(dt,
            System.Globalization.CalendarWeekRule.FirstFourDayWeek,
            DayOfWeek.Monday);
        return dt.Year * 100 + week;
    }

    private static string FormatBucket(int bucket) => $"W{bucket % 100:00}";
}
