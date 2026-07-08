import type {
  CanvasDocument,
  PharmacyProfile,
  Shape,
  TextShape,
} from "@acme/shared-types";

const PHARMACY_NAME_RE = /\{\{\s*pharmacy(_| )?name\s*\}\}/gi;
const ADDRESS_RE = /\{\{\s*address\s*\}\}/gi;
const CONTACT_RE = /\{\{\s*contact(_| )?name\s*\}\}/gi;
const PHONE_RE = /\{\{\s*phone\s*\}\}/gi;

/**
 * Inject the pharmacy profile into a canvas. Behaviour:
 *   1. Replace {{pharmacy_name}}, {{address}}, {{contact_name}}, {{phone}}
 *      tokens in every text shape.
 *   2. If no tokens were found, append a small footer line with the
 *      pharmacy name + phone at the bottom of the canvas.
 *   3. Update any shape already tagged with `id: "pharmacy-info"` instead
 *      of duplicating.
 */
export function applyPharmacyInfo(
  doc: CanvasDocument,
  profile: PharmacyProfile,
): CanvasDocument {
  let hadTokens = false;

  const shapes: Shape[] = doc.shapes.map((shape) => {
    if (shape.kind !== "text") return shape;

    const replaced = shape.text
      .replace(PHARMACY_NAME_RE, profile.pharmacyName || "")
      .replace(ADDRESS_RE, profile.address || "")
      .replace(CONTACT_RE, profile.contactName || "")
      .replace(PHONE_RE, profile.contactPhone || "");

    if (replaced !== shape.text) hadTokens = true;
    return { ...shape, text: replaced };
  });

  if (!hadTokens) {
    const footerText = [profile.pharmacyName, profile.contactPhone]
      .filter(Boolean)
      .join(" · ");
    if (footerText) {
      const existingIdx = shapes.findIndex((s) => s.id === "pharmacy-info");
      const footer: TextShape = {
        id: "pharmacy-info",
        kind: "text",
        position: { x: 80, y: doc.height - 70 },
        rotation: 0,
        scale: { x: 1, y: 1 },
        opacity: 0.85,
        draggable: true,
        zIndex: Math.max(0, ...doc.shapes.map((s) => s.zIndex)) + 1,
        text: footerText,
        fontSize: 22,
        fontFamily: "Inter, sans-serif",
        fill: "#475569",
        align: "left",
        width: doc.width - 160,
      };
      if (existingIdx >= 0) shapes[existingIdx] = footer;
      else shapes.push(footer);
    }
  }

  return {
    ...doc,
    shapes,
    updatedAtUtc: new Date().toISOString(),
  };
}
