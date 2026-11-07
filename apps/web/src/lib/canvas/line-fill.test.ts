import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { lineFill } from "./line-fill";

/**
 * A closed line is a filled polygon in the PDF and the gallery preview; the
 * editor drew only its outline. The AI designer builds diagonal splits from
 * closed lines, so those showed as outlines while editing and printed solid.
 */
describe("lineFill", () => {
  it("fills a closed line with its stroke colour", () => {
    expect(lineFill({ closed: true, stroke: "#0fa3b1" })).toBe("#0fa3b1");
  });

  it("leaves an open line unfilled", () => {
    expect(lineFill({ closed: false, stroke: "#0fa3b1" })).toBeUndefined();
    expect(lineFill({ stroke: "#0fa3b1" })).toBeUndefined();
  });

  it("is what both Konva renderers pass to <Line>", () => {
    // Source-level, because rendering Konva needs a real canvas. Each Line in
    // these files must take its fill from here, or they drift apart again.
    for (const file of [
      "src/components/editor/shapes/ShapeNode.tsx",
      "src/components/canvas/CanvasStage.tsx",
    ]) {
      const src = readFileSync(resolve(process.cwd(), file), "utf8");
      const lines = src.split("<Line").slice(1).map((chunk) => chunk.split("/>")[0]);
      expect(lines.length, file).toBeGreaterThan(0);
      for (const props of lines) expect(props, file).toContain("fill={lineFill(shape)}");
    }
  });
});
