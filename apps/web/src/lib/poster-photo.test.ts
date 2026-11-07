import { describe, expect, it } from "vitest";
import {
  DEFAULT_PHOTO,
  MAX_PHOTO_BYTES,
  checkPhotoFile,
  photoBlockedReason,
  toRequestPhoto,
} from "./poster-photo";

/**
 * The photo rules for "Design with AI", shared by the admin designer and the
 * pharmacy dialog: four modes, the same file checks, the same reasons.
 */
describe("checkPhotoFile", () => {
  it("accepts JPEG, PNG and WebP up to 12MB", () => {
    for (const type of ["image/jpeg", "image/png", "image/webp"]) {
      expect(checkPhotoFile({ type, size: 2_000_000 })).toBeNull();
    }
    expect(checkPhotoFile({ type: "image/jpeg", size: MAX_PHOTO_BYTES })).toBeNull();
  });

  it("refuses anything else before it is uploaded", () => {
    expect(checkPhotoFile({ type: "application/pdf", size: 1000 })).toMatch(/JPEG, PNG or WebP/);
    expect(checkPhotoFile({ type: "image/gif", size: 1000 })).toMatch(/JPEG, PNG or WebP/);
    expect(checkPhotoFile({ type: "image/jpeg", size: MAX_PHOTO_BYTES + 1 })).toMatch(/12MB/);
    expect(checkPhotoFile({ type: "image/png", size: 0 })).toMatch(/empty/);
  });
});

describe("photoBlockedReason", () => {
  it("lets the default — stock, nothing picked — generate straight away", () => {
    expect(DEFAULT_PHOTO.mode).toBe("Stock");
    expect(photoBlockedReason(DEFAULT_PHOTO, { aiAvailable: false })).toBeNull();
  });

  it("waits for an upload, and asks for one", () => {
    expect(photoBlockedReason({ mode: "Upload" }, { aiAvailable: false })).toMatch(/Choose a photo/);
    expect(photoBlockedReason({ mode: "Upload" }, { aiAvailable: false, uploading: true })).toMatch(
      /finish uploading/,
    );
    expect(
      photoBlockedReason({ mode: "Upload", assetUrl: "/api/assets/templates/a.jpg" }, { aiAvailable: false }),
    ).toBeNull();
  });

  it("refuses AI when it is not set up rather than quietly using stock", () => {
    expect(photoBlockedReason({ mode: "Ai", description: "a pharmacist" }, { aiAvailable: false })).toMatch(
      /aren't set up/,
    );
    expect(photoBlockedReason({ mode: "Ai", description: "ab" }, { aiAvailable: true })).toMatch(/Describe/);
    expect(photoBlockedReason({ mode: "Ai", description: "a pharmacist" }, { aiAvailable: true })).toBeNull();
  });
});

describe("toRequestPhoto", () => {
  it("sends each mode as the API expects", () => {
    expect(toRequestPhoto({ mode: "None", description: "x" })).toEqual({ mode: "None" });
    expect(toRequestPhoto({ mode: "Stock" })).toEqual({ mode: "Stock", stockPhotoId: null, description: null });
    expect(toRequestPhoto({ mode: "Stock", stockPhotoId: "123", description: " alt " })).toEqual({
      mode: "Stock",
      stockPhotoId: "123",
      description: "alt",
    });
    expect(toRequestPhoto({ mode: "Upload", assetUrl: "/api/assets/templates/a.jpg", description: "" }))
      .toEqual({ mode: "Upload", assetUrl: "/api/assets/templates/a.jpg", description: null });
    expect(toRequestPhoto({ mode: "Ai", description: "a nurse" })).toEqual({ mode: "Ai", description: "a nurse" });
  });

  it("sends an admin's previewed AI image as the stored asset it already is", () => {
    expect(toRequestPhoto({ mode: "Ai", description: "a nurse", assetUrl: "/api/assets/templates/b.png" }))
      .toEqual({ mode: "Upload", assetUrl: "/api/assets/templates/b.png", description: "a nurse" });
  });
});
