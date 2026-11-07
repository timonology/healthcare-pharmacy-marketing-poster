// No "use client": this is pure markup (no state, no effects, no handlers),
// so a server component can render it and ship only the SVG. The home page
// draws seven posters with it; as a client component every canvas would also
// travel to the browser a second time as serialized props.
import { BRAND_BOOKING_QR_SRC, BRAND_LOGO_SRC } from "@acme/shared-types";
import type { CanvasDocument, Shape } from "@acme/shared-types";
import { displayTokens, type TokenValues } from "@/lib/canvas/display-tokens";

interface CanvasPreviewProps {
  doc: CanvasDocument;
  className?: string;
  background?: string;
  /**
   * For a pharmacy's own poster: draw `{{tokens}}` with these details, or
   * blank. Leave unset for a template, whose card shows labels ("Your
   * address") because it is describing the template, not one copy of it.
   * `null` means "a poster, details still loading": blanks, never raw tokens.
   */
  tokenValues?: TokenValues | null;
  /**
   * Leave out the pharmacy logo and booking QR. For a visitor who is not
   * signed in there is no brand kit to draw them from: the requests would be
   * refused and the slots left empty anyway, so they are not made.
   */
  withoutBrandAssets?: boolean;
}

const BRAND_SOURCES = new Set([BRAND_BOOKING_QR_SRC, BRAND_LOGO_SRC]);

export function CanvasPreview({
  doc,
  className,
  background,
  tokenValues,
  withoutBrandAssets = false,
}: CanvasPreviewProps) {
  const textOf = (text: string) =>
    tokenValues === undefined ? previewText(text) : displayTokens(text, tokenValues);
  const sorted = [...doc.shapes]
    .filter((s) => !(withoutBrandAssets && s.kind === "image" && BRAND_SOURCES.has(s.blobKey)))
    .sort((a, b) => a.zIndex - b.zIndex);

  return (
    <svg
      viewBox={`0 0 ${doc.width} ${doc.height}`}
      preserveAspectRatio="xMidYMid meet"
      className={className}
      style={{ display: "block", background: background ?? doc.background }}
      role="img"
      aria-label={`Canvas preview: ${doc.name}`}
    >
      {sorted.map((shape) => (
        <ShapeSvg key={shape.id} shape={shape} textOf={textOf} />
      ))}
    </svg>
  );
}

/**
 * What an unfilled `{{token}}` should look like in a preview.
 *
 * <b>Templates carry their contact line as tokens.</b> A poster fills them when
 * it is created, so a preview of a *poster* never sees one. A preview of a
 * *template* always does — the gallery draws the template's own canvas, and the
 * built-in templates have no uploaded thumbnail to draw instead.
 *
 * Without this, every card in the gallery read "{{address}}" across the bottom,
 * to signed-in pharmacies and to guests browsing the public library alike.
 *
 * Resolved to a label rather than to the viewer's real details, because the
 * gallery is browsable signed out and because the card is showing what the
 * template is, not what one pharmacy's copy of it would say.
 */
const PREVIEW_LABELS: Record<string, string> = {
  pharmacy_name: "Your pharmacy",
  pharmacyname: "Your pharmacy",
  address: "Your address",
  post_code: "Postcode",
  postcode: "Postcode",
  contact_name: "Your name",
  contactname: "Your name",
  phone: "Your phone",
};

function previewText(text: string): string {
  if (!text.includes("{{")) return text;

  return text.replace(
    /\{\{\s*(pharmacy[_ ]?name|address|post[_ ]?code|contact[_ ]?name|phone)\s*\}\}/gi,
    (whole, token: string, offset: number) => {
      const label = PREVIEW_LABELS[token.toLowerCase().replace(/ /g, "_")];
      if (!label) return whole;

      // "Welcome to Your pharmacy" is the kind of small wrongness a customer
      // notices on a gallery card, so the label is only capitalised when it
      // starts the line. Mid-sentence it stays lower case.
      return offset === 0 ? label : label.charAt(0).toLowerCase() + label.slice(1);
    },
  );
}

function ShapeSvg({ shape, textOf }: { shape: Shape; textOf: (text: string) => string }) {
  const transform = `translate(${shape.position.x} ${shape.position.y}) rotate(${shape.rotation}) scale(${shape.scale.x} ${shape.scale.y})`;

  switch (shape.kind) {
    case "rect":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <rect
            width={shape.width}
            height={shape.height}
            fill={shape.fill}
            stroke={shape.stroke}
            strokeWidth={shape.strokeWidth ?? 0}
            rx={shape.cornerRadius ?? 0}
            ry={shape.cornerRadius ?? 0}
          />
        </g>
      );

    case "circle":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <circle
            r={shape.radius}
            fill={shape.fill}
            stroke={shape.stroke}
            strokeWidth={shape.strokeWidth ?? 0}
          />
        </g>
      );

    case "line": {
      const pts: string[] = [];
      for (let i = 0; i + 1 < shape.points.length; i += 2) {
        pts.push(`${shape.points[i]},${shape.points[i + 1]}`);
      }
      const points = pts.join(" ");
      return (
        <g transform={transform} opacity={shape.opacity}>
          {shape.closed ? (
            <polygon
              points={points}
              fill={shape.stroke}
              stroke={shape.stroke}
              strokeWidth={shape.strokeWidth}
            />
          ) : (
            <polyline
              points={points}
              fill="none"
              stroke={shape.stroke}
              strokeWidth={shape.strokeWidth}
              strokeLinecap="round"
            />
          )}
        </g>
      );
    }

    case "text":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <foreignObject
            x={0}
            y={0}
            width={shape.width ?? 99999}
            height={99999}
          >
            <div
              style={{
                fontSize: `${shape.fontSize}px`,
                fontFamily: shape.fontFamily,
                color: shape.fill,
                textAlign: shape.align ?? "left",
                lineHeight: 1.15,
                // pre-wrap, not normal: house templates set their line breaks
                // with "\n" so the editor and the PDF wrap alike. "normal"
                // folded them into spaces and every card re-wrapped its
                // headline differently from the poster you would get.
                whiteSpace: shape.width ? "pre-wrap" : "pre",
                wordBreak: "break-word",
                margin: 0,
              }}
            >
              {textOf(shape.text)}
            </div>
          </foreignObject>
        </g>
      );

    case "image":
      return (
        <g transform={transform} opacity={shape.opacity}>
          <image
            href={shape.blobKey}
            width={shape.width}
            height={shape.height}
            preserveAspectRatio="xMidYMid meet"
          />
        </g>
      );

    case "group":
      return null;
  }
}
