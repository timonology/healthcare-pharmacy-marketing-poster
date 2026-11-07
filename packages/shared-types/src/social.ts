/**
 * Social publishing and Google Ads.
 *
 * Mirrors the C# DTOs. Two things are deliberate and load-bearing:
 *
 * 1. **No type here has a token field.** Credentials never leave the server.
 *    If something in the UI seems to need one, it needs a new endpoint.
 * 2. **Every instant is UTC.** The API takes and returns UTC; the client
 *    converts for display and back again when scheduling. Sending wall-clock
 *    time would make the server guess a timezone, and it would guess wrong
 *    twice a year.
 */

export type SocialPlatform =
  | "FacebookPage"
  | "InstagramBusiness"
  | "GoogleBusinessProfile"
  | "GoogleAds"
  /** Video only. Connected with Google sign-in, separately from Google Ads. */
  | "YouTubeChannel"
  /** The signed-in person's own LinkedIn profile. */
  | "LinkedInProfile"
  /** A LinkedIn company Page the person administers (needs LinkedIn's approval). */
  | "LinkedInPage"
  /** Video only. Each post carries the audience and settings chosen for it. */
  | "TikTokAccount"
  /** Posters and videos. The caption must fit X's 280 (see apps/web/src/lib/x-text.ts). */
  | "XAccount";

export type ConnectionStatus = "Active" | "NeedsReauth" | "Revoked";

export type SocialOAuthProviderKind = "Meta" | "Google" | "YouTube" | "LinkedIn" | "TikTok" | "X";

export interface SocialConnection {
  id: string;
  platform: SocialPlatform;
  platformName: string;
  externalAccountId: string;
  displayName: string;
  avatarUrl: string | null;
  status: ConnectionStatus;
  expiresAtUtc: string | null;
  lastVerifiedAtUtc: string | null;
  /** The platform's own words when it last refused us. Safe to show. */
  lastError: string | null;
  /** False for Google Ads (not a publishing target) and for expired credentials. */
  canPublish: boolean;
  supportsVideo: boolean;
  /** False for video-only platforms (YouTube): posters are not offered there. */
  supportsImages?: boolean;
  createdAtUtc: string;
}

/**
 * An account offered during OAuth, before the pharmacy picks one.
 *
 * `handle` is an opaque reference to credentials held server-side for ten
 * minutes. It is not the account id and cannot be used for anything else.
 */
export interface DiscoveredAccount {
  handle: string;
  externalAccountId: string;
  platform: SocialPlatform;
  platformName: string;
  displayName: string;
  avatarUrl: string | null;
  alreadyConnected: boolean;
}

/** Whether each Connect button can work for this pharmacy, and why not. */
export interface SocialAvailability {
  /** False on plans without social publishing. */
  planAllows: boolean;
  planReason: string | null;
  providers: {
    kind: SocialOAuthProviderKind;
    available: boolean;
    reason: string | null;
  }[];
}

export interface OAuthStart {
  authorizationUrl: string;
  /** Doubles as the session id the callback redeems. Generated server-side. */
  state: string;
}

/* ------------------------------------------------------------------ *
 * Publishing
 * ------------------------------------------------------------------ */

export type PublishStatus =
  | "Draft"
  | "Scheduled"
  | "Publishing"
  | "Published"
  /** Some targets succeeded, some didn't. A retry only re-attempts the failures. */
  | "PartiallyPublished"
  | "Failed"
  | "Cancelled";

export type PublishTargetStatus =
  | "Pending"
  | "Publishing"
  | "Published"
  | "Failed"
  /** Not attempted and not an error — the media doesn't suit that platform. */
  | "Skipped"
  /**
   * The platform may have published it; we could not confirm. Never retried
   * automatically — the pharmacy checks the account, then retries if needed.
   */
  | "Uncertain";

export type PublishSourceKind = "Poster" | "Video";

export type GbpPostType = "Update" | "Offer" | "Event";

export interface GbpPostOptions {
  postType: GbpPostType;
  couponCode?: string | null;
  redeemOnlineUrl?: string | null;
  termsConditions?: string | null;
  eventTitle?: string | null;
  eventStartUtc?: string | null;
  eventEndUtc?: string | null;
  callToActionType?: string | null;
  callToActionUrl?: string | null;
}

export type TikTokPrivacyLevel =
  | "PUBLIC_TO_EVERYONE"
  | "MUTUAL_FOLLOW_FRIENDS"
  | "FOLLOWER_OF_CREATOR"
  | "SELF_ONLY";

/** What the pharmacy chose for one TikTok post. Required when TikTok is a target. */
export interface TikTokPostOptions {
  privacyLevel: TikTokPrivacyLevel;
  allowComments: boolean;
  allowDuet: boolean;
  allowStitch: boolean;
  /** "Your brand": promotes the pharmacy's own business. */
  promotesOwnBusiness: boolean;
  /** "Branded content": a paid partnership promoting someone else. */
  isPaidPartnership: boolean;
}

/** TikTok's live answer about an account, shown on the share screen. */
export interface TikTokCreatorInfo {
  username: string;
  nickname: string;
  avatarUrl: string | null;
  privacyLevelOptions: TikTokPrivacyLevel[];
  commentDisabled: boolean;
  duetDisabled: boolean;
  stitchDisabled: boolean;
  maxVideoPostDurationSec: number;
}

export interface PublishTarget {
  connectionId: string;
  platform: SocialPlatform;
  platformName: string;
  accountName: string;
  status: PublishTargetStatus;
  externalPostId: string | null;
  permalink: string | null;
  error: string | null;
  attemptCount: number;
  publishedAtUtc: string | null;
}

export interface ScheduledPost {
  id: string;
  sourceKind: PublishSourceKind;
  sourceId: string;
  sourceName: string;
  caption: string;
  /** UTC. Null means it was queued to go out immediately. */
  scheduledForUtc: string | null;
  status: PublishStatus;
  targets: PublishTarget[];
  gbpOptions: GbpPostOptions | null;
  completedAtUtc: string | null;
  createdAtUtc: string;
  canRetry: boolean;
  canCancel: boolean;
  /** UTC. Set while an automatic retry is waiting out its backoff. */
  nextAttemptAtUtc?: string | null;
  /** The recurring series this post is an occurrence of; null for a one-off. */
  seriesId?: string | null;
  tikTokOptions?: TikTokPostOptions | null;
}

export interface ScheduledPostList {
  items: ScheduledPost[];
  total: number;
  skip: number;
  take: number;
}

export interface CreatePublishRequest {
  sourceKind: PublishSourceKind;
  sourceId: string;
  caption: string;
  connectionIds: string[];
  /** UTC ISO string, or omit for "publish now". */
  scheduledForUtc?: string | null;
  gbpOptions?: GbpPostOptions | null;
  tikTokOptions?: TikTokPostOptions | null;
}

/* ------------------------------------------------------------------ *
 * Google Ads
 * ------------------------------------------------------------------ */

export type AdsCampaignType = "Search" | "PerformanceMax" | "DemandGen";
export type AdsBudgetKind = "Daily" | "Lifetime";
export type AdsCampaignStatus =
  | "Draft"
  | "Creating"
  | "Enabled"
  | "Paused"
  | "Removed"
  | "Failed";

export interface AdsLocation {
  geoTargetConstantId: string | null;
  displayName: string | null;
  latitude: number | null;
  longitude: number | null;
  radiusKm: number | null;
}

/**
 * Metrics as of the last sync, not live.
 *
 * Google's figures move for days as invalid traffic is filtered, so always
 * show `lastSyncedAtUtc` alongside them — a number with no "as of" invites
 * someone to reconcile it against the Ads console and conclude one is broken.
 */
export interface AdsMetrics {
  impressions: number;
  clicks: number;
  cost: number;
  conversions: number;
  /** Percentage, already computed server-side. */
  clickThroughRate: number;
  costPerClick: number;
  lastSyncedAtUtc: string;
}

export interface GoogleAdsCampaign {
  id: string;
  connectionId: string;
  customerId: string;
  name: string;
  campaignType: AdsCampaignType;
  status: AdsCampaignStatus;
  externalCampaignId: string | null;
  budgetKind: AdsBudgetKind;
  /** Whole currency units. Micros conversion happens server-side. */
  budget: number;
  currencyCode: string;
  /** Calendar day, yyyy-MM-dd — not an instant. */
  startDate: string;
  endDate: string | null;
  finalUrl: string;
  locations: AdsLocation[];
  creativeKind: PublishSourceKind;
  creativeSourceId: string;
  headlines: string[];
  descriptions: string[];
  metrics: AdsMetrics;
  lastError: string | null;
  createdAtUtc: string;
  keywords?: string[] | null;
  longHeadline?: string | null;
  businessName?: string | null;
}

export interface CreateAdsCampaignRequest {
  connectionId: string;
  name: string;
  campaignType: AdsCampaignType;
  budgetKind: AdsBudgetKind;
  budget: number;
  startDate: string;
  endDate?: string | null;
  finalUrl: string;
  creativeKind: PublishSourceKind;
  creativeSourceId: string;
  locations?: AdsLocation[];
  headlines?: string[];
  descriptions?: string[];
  currencyCode?: string;
  /** Search: what people type. 1–20, up to 80 characters. */
  keywords?: string[];
  /** Performance Max: one long headline, up to 90 characters. */
  longHeadline?: string;
  /** Performance Max: the business name shown in the ad, up to 25 characters. */
  businessName?: string;
}

export interface AdsAccount {
  customerId: string;
  descriptiveName: string;
  currencyCode: string;
}

/* ------------------------------------------------------------------ *
 * Display helpers
 * ------------------------------------------------------------------ */

export const PUBLISH_PLATFORMS: SocialPlatform[] = [
  "FacebookPage",
  "InstagramBusiness",
  "GoogleBusinessProfile",
  "YouTubeChannel",
  "LinkedInProfile",
  "LinkedInPage",
  "TikTokAccount",
  "XAccount",
];

/** Both kinds of LinkedIn connection. */
export function isLinkedIn(platform: SocialPlatform): boolean {
  return platform === "LinkedInProfile" || platform === "LinkedInPage";
}

/**
 * Where a recurring series can post. Not Google Business Profile or Ads, and
 * not TikTok (each post's audience is chosen by a person) and not X (it
 * refuses repeated text, and a series repeats its caption).
 */
export const RECURRING_PLATFORMS: SocialPlatform[] = [
  "FacebookPage",
  "InstagramBusiness",
  "YouTubeChannel",
  "LinkedInProfile",
  "LinkedInPage",
];

/** Tone for a status pill. Keeps "Failed" the same red everywhere. */
export function publishStatusTone(
  status: PublishStatus,
): "neutral" | "good" | "warn" | "bad" {
  switch (status) {
    case "Published":
      return "good";
    case "Failed":
      return "bad";
    case "PartiallyPublished":
    case "Publishing":
    case "Scheduled":
      return "warn";
    default:
      return "neutral";
  }
}

export function describePublishStatus(status: PublishStatus): string {
  switch (status) {
    case "PartiallyPublished":
      return "Partly published";
    case "Publishing":
      return "Publishing…";
    default:
      return status;
  }
}

/**
 * True when a connection needs the pharmacy to do something before it works.
 *
 * Separate from `canPublish` because the two answer different questions:
 * a Google Ads connection is perfectly healthy and can never publish.
 */
export function needsAttention(connection: SocialConnection): boolean {
  return connection.status === "NeedsReauth";
}

/* ------------------------------------------------------------------ *
 * Recurring series
 * ------------------------------------------------------------------ */

export type SeriesStatus = "Active" | "Paused" | "Ended";
export type CadenceKind = "Daily" | "Weekly" | "EveryNDays" | "SpecificDates";
export type SeriesNotify = "All" | "ProblemsOnly" | "Off";
export type Weekday =
  | "Monday"
  | "Tuesday"
  | "Wednesday"
  | "Thursday"
  | "Friday"
  | "Saturday"
  | "Sunday";

/** Mon-first, as the pickers show them. */
export const WEEKDAYS: { value: Weekday; short: string }[] = [
  { value: "Monday", short: "Mon" },
  { value: "Tuesday", short: "Tue" },
  { value: "Wednesday", short: "Wed" },
  { value: "Thursday", short: "Thu" },
  { value: "Friday", short: "Fri" },
  { value: "Saturday", short: "Sat" },
  { value: "Sunday", short: "Sun" },
];

export interface CadenceRequest {
  kind: CadenceKind;
  /** Local wall-clock time, "HH:mm". */
  time: string;
  days?: Weekday[];
  intervalDays?: number;
  /** Local calendar days, "yyyy-MM-dd". */
  dates?: string[];
}

export interface SeriesRequest {
  name?: string;
  sourceKind: PublishSourceKind;
  sourceId: string;
  caption?: string;
  connectionIds: string[];
  cadence: CadenceRequest;
  /** IANA id. Defaults to Europe/London on the server. */
  timeZoneId?: string;
  /** "yyyy-MM-dd", local. */
  startDate: string;
  /** "yyyy-MM-dd", local, inclusive. Omit for "until I stop it". */
  endDate?: string | null;
  notify?: SeriesNotify;
  /** Create only: also post once straight away. */
  postNow?: boolean;
}

export interface SeriesPreviewRequest {
  cadence: CadenceRequest;
  timeZoneId?: string;
  startDate: string;
  endDate?: string | null;
  count?: number;
}

/** One occurrence, already on the pharmacy's clock. */
export interface SeriesTime {
  atUtc: string;
  /** "Tue 28 Oct, 09:00 UK" */
  label: string;
  /** "yyyy-MM-dd" on the pharmacy's clock. */
  localDate: string;
  /** "HH:mm" on the pharmacy's clock. */
  localTime: string;
}

export interface SeriesPreview {
  next: SeriesTime[];
  cadenceText: string;
  timeZoneLabel: string;
  /** Why the rule cannot be used, or null. */
  problem: string | null;
}

export interface SeriesDestination {
  connectionId: string;
  platform: SocialPlatform;
  platformName: string;
  displayName: string;
  /** Null when the account has been disconnected and removed. */
  status: ConnectionStatus | null;
  needsReconnect: boolean;
}

export interface SeriesOccurrence {
  postId: string;
  occurrenceAtUtc: string;
  label: string;
  status: PublishStatus;
  targets: PublishTarget[];
}

export interface RecurringSeries {
  id: string;
  name: string;
  sourceKind: PublishSourceKind;
  sourceId: string;
  sourceName: string;
  caption: string;
  destinations: SeriesDestination[];
  cadenceKind: CadenceKind;
  time: string;
  days: Weekday[];
  intervalDays: number;
  dates: string[];
  timeZoneId: string;
  timeZoneLabel: string;
  startDate: string;
  endDate: string | null;
  status: SeriesStatus;
  statusReason: string | null;
  pausedBySystem: boolean;
  notify: SeriesNotify;
  /** "Every Mon and Thu at 9:00" */
  cadenceText: string;
  nextOccurrenceUtc: string | null;
  /** "Tue 28 Oct, 09:00 UK" */
  nextLabel: string | null;
  /** Occurrences in the next 14 days. Empty unless active. */
  upcoming: SeriesTime[];
  recent: SeriesOccurrence[];
  missedCount: number;
  createdAtUtc: string;
}

export interface RecurringSeriesList {
  items: RecurringSeries[];
  activeCount: number;
  /** Active series the plan allows; 0 means not on this plan. */
  maxActive: number;
  planReason: string | null;
}
