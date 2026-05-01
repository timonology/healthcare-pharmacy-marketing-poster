import type { CanvasDocument } from "./canvas";

export type PosterStatus = "Draft" | "Published" | "Archived";

export const POSTER_STATUSES: PosterStatus[] = ["Draft", "Published", "Archived"];

export interface PosterSummary {
  id: string;
  ownerId: string;
  name: string;
  sourceTemplateId: string | null;
  thumbnailUrl: string | null;
  /** Full canvas — rendered as an inline SVG preview on the My Posters list. */
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
