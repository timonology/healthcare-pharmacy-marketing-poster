import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import type { Shape } from "@acme/shared-types";
import { HERO_PREVIEW, SHOWCASE_PREVIEWS } from "./house-previews.generated";

/**
 * The marketing site shows snapshots of real house templates. These tests are
 * what keep them real: change a template in the seed and forget to re-run
 * `python3 scripts/marketing/build-house-previews.py`, and this fails instead
 * of the home page quietly advertising the old design.
 */

const SEED = path.resolve(
  __dirname,
  "../../../../api/src/Acme.Infrastructure/Persistence/HouseTemplates/templates.json",
);

type SeedTemplate = {
  id: string;
  name: string;
  category: string;
  background: string;
  shapes: Shape[];
};

const seed: SeedTemplate[] = JSON.parse(readFileSync(SEED, "utf8"));
const previews = [HERO_PREVIEW, ...SHOWCASE_PREVIEWS];
const BRAND_SOURCES = new Set(["/api/brand-kit/booking-qr", "/api/brand-kit/logo"]);

/** A shape with its image source blanked: the snapshot serves its own copy. */
function comparable(shape: Shape): unknown {
  return shape.kind === "image" ? { ...shape, blobKey: "" } : shape;
}

describe("marketing previews are the seeded house templates", () => {
  it.each(previews.map((p) => [p.id, p] as const))("%s matches the seed", (id, preview) => {
    const source = seed.find((t) => t.id === id);
    expect(source, `${id} is not in the house set any more`).toBeDefined();

    expect(preview.name).toBe(source!.name);
    expect(preview.category).toBe(source!.category);
    expect(preview.canvas.background).toBe(source!.background);

    const expected = source!.shapes
      .filter((s) => !(s.kind === "image" && BRAND_SOURCES.has(s.blobKey)))
      .map(comparable);
    expect(preview.canvas.shapes.map(comparable)).toEqual(expected);
  });

  it("never asks a visitor's browser for a pharmacy's brand assets", () => {
    for (const p of previews) {
      for (const s of p.canvas.shapes) {
        if (s.kind === "image") {
          expect(s.blobKey.startsWith("/marketing/house/"), `${p.id}/${s.id}`).toBe(true);
        }
      }
    }
  });

  it("shows only house templates, one per category in the showcase", () => {
    for (const p of previews) expect(p.id.startsWith("sys-house-")).toBe(true);
    const categories = SHOWCASE_PREVIEWS.map((p) => p.category);
    expect(new Set(categories).size).toBe(categories.length);
  });

  it("keeps no US copy or invented details on the most public page", () => {
    const text = previews
      .flatMap((p) => p.canvas.shapes)
      .filter((s) => s.kind === "text")
      .map((s) => (s as { text: string }).text)
      .join("\n");
    expect(text).not.toMatch(/insurance|\bcopay\b|flu shots?/i);
    expect(text).not.toMatch(/555-0\d{3}|Main Street|marketing25\.co\.uk/i);
  });
});
