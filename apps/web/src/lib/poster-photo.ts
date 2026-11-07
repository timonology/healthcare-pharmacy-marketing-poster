/**
 * The photo choice for "Design with AI" — the rules, without the UI.
 *
 * One set for the admin designer and the pharmacy dialog, so the two cannot
 * drift: four modes, the same file rules, the same "why can't I generate yet".
 */
import type { PosterPhoto, PosterPhotoMode } from "@acme/shared-types";

/** The formats the API stores and the designer bakes. Checked again, by bytes, on upload. */
export const ACCEPTED_PHOTO_TYPES = ["image/jpeg", "image/png", "image/webp"] as const;

/** The same 12MB as the admin upload. The browser downscales before sending. */
export const MAX_PHOTO_BYTES = 12 * 1024 * 1024;

export interface PhotoChoice {
  mode: PosterPhotoMode;
  /** Upload (or an admin's previewed AI image): our stored asset. */
  assetUrl?: string | null;
  /** Stock: the photo the user picked. Empty lets the designer choose. */
  stockPhotoId?: string | null;
  /** Upload: what it shows. Ai: what to generate. Stock: the photo's alt text. */
  description?: string;
  /** For the thumbnail only. */
  previewUrl?: string | null;
  /** Where a stored asset came from, for the caption. */
  source?: "upload" | "ai" | "stock";
}

export const DEFAULT_PHOTO: PhotoChoice = { mode: "Stock" };

/** Why this file cannot be used, or null. Shown before anything is uploaded. */
export function checkPhotoFile(file: { type: string; size: number }): string | null {
  if (!(ACCEPTED_PHOTO_TYPES as readonly string[]).includes(file.type)) {
    return "Choose a JPEG, PNG or WebP photo.";
  }
  if (file.size > MAX_PHOTO_BYTES) return "That photo is larger than 12MB.";
  if (file.size === 0) return "That file is empty.";
  return null;
}

/**
 * Why a design cannot be generated yet, or null when it can.
 *
 * A message, not a boolean, so the dialog can say what is missing instead of
 * leaving a dead button.
 */
export function photoBlockedReason(
  choice: PhotoChoice,
  state: { uploading?: boolean; aiAvailable: boolean },
): string | null {
  switch (choice.mode) {
    case "Upload":
      if (state.uploading) return "Waiting for the photo to finish uploading…";
      return choice.assetUrl ? null : "Choose a photo to upload, or pick another photo option.";
    case "Ai":
      if (!state.aiAvailable) return "AI images aren't set up. Choose stock, upload or no photo.";
      return (choice.description?.trim().length ?? 0) >= 4
        ? null
        : "Describe the photo you want generated.";
    default:
      return null;
  }
}

/** The request field. An admin's previewed AI image travels as the stored asset it already is. */
export function toRequestPhoto(choice: PhotoChoice): PosterPhoto {
  const description = choice.description?.trim() || null;
  switch (choice.mode) {
    case "None":
      return { mode: "None" };
    case "Upload":
      return { mode: "Upload", assetUrl: choice.assetUrl ?? null, description };
    case "Ai":
      return choice.assetUrl
        ? { mode: "Upload", assetUrl: choice.assetUrl, description }
        : { mode: "Ai", description };
    default:
      return { mode: "Stock", stockPhotoId: choice.stockPhotoId ?? null, description };
  }
}
