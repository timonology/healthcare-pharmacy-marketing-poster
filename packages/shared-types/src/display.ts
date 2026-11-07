/**
 * In-pharmacy display screens: the hardware, the playlists, and what a kiosk
 * receives.
 */

export type PlaylistItemKind = "Poster" | "Video";

export type DisplayCredentialKind = "PairedDevice" | "DirectLink";

/** Where a device presents its token. Never a query string — logs keep those. */
export const DISPLAY_TOKEN_HEADER = "X-Display-Token";

/** Where a paired device remembers who it is. */
export const DISPLAY_TOKEN_STORAGE_KEY = "marketing25.display.token";

/** Where it remembers what to show when the network is gone. */
export const DISPLAY_CACHE_STORAGE_KEY = "marketing25.display.content";

export interface DisplayCredential {
  id: string;
  kind: DisplayCredentialKind;
  createdAtUtc: string;
  lastSeenAtUtc: string | null;
}

export interface DisplayScreen {
  id: string;
  name: string;
  /** Where in the shop it hangs. For the pharmacy's own records. */
  location: string;
  /** Drawn on the screen itself when set. Empty means show nothing. */
  addressLine: string;
  playlistId: string | null;
  playlistName: string | null;
  itemCount: number;
  isPaired: boolean;
  /** Only present while still worth typing; the API withholds a stale code. */
  pairingCode: string | null;
  pairingCodeExpiresAtUtc: string | null;
  lastSeenAtUtc: string | null;
  credentials: DisplayCredential[];
  createdAtUtc: string;
}

export interface PlaylistItem {
  kind: PlaylistItemKind;
  refId: string;
  name: string;
  durationSeconds: number;
  /**
   * False once the poster is unpublished or deleted, or the video is deleted,
   * failed, or being re-cut — shown in the editor, skipped on the screen.
   */
  isPlayable: boolean;
}

export interface Playlist {
  id: string;
  name: string;
  version: number;
  items: PlaylistItem[];
  totalSeconds: number;
  /** Names of the screens currently showing this, so an edit's reach is visible. */
  screenNames: string[];
  updatedAtUtc: string;
}

export interface DisplayTokenIssued {
  token: string;
  screenId: string;
  screenName: string;
}

export interface KioskSlide {
  kind: PlaylistItemKind;
  /** Poster path, fetched with the token in a header. Empty for a video. */
  imageUrl: string;
  name: string;
  /**
   * A poster's dwell time. For a video, its own length — the kiosk moves on
   * when the video ends and uses this only as a safety net.
   */
  durationSeconds: number;
  /**
   * What to show. A poster: the same path as `imageUrl`. A video: a
   * same-origin path with a signature for that one file (no token, no blob
   * key), playable directly by a `<video>` element. Absent from older APIs.
   */
  mediaUrl?: string | null;
}

export interface KioskContent {
  screenName: string;
  addressLine: string;
  slides: KioskSlide[];
  /** Moves only when what is displayed moves. Backs the ETag. */
  revision: string;
  pollSeconds: number;
}

/* ── shared bounds, so the editor refuses what the server would ──────── */

export const PLAYLIST_MIN_SECONDS = 3;
export const PLAYLIST_MAX_SECONDS = 300;
export const PLAYLIST_DEFAULT_SECONDS = 12;
export const PLAYLIST_MAX_ITEMS = 50;
/** Videos stream rather than cache, so a playlist carries only a few. */
export const PLAYLIST_MAX_VIDEOS = 10;
/** Sent by a kiosk that can play video; without it the API sends posters only. */
export const DISPLAY_CAPABILITIES_HEADER = "X-Display-Capabilities";
export const PAIRING_CODE_LENGTH = 6;
