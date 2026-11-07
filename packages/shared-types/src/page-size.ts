/**
 * Print page sizes for the poster designer.
 *
 * Consumed by:
 *   - apps/web (page size selector + canvas resize)
 *   - apps/api (QuestPdfExporter maps canvas dimensions back to a paper size)
 *
 * Pixel dimensions are ISO paper sizes rasterised at PRINT_DPI. 150 DPI is the
 * practical floor for poster print — below it, large-format output visibly
 * softens — and it is what the seeded templates already use (A4 => 1240x1754),
 * so existing posters classify as A4 without a migration.
 */

export const PRINT_DPI = 150;

const MM_PER_INCH = 25.4;

/**
 * Paper dimensions in millimetres, portrait.
 *
 * Pure paper, and nothing else. It is tempting to add "TrifoldA4" here, but a
 * trifold sheet *is* A4 landscape — an entry with the same dimensions as one
 * already present would make {@link classifyPageSize} ambiguous, and whichever
 * happened to be checked first would win. What a piece of artwork is *for*
 * lives in {@link PrintFormat} instead.
 */
const PAPER_MM = {
  A4: { width: 210, height: 297 },
  A3: { width: 297, height: 420 },
  A5: { width: 148, height: 210 },
  /** DIN long — the standard leaflet that fits a #10 envelope. */
  DL: { width: 99, height: 210 },
} as const;

export type PageSizeName = keyof typeof PAPER_MM;
export type PageOrientation = "portrait" | "landscape";

export const PAGE_SIZE_NAMES = Object.keys(PAPER_MM) as PageSizeName[];

export interface PageDimensions {
  width: number;
  height: number;
}

function mmToPx(mm: number): number {
  return Math.round((mm / MM_PER_INCH) * PRINT_DPI);
}

/** Pixel dimensions for a named size and orientation. */
export function pageDimensions(
  name: PageSizeName,
  orientation: PageOrientation = "portrait",
): PageDimensions {
  const mm = PAPER_MM[name];
  const width = mmToPx(mm.width);
  const height = mmToPx(mm.height);
  return orientation === "landscape"
    ? { width: height, height: width }
    : { width, height };
}

/** Human label for pickers, e.g. "A4 — 210 × 297 mm". */
export function pageSizeLabel(name: PageSizeName): string {
  const mm = PAPER_MM[name];
  return `${name} — ${mm.width} × ${mm.height} mm`;
}

export const DEFAULT_PAGE_SIZE: PageSizeName = "A4";

/**
 * Best-effort classification of arbitrary canvas dimensions.
 *
 * Posters predate the size selector and users can resize freely, so this has to
 * cope with dimensions that match nothing. It picks the closest known size by
 * relative area error and only commits when within `tolerance` (5% by default,
 * which comfortably absorbs the sub-pixel rounding in mmToPx but rejects, say,
 * a 1920x1080 social graphic). Returning null lets the caller fall back to
 * rendering at the canvas's own aspect ratio rather than forcing it onto A4.
 */
export function classifyPageSize(
  width: number,
  height: number,
  tolerance = 0.05,
): { name: PageSizeName; orientation: PageOrientation } | null {
  if (!(width > 0) || !(height > 0)) return null;

  let best: { name: PageSizeName; orientation: PageOrientation } | null = null;
  let bestError = Number.POSITIVE_INFINITY;

  for (const name of PAGE_SIZE_NAMES) {
    for (const orientation of ["portrait", "landscape"] as PageOrientation[]) {
      const dims = pageDimensions(name, orientation);
      const error =
        Math.abs(dims.width - width) / dims.width +
        Math.abs(dims.height - height) / dims.height;
      if (error < bestError) {
        bestError = error;
        best = { name, orientation };
      }
    }
  }

  return bestError <= tolerance * 2 ? best : null;
}


/* ── what a piece of artwork is for ──────────────────────────────────── */

/**
 * The kind of printed thing, which is not the same question as its paper size.
 *
 * A4 is a poster and also the sheet a trifold leaflet is printed on. The size
 * says how big it is; this says what it becomes — which is what decides whether
 * the editor draws fold guides and how the gallery groups templates.
 */
export const PRINT_FORMATS = ["Poster", "Leaflet", "Brochure"] as const;
export type PrintFormat = (typeof PRINT_FORMATS)[number];

export const DEFAULT_PRINT_FORMAT: PrintFormat = "Poster";

/** The paper sizes that make sense for each format, best first. */
export const SIZES_FOR_FORMAT: Record<PrintFormat, readonly PageSizeName[]> = {
  Poster: ["A4", "A3"],
  Leaflet: ["DL", "A5", "A4"],
  Brochure: ["A4", "A5"],
};

/**
 * How many panels the sheet folds into.
 *
 * Three for a trifold, two for a simple folded card, one for anything flat.
 * Kept on the document rather than inferred from the size, because an A4
 * landscape sheet is a trifold only if somebody meant it to be.
 */
export type FoldPanels = 1 | 2 | 3;

/**
 * Where the folds fall, in canvas pixels.
 *
 * Evenly spaced, which is the honest simplification: a real trifold makes the
 * inward-folding panel a couple of millimetres narrower so it does not catch on
 * the fold. That refinement belongs with a print partner's specification rather
 * than guessed at here — and evenly spaced guides are still the difference
 * between designing a leaflet and designing a landscape poster.
 *
 * Returns an empty list for a flat sheet, so callers need no special case.
 */
export function foldGuides(canvasWidth: number, panels: FoldPanels): number[] {
  if (panels < 2 || !(canvasWidth > 0)) return [];
  return Array.from({ length: panels - 1 }, (_, i) =>
    Math.round((canvasWidth * (i + 1)) / panels),
  );
}

/**
 * The sheet a format is printed on by default.
 *
 * A trifold leaflet is A4 turned on its side and folded into three 99mm panels
 * — which is precisely DL, and is why a trifold fits the same envelope as a DL
 * leaflet.
 */
export function defaultSheet(format: PrintFormat): {
  name: PageSizeName;
  orientation: PageOrientation;
  panels: FoldPanels;
} {
  switch (format) {
    case "Leaflet":
      return { name: "DL", orientation: "portrait", panels: 1 };
    case "Brochure":
      return { name: "A4", orientation: "landscape", panels: 3 };
    case "Poster":
    default:
      return { name: "A4", orientation: "portrait", panels: 1 };
  }
}

/**
 * What a document is, for one that may predate formats.
 *
 * An explicit {@link CanvasDocument.format} always wins — it is what the
 * designer chose. Everything else is inferred from the sheet, because every
 * template made before this existed still has to appear under the right heading
 * without a migration:
 *
 *   · a DL or A5 sheet is a leaflet — nobody prints an A5 poster
 *   · a sheet folded into panels is a brochure, whatever size it is
 *   · anything else is a poster, including sizes we do not recognise
 *
 * Inference, not truth. Saving the document records the real answer.
 */
export function inferPrintFormat(input: {
  width: number;
  height: number;
  format?: PrintFormat;
  foldPanels?: FoldPanels;
}): PrintFormat {
  if (input.format) return input.format;
  if ((input.foldPanels ?? 1) > 1) return "Brochure";

  const sheet = classifyPageSize(input.width, input.height);
  if (sheet && (sheet.name === "DL" || sheet.name === "A5")) return "Leaflet";

  return "Poster";
}
