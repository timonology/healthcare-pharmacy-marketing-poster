import {
  BRAND_BOOKING_QR_SRC,
  BRAND_LOGO_SRC,
  type CanvasDocument,
  type ImageShape,
  type PharmacyProfile,
  type Shape,
  type TextShape,
} from "@acme/shared-types";

/**
 * Ids for the shapes this function owns.
 *
 * Anything carrying one of these is managed: re-applying rewrites it in place.
 * Anything else on the canvas belongs to the person designing, and is left
 * alone. Without this, pressing the button twice left two footers and two
 * logos, and the second press looked like it had done nothing wrong.
 */
export const PHARMACY_INFO_ID = "pharmacy-info";
export const PHARMACY_LOGO_ID = "pharmacy-logo";

/**
 * The slot a template author places for a booking QR code.
 *
 * <b>Filled, never invented.</b> The logo is auto-placed on any poster that has
 * no slot for it, because every pharmacy has one identity mark and it belongs on
 * everything they print. A booking code is not like that: it is optional, it is
 * only meaningful on a poster that asks the reader to book, and dropping one
 * onto every design unasked would be the app editing somebody's artwork.
 *
 * So a template that wants one gives it this id, and this function points it at
 * the pharmacy's own link — or blanks it when they have none.
 */
export const PHARMACY_BOOKING_QR_ID = "pharmacy-booking-qr";

/**
 * Mirrors the regex in `BrandCanvasAssets.PharmacyToken` on the API side.
 *
 * Two lists that have to agree, and nothing makes them. This one runs when the
 * designer presses Apply pharmacy info; the API's runs at render time. A token
 * added to only one side either resolves on screen and prints literally, or the
 * reverse — and in both cases the pharmacist sees the correct thing while the
 * patient gets the broken one. Adding a token means editing both.
 */
export const TOKENS: ReadonlyArray<[RegExp, keyof ReplacementValues]> = [
  [/\{\{\s*pharmacy(_| )?name\s*\}\}/gi, "pharmacyName"],
  [/\{\{\s*address\s*\}\}/gi, "address"],
  [/\{\{\s*post(_| )?code\s*\}\}/gi, "postCode"],
  [/\{\{\s*contact(_| )?name\s*\}\}/gi, "contactName"],
  [/\{\{\s*phone\s*\}\}/gi, "contactPhone"],
  [/\{\{\s*booking(_| )?(url|link)\s*\}\}/gi, "bookingUrl"],
];

export interface ReplacementValues {
  pharmacyName: string;
  address: string;
  postCode: string;
  contactName: string;
  contactPhone: string;
  bookingUrl: string;
}

export interface ApplyOptions {
  /**
   * Whether the pharmacy has uploaded a logo.
   *
   * Passed in rather than assumed, because placing a logo shape for a pharmacy
   * that has none puts a permanent broken image on the poster.
   */
  hasLogo?: boolean;
}

/**
 * Puts the pharmacy's own details onto a canvas.
 *
 * <b>Runnable more than once.</b> The first version replaced `{{tokens}}` in
 * place, which destroyed them — so the details could be applied exactly once
 * per template, and correcting a typo in your phone number left every poster
 * you had already made showing the old one with no way to refresh it short of
 * starting again. Each text shape now remembers the text it was authored with,
 * so applying again re-renders from the original rather than from the result.
 *
 * What it applies: name, address (with postcode), contact name, phone, the
 * logo, and a booking QR into a slot that asks for one. What it will not do is
 * touch a shape the designer made themselves.
 */
export function applyPharmacyInfo(
  doc: CanvasDocument,
  profile: PharmacyProfile,
  options: ApplyOptions = {},
): CanvasDocument {
  const values: ReplacementValues = {
    pharmacyName: profile.pharmacyName?.trim() ?? "",
    address: profile.address?.trim() ?? "",
    postCode: profile.postCode?.trim() ?? "",
    contactName: profile.contactName?.trim() ?? "",
    contactPhone: profile.contactPhone?.trim() ?? "",
    // Empty rather than left as a token when the pharmacy has not set one — a
    // gap on the poster, not the words "booking_url" in front of a patient.
    bookingUrl: profile.bookingUrl?.trim() ?? "",
  };

  let hadTokens = false;

  const shapes: Shape[] = doc.shapes.map((shape) => {
    if (shape.kind !== "text") return shape;

    // The template's own wording, not the last render of it.
    const source = shape.sourceText ?? shape.text;
    let replaced = source;
    for (const [pattern, key] of TOKENS) {
      // A fresh lastIndex each time: these are module-level /g regexes, and a
      // /g regex carries its position between calls. Reusing one across shapes
      // makes it skip matches in every other shape, which reads as the details
      // applying to some parts of a poster and not others.
      pattern.lastIndex = 0;
      replaced = replaced.replace(pattern, values[key]);
    }

    if (replaced === source) return shape;

    hadTokens = true;
    // Keep the original so the next apply starts from the template again.
    return { ...shape, text: replaced, sourceText: source };
  });

  const nextZ = () => Math.max(0, ...shapes.map((s) => s.zIndex)) + 1;

  /* ── the footer line, when the template has no tokens of its own ──── */

  // A template that carries its own tokens has said where the details go, so
  // a footer from an earlier apply is now a second copy of them. Remove it.
  if (hadTokens) {
    const stale = shapes.findIndex((s) => s.id === PHARMACY_INFO_ID);
    if (stale >= 0) shapes.splice(stale, 1);
  }

  if (!hadTokens && !alreadyShowsContactDetails(shapes, values)) {
    const line = [
      values.pharmacyName,
      [values.address, values.postCode].filter(Boolean).join(", "),
      values.contactPhone,
    ]
      .filter(Boolean)
      .join(" · ");

    if (line) {
      const footer: TextShape = {
        id: PHARMACY_INFO_ID,
        kind: "text",
        position: { x: 80, y: doc.height - 70 },
        rotation: 0,
        scale: { x: 1, y: 1 },
        opacity: 0.85,
        draggable: true,
        zIndex: nextZ(),
        text: line,
        fontSize: 22,
        fontFamily: "Inter, sans-serif",
        fill: "#475569",
        align: "left",
        width: doc.width - 160,
      };
      upsert(shapes, PHARMACY_INFO_ID, footer);
    }
  }

  /* ── the logo ────────────────────────────────────────────────────── */

  if (options.hasLogo) {
    const existing = shapes.find(
      (s): s is ImageShape => s.id === PHARMACY_LOGO_ID && s.kind === "image",
    );

    if (existing) {
      // A slot the template author placed, or our own from a previous apply.
      // Its position and size are somebody's decision — only the source is
      // ours to set.
      upsert(shapes, PHARMACY_LOGO_ID, { ...existing, blobKey: BRAND_LOGO_SRC });
    } else {
      // No slot: bottom-right, above the footer line, sized to a sensible
      // fraction of the page so it lands proportionate on A4 and on A3 alike.
      const width = Math.round(doc.width * 0.16);
      const height = Math.round(width * 0.5);
      const logo: ImageShape = {
        id: PHARMACY_LOGO_ID,
        kind: "image",
        position: { x: doc.width - width - 80, y: doc.height - height - 90 },
        rotation: 0,
        scale: { x: 1, y: 1 },
        opacity: 1,
        draggable: true,
        zIndex: nextZ(),
        blobKey: BRAND_LOGO_SRC,
        width,
        height,
      };
      shapes.push(logo);
    }
  }

  // The booking code, if this design asked for one.
  //
  // Only ever rewrites the source. Where it sits and how big it is were decided
  // by whoever laid the template out, and a QR that the app moved or resized is
  // a QR somebody has to put back.
  const qrSlot = shapes.find(
    (s): s is ImageShape => s.id === PHARMACY_BOOKING_QR_ID && s.kind === "image",
  );
  if (qrSlot) {
    // Always the path, even when the pharmacy has no booking link yet.
    //
    // Blanking the source here looked tidier and was wrong. It decides, once, a
    // question the renderers already answer every time they draw: the export
    // turns this path into an empty source when there is no link, and an empty
    // source is skipped — so the poster prints a gap either way.
    //
    // The difference is what happens afterwards. A blanked slot has forgotten
    // it was ever a QR, so a pharmacy that sets a booking link next week gets
    // nothing on any poster they already made until somebody thinks to press
    // this button again. Leaving the path in place means those posters start
    // working the moment the link exists, which is the same way the logo
    // behaves and the reason a canvas stores a path rather than an image.
    upsert(shapes, PHARMACY_BOOKING_QR_ID, {
      ...qrSlot,
      blobKey: BRAND_BOOKING_QR_SRC,
    });
  }

  return { ...doc, shapes };
}

/**
 * Whether the design already puts the pharmacy's address or phone on the page.
 *
 * <b>Why this is needed at all.</b> The footer below is only meant for a
 * template that says nothing about the pharmacy. The test for that used to be
 * "did any `{{token}}` get replaced", which is not the same question — a
 * template can show contact details as ordinary typed text, and twenty of the
 * twenty-four built-in ones did exactly that. Applying the details then left
 * the design's own contact line untouched and appended a second one, so a
 * poster went out with the address twice. It reached a real campaign before
 * anyone noticed.
 *
 * The templates now use tokens, which fixes those. This is the guard for
 * everything else: a template written in the admin editor, a design somebody
 * typed their address into by hand, a poster imported from elsewhere.
 *
 * Matching is on the values themselves rather than on shape ids or a guess at
 * layout, because the only reliable signal that the address is already on the
 * page is the address being on the page.
 */
function alreadyShowsContactDetails(
  shapes: Shape[],
  values: ReplacementValues,
): boolean {
  const normalise = (s: string) => s.toLowerCase().replace(/\s+/g, " ").trim();

  // Short values match too easily. A three-character address is not something
  // to suppress a footer over, and an empty one would match every shape.
  const needles = [values.address, values.contactPhone]
    .map(normalise)
    .filter((v) => v.length >= 6);

  if (needles.length === 0) return false;

  return shapes.some((shape) => {
    if (shape.kind !== "text") return false;
    // Our own footer does not count as the design already showing them —
    // otherwise re-applying would never refresh it.
    if (shape.id === PHARMACY_INFO_ID) return false;

    const haystack = normalise(shape.text);
    return needles.some((needle) => haystack.includes(needle));
  });
}

/** Replaces the shape with this id, or appends it. */
function upsert(shapes: Shape[], id: string, next: Shape): void {
  const at = shapes.findIndex((s) => s.id === id);
  if (at >= 0) shapes[at] = next;
  else shapes.push(next);
}
