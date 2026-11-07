namespace Acme.Application.Analytics;

/// <summary>Snapshot of a user's usage + engagement for the /analytics page.
/// Some fields (open/click rates) are currently mocked and will be replaced
/// with real event tracking once the messaging provider supports webhooks.</summary>
public sealed record AnalyticsOverviewDto(
    UsageBlock Usage,
    EngagementBlock Engagement,
    IReadOnlyList<TopItem> TopPosters,
    IReadOnlyList<TopItem> TopVideos,
    IReadOnlyList<TimeseriesPoint> CampaignsSeries);

public sealed record UsageBlock(
    int PostersThisMonth,
    int PostersTotal,
    int VideosThisMonth,
    int VideosTotal,
    int CampaignsThisMonth,
    int CampaignsTotal,
    int RecipientsThisMonth,
    int PatientsTotal);

/// <summary>Aggregate engagement rates. Real open/click tracking depends on
/// wiring Mailjet webhooks — for now these are stable per-user mocks derived
/// from the sent count so the dashboard visualises something realistic.</summary>
public sealed record EngagementBlock(
    int EmailsSent,
    double OpenRate,
    double ClickRate,
    int VideosViewed);

public sealed record TopItem(
    string Id,
    string Name,
    int Score,
    string Kind);   // "poster" | "video"

public sealed record TimeseriesPoint(
    string Label,
    int Count);
