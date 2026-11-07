import type { CanvasDocument } from "./canvas";
import type { PosterPhoto } from "./template";

export type PosterStatus = "Draft" | "Published" | "Archived";

export const POSTER_STATUSES: PosterStatus[] = ["Draft", "Published", "Archived"];

export interface PosterSummary {
  id: string;
  ownerId: string;
  name: string;
  sourceTemplateId: string | null;
  /**
   * The card image, rendered by the API with the PDF renderer
   * (`/api/posters/{id}/card?v=…`, same-origin). Never a storage URL.
   */
  thumbnailUrl: string | null;
  /** Full canvas — the in-browser fallback when the card image cannot load. */
  canvas: CanvasDocument;
  status: PosterStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface Poster {
  id: string;
  ownerId: string;
  name: string;
  sourceTemplateId: string | null;
  thumbnailBlobKey: string | null;
  thumbnailUrl: string | null;
  canvas: CanvasDocument;
  status: PosterStatus;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface CreatePosterRequest {
  name: string;
  fromTemplateId?: string;
  canvas?: CanvasDocument;
}

export interface UpdatePosterRequest {
  name: string;
  canvas: CanvasDocument;
}

/**
 * A pharmacy's brief for an AI-designed poster.
 *
 * Design only. The layout model costs pennies per call and is predictable;
 * AI-generated *imagery* is billed per picture and "try another" is a new
 * charge every time, so generated designs use stock photography and the
 * pharmacy's own logo instead. That is a cost decision, not a limitation of
 * the generator — see PosterAiService.
 */
/** A designed poster, and what happened to its photo if it was not what was asked for. */
export interface GeneratedPoster {
  poster: Poster;
  /** e.g. "No stock photograph matched, so this design is photo-free." */
  photoNotes: string[];
}

export interface GeneratePosterRequest {
  /** What the poster should say and be for, in plain English. */
  prompt: string;
  /** Optional hint, e.g. "vaccination". */
  category?: string | null;
  /** Ignored: the house palette is fixed. */
  palette?: string | null;
  /** Where the photograph comes from. Omitted means stock. */
  photo?: PosterPhoto | null;
}

/** Shortest brief worth sending to a model. Mirrors PosterAiService.MinPromptLength. */
export const MIN_POSTER_PROMPT_CHARS = 8;

/** Longest brief accepted. Mirrors PosterAiService.MaxPromptLength. */
export const MAX_POSTER_PROMPT_CHARS = 1000;
