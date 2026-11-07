export interface UsageBlock {
  postersThisMonth: number;
  postersTotal: number;
  videosThisMonth: number;
  videosTotal: number;
  campaignsThisMonth: number;
  campaignsTotal: number;
  recipientsThisMonth: number;
  patientsTotal: number;
}

export interface EngagementBlock {
  emailsSent: number;
  openRate: number;   // 0..1
  clickRate: number;  // 0..1
  videosViewed: number;
}

export interface TopItem {
  id: string;
  name: string;
  score: number;
  kind: "poster" | "video";
}

export interface TimeseriesPoint {
  label: string;
  count: number;
}

export interface AnalyticsOverview {
  usage: UsageBlock;
  engagement: EngagementBlock;
  topPosters: TopItem[];
  topVideos: TopItem[];
  campaignsSeries: TimeseriesPoint[];
}
