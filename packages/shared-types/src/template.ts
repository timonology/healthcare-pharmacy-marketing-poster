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
