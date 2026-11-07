"use client";

import { cn } from "@/lib/utils";

/**
 * Chart primitives for the admin dashboard.
 *
 * Hand-built SVG rather than a charting library, for two reasons:
 *
 *  1. Colours come from the app's CSS custom properties (`--primary`,
 *     `--muted`, …), which are HSL triples redefined under `.dark`. Because the
 *     values are referenced in markup rather than resolved in JavaScript, dark
 *     mode works with no theme-detection code and no flash on toggle — a real
 *     problem with canvas-based libraries, which have to read a computed colour
 *     at render time and re-render on theme change.
 *  2. These are distribution and trend visuals with a handful of points. A full
 *     charting dependency is a lot of bundle for two shapes.
 *
 * The component API is deliberately shaped like a chart library's (data arrays
 * of `{label, value}`), so moving to Recharts later is a change to this file
 * and nothing else.
 */

export interface Slice {
  label: string;
  value: number;
  /** CSS colour. Defaults walk the palette below. */
  color?: string;
  /**
   * Stable identity, when the label is not one.
   *
   * A label is a pharmacy name, and two accounts can share one — a chain with
   * a branch of the same name, two accounts mid-setup with no name yet, or
   * several rows falling back to the same placeholder. React then sees
   * duplicate keys and may reuse the wrong row, which on a ranked chart means
   * bars quietly attached to the wrong pharmacy.
   */
  id?: string;
}

/**
 * Series palette, in HSL-triple form so it composes with the theme.
 * Ordered for distinguishability rather than prettiness — adjacent slices in a
 * donut must not be confusable at a glance.
 */
const PALETTE = [
  "hsl(var(--primary))",
  "hsl(var(--brand-green))",
  "hsl(215 90% 60%)",
  "hsl(35 92% 55%)",
  "hsl(280 65% 60%)",
];

function colourAt(index: number, explicit?: string) {
  return explicit ?? PALETTE[index % PALETTE.length];
}

/* ------------------------------------------------------------------ *
 * Donut
 * ------------------------------------------------------------------ */

/**
 * Proportional donut with a centred total and a legend.
 *
 * Drawn with stroke-dasharray on concentric circles rather than arc paths:
 * far less trigonometry to get wrong, and it degrades sensibly when a slice
 * rounds to zero.
 */
export function DonutChart({
  data,
  total,
  centerLabel,
  className,
}: {
  data: Slice[];
  /** Override the centre figure. Defaults to the sum. */
  total?: number;
  centerLabel?: string;
  className?: string;
}) {
  const sum = data.reduce((acc, d) => acc + d.value, 0);
  const displayTotal = total ?? sum;

  // Geometry. r is chosen so the 100-unit circumference maths below is exact
  // enough for percentages without fighting floating point.
  const size = 160;
  const stroke = 22;
  const radius = (size - stroke) / 2;
  const circumference = 2 * Math.PI * radius;

  let offset = 0;

  return (
    <div className={cn("flex flex-wrap items-center gap-6", className)}>
      <div className="relative shrink-0" style={{ width: size, height: size }}>
        <svg
          width={size}
          height={size}
          viewBox={`0 0 ${size} ${size}`}
          role="img"
          aria-label={
            sum === 0
              ? "No data"
              : data.map((d) => `${d.label}: ${d.value}`).join(", ")
          }
        >
          {/* Track — also the entire visual when there is no data. */}
          <circle
            cx={size / 2}
            cy={size / 2}
            r={radius}
            fill="none"
            stroke="hsl(var(--muted))"
            strokeWidth={stroke}
          />
          {sum > 0 &&
            data.map((slice, i) => {
              if (slice.value <= 0) return null;
              const fraction = slice.value / sum;
              const length = fraction * circumference;
              const dash = `${length} ${circumference - length}`;
              // -90deg so the first slice starts at twelve o'clock, which is
              // what people expect when reading a donut.
              const rotation = (offset / sum) * 360 - 90;
              offset += slice.value;

              return (
                <circle
                  // Same rule as BarList below. Safe today — the only caller
                  // passes fixed plan names — but the type is shared, so the
                  // next caller with real entities inherits the defence rather
                  // than the bug.
                  key={slice.id ?? `${slice.label}#${i}`}
                  cx={size / 2}
                  cy={size / 2}
                  r={radius}
                  fill="none"
                  stroke={colourAt(i, slice.color)}
                  strokeWidth={stroke}
                  strokeDasharray={dash}
                  transform={`rotate(${rotation} ${size / 2} ${size / 2})`}
                  className="transition-all duration-500"
                />
              );
            })}
        </svg>

        <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
          <span className="text-2xl font-semibold tabular-nums">
            {displayTotal.toLocaleString()}
          </span>
          {centerLabel && (
            <span className="text-[11px] text-muted-foreground">{centerLabel}</span>
          )}
        </div>
      </div>

      <ul className="min-w-0 flex-1 space-y-2">
        {data.map((slice, i) => {
          const pct = sum > 0 ? Math.round((slice.value / sum) * 100) : 0;
          return (
            <li key={slice.id ?? `${slice.label}#${i}`} className="flex items-center gap-2 text-sm">
              <span
                aria-hidden
                className="h-2.5 w-2.5 shrink-0 rounded-full"
                style={{ background: colourAt(i, slice.color) }}
              />
              <span className="min-w-0 flex-1 truncate">{slice.label}</span>
              <span className="tabular-nums font-medium">{slice.value.toLocaleString()}</span>
              <span className="w-9 text-right tabular-nums text-xs text-muted-foreground">
                {pct}%
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/* ------------------------------------------------------------------ *
 * Bars
 * ------------------------------------------------------------------ */

/**
 * Horizontal bars for comparing a handful of categories.
 *
 * Horizontal rather than vertical because the labels are words ("Posters
 * created", a pharmacy name) — vertical bars would force rotated text, which is
 * measurably harder to read.
 */
export function BarList({
  data,
  className,
  emptyMessage = "Nothing to show yet.",
}: {
  data: Slice[];
  className?: string;
  emptyMessage?: string;
}) {
  const max = Math.max(...data.map((d) => d.value), 0);

  if (data.length === 0 || max === 0) {
    return (
      <p className={cn("py-6 text-center text-sm text-muted-foreground", className)}>
        {emptyMessage}
      </p>
    );
  }

  return (
    <ul className={cn("space-y-2.5", className)}>
      {data.map((item, i) => (
        // The id when the caller has one, otherwise the label with its
        // position appended — duplicates are then distinct rather than
        // colliding, and the list has no per-row state for an index-based key
        // to disturb.
        <li key={item.id ?? `${item.label}#${i}`} className="space-y-1">
          <div className="flex items-baseline justify-between gap-2 text-sm">
            <span className="min-w-0 truncate">{item.label}</span>
            <span className="shrink-0 tabular-nums font-medium">
              {item.value.toLocaleString()}
            </span>
          </div>
          <div className="h-2 overflow-hidden rounded-full bg-muted">
            <div
              className="h-full rounded-full transition-all duration-500"
              style={{
                // Scaled against the largest value, not the total: the question
                // is "how do these compare", not "what share of a whole".
                width: `${Math.max((item.value / max) * 100, 2)}%`,
                background: colourAt(i, item.color),
              }}
            />
          </div>
        </li>
      ))}
    </ul>
  );
}
