import type { LineShape } from "@acme/shared-types";

/**
 * What a line is filled with: its stroke colour when it is closed, nothing
 * when it is open.
 *
 * <b>One rule for every renderer.</b> The PDF exporter and the gallery preview
 * have always drawn a closed line as a filled polygon in its stroke colour —
 * it is the schema's only filled polygon, and the AI designer builds its
 * diagonal splits from it. The editor passed `closed` to Konva without a
 * `fill`, and Konva fills only when told to, so the same poster showed an
 * outline while you edited it and printed a solid band. Both Konva renderers
 * use this, so they cannot drift apart again.
 */
export function lineFill(shape: Pick<LineShape, "closed" | "stroke">): string | undefined {
  return shape.closed ? shape.stroke : undefined;
}
