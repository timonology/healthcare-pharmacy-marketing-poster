import type { CanvasDocument } from "./canvas";

export type TemplateCategory =
  | "Vaccination"
  | "Promotion"
  | "Awareness"
  | "Safety"
  | "Seasonal"
  | "General";

export const TEMPLATE_CATEGORIES: TemplateCategory[] = [
  "Vaccination",
  "Promotion",
  "Awareness",
  "Safety",
  "Seasonal",
  "General",
];

export interface TemplateSummary {
  id: string;
  name: string;
  description: string;
  category: TemplateCategory;
  tags: string[];
  thumbnailUrl: string | null;
  /** Full canvas document so the web can render an inline SVG preview. */
  canvas: CanvasDocument;
  isPublished: boolean;
  updatedAtUtc: string;
}

export interface Template {
  id: string;
  name: string;
  description: string;
  category: TemplateCategory;
  tags: string[];
  thumbnailBlobKey: string | null;
  thumbnailUrl: string | null;
  canvas: CanvasDocument;
  isPublished: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
}

/**
 * A candidate design returned by the AI template generator.
 *
 * `canvasJson` is a serialised CanvasDocument rather than a parsed object
 * because the API passes the generator's output through verbatim — it has
 * already been validated against the print rules on the Python side, and
 * re-serialising it through C# would risk changing it.
 */
/** A stored image available to use in a template. */
export interface StoredTemplateAsset {
  blobKey: string;
  /** Permanent application URL, e.g. "/api/assets/templates/ab12….jpg". */
  url: string;
  contentType: string;
}

/** A stock photo candidate shown in the picker. */
export interface StockPhoto {
  id: string;
  /** Provider CDN URL — only the chosen photo is copied into our storage. */
  thumbnailUrl: string;
  width: number;
  height: number;
  photographer: string;
  alt: string;
}

/** An image ready to design around, wherever it came from. */
export interface TemplateImage {
  url: string;
  width: number;
  height: number;
  description?: string | null;
}

/** A produced poster image, plus where it came from. */
export interface PosterImage extends TemplateImage {
  /**
   * "ai" when an image model produced it, "stock" when it came from
   * photography. Chosen server-side based on which credentials exist, so the
   * UI never has to know the deployment's configuration.
   */
  source: "ai" | "stock";
  /** Further stock candidates available, for "try another". */
  remaining: number;
}

/** Shape of a generated photograph. Matches poster_imagery.ASPECT_RATIOS. */
export type TemplateImageAspect = "banner" | "hero" | "portrait" | "full";

export const TEMPLATE_IMAGE_ASPECTS: {
  value: TemplateImageAspect;
  label: string;
  hint: string;
}[] = [
  { value: "hero", label: "Hero", hint: "4:3 — the classic upper-half photo" },
  { value: "banner", label: "Banner", hint: "16:9 — a wide strip across the top" },
  { value: "portrait", label: "Side panel", hint: "3:4 — a tall column" },
  { value: "full", label: "Full bleed", hint: "9:16 — behind the whole poster" },
];

/**
 * Where an AI design's photograph comes from. The same four choices for an
 * administrator and a pharmacy.
 *
 * - None: a typographic / geometric house-style design.
 * - Stock: the chosen stock photo, or the best match for the design when none
 *   was chosen. The default when nothing is uploaded.
 * - Upload: an image already uploaded to our storage (`assetUrl`).
 * - Ai: generated — offered only when an image model is configured.
 */
export type PosterPhotoMode = "None" | "Stock" | "Upload" | "Ai";

export interface PosterPhoto {
  mode: PosterPhotoMode;
  /** Upload: our own asset URL. */
  assetUrl?: string | null;
  /** Stock: the photo the user chose, by provider id. */
  stockPhotoId?: string | null;
  /** Upload: what it shows. Ai: what it should show. */
  description?: string | null;
}

export const POSTER_PHOTO_DESCRIPTION_MAX = 300;

/** Which photo sources this deployment can serve. */
export interface PhotoOptions {
  ai: boolean;
  stock: boolean;
}

/** A photo a pharmacy uploaded for an AI design. */
export interface StoredPosterPhoto {
  url: string;
  contentType: string;
}

export interface GeneratedTemplate {
  name: string;
  canvasJson: string;
  /** Repair rounds the generator needed before the design passed validation. */
  notes: string[];
  /** What happened to the photo when it was not what was asked for. */
  photoNotes?: string[] | null;
}

export interface UpsertTemplateRequest {
  name: string;
  description: string;
  category: TemplateCategory;
  tags: string[];
  canvas: CanvasDocument;
  isPublished: boolean;
}

export interface PagedResponse<T> {
  items: T[];
  total: number;
  skip: number;
  take: number;
}
