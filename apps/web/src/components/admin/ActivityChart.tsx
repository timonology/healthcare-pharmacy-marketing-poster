"use client";

import { useEffect, useState } from "react";
import {
  Area,
  AreaChart,
  CartesianGrid,
  Legend,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import type { AnalyticsPoint } from "@acme/shared-types";

/**
 * Platform activity over time.
 *
 * Recharts renders to SVG with colours resolved at render time, so unlike the
 * CSS-variable approach used elsewhere it cannot pick up a theme switch on its
 * own. `useThemeColours` below reads the computed values and re-reads them when
 * the `dark` class changes, which is what keeps the chart legible after a
 * toggle rather than leaving dark-on-dark lines.
 *
 * Stacked areas rather than lines: the three series are parts of one total
 * ("what did the platform produce"), and stacking answers that at a glance
 * while still showing each part.
 */
export function ActivityChart({
  series,
  height = 280,
}: {
  series: AnalyticsPoint[];
  height?: number;
}) {
  const colours = useThemeColours();

  // Recharts needs a real width to lay out; ResponsiveContainer supplies it,
  // but only once mounted. Rendering the chart on the server produces a
  // zero-width warning and a flash of empty space.
  const mounted = useMounted();
  if (!mounted) return <div style={{ height }} />;

  const data = series.map((p) => ({
    ...p,
    // Axis labels are "5 Aug", not the full ISO date — 30 of those overlap
    // into an unreadable smear.
    label: formatDay(p.date),
  }));

  return (
    <div style={{ height }}>
      <ResponsiveContainer width="100%" height="100%">
        <AreaChart data={data} margin={{ top: 8, right: 8, left: -18, bottom: 0 }}>
          <defs>
            {(["posters", "videos", "campaigns"] as const).map((key, i) => (
              <linearGradient
                key={key}
                id={`fill-${key}`}
                x1="0"
                y1="0"
                x2="0"
                y2="1"
              >
                <stop offset="0%" stopColor={colours.series[i]} stopOpacity={0.55} />
                <stop offset="100%" stopColor={colours.series[i]} stopOpacity={0.05} />
              </linearGradient>
            ))}
          </defs>

          <CartesianGrid
            strokeDasharray="3 3"
            stroke={colours.border}
            vertical={false}
          />
          <XAxis
            dataKey="label"
            tick={{ fill: colours.muted, fontSize: 11 }}
            tickLine={false}
            axisLine={{ stroke: colours.border }}
            // With 90 days every label would collide; let Recharts thin them.
            interval="preserveStartEnd"
            minTickGap={24}
          />
          <YAxis
            tick={{ fill: colours.muted, fontSize: 11 }}
            tickLine={false}
            axisLine={false}
            allowDecimals={false}
            width={44}
          />
          <Tooltip
            contentStyle={{
              background: colours.card,
              border: `1px solid ${colours.border}`,
              borderRadius: 8,
              fontSize: 12,
              color: colours.foreground,
            }}
            labelStyle={{ color: colours.foreground, fontWeight: 500 }}
          />
          <Legend
            wrapperStyle={{ fontSize: 12, color: colours.muted }}
            iconType="circle"
            iconSize={8}
          />

          <Area
            type="monotone"
            dataKey="posters"
            name="Posters"
            stackId="1"
            stroke={colours.series[0]}
            fill="url(#fill-posters)"
            strokeWidth={2}
          />
          <Area
            type="monotone"
            dataKey="videos"
            name="Videos"
            stackId="1"
            stroke={colours.series[1]}
            fill="url(#fill-videos)"
            strokeWidth={2}
          />
          <Area
            type="monotone"
            dataKey="campaigns"
            name="Campaigns"
            stackId="1"
            stroke={colours.series[2]}
            fill="url(#fill-campaigns)"
            strokeWidth={2}
          />
        </AreaChart>
      </ResponsiveContainer>
    </div>
  );
}

/** Avoids rendering the chart until the client has mounted. */
function useMounted() {
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);
  return mounted;
}

interface ThemeColours {
  border: string;
  muted: string;
  card: string;
  foreground: string;
  series: [string, string, string];
}

/**
 * Resolves the theme's HSL triples into concrete colours, re-reading on toggle.
 *
 * The variables hold bare triples ("185 72% 32%") for Tailwind's opacity
 * modifier syntax, so they need wrapping in hsl() to be usable as SVG
 * attributes.
 */
function useThemeColours(): ThemeColours {
  const [colours, setColours] = useState<ThemeColours>(FALLBACK);

  useEffect(() => {
    const read = () => {
      const style = getComputedStyle(document.documentElement);
      const v = (name: string, fallback: string) => {
        const raw = style.getPropertyValue(name).trim();
        return raw ? `hsl(${raw})` : fallback;
      };
      setColours({
        border: v("--border", FALLBACK.border),
        muted: v("--muted-foreground", FALLBACK.muted),
        card: v("--card", FALLBACK.card),
        foreground: v("--foreground", FALLBACK.foreground),
        series: [
          v("--primary", FALLBACK.series[0]),
          v("--brand-green", FALLBACK.series[1]),
          "hsl(215 90% 60%)",
        ],
      });
    };

    read();

    // next-themes toggles a class on <html>, which fires no event. Observing
    // the attribute is the only way to know the palette changed underneath us.
    const observer = new MutationObserver(read);
    observer.observe(document.documentElement, {
      attributes: true,
      attributeFilter: ["class"],
    });
    return () => observer.disconnect();
  }, []);

  return colours;
}

const FALLBACK: ThemeColours = {
  border: "hsl(200 22% 91%)",
  muted: "hsl(215 16% 47%)",
  card: "hsl(0 0% 100%)",
  foreground: "hsl(195 50% 14%)",
  series: ["hsl(185 72% 32%)", "hsl(158 64% 41%)", "hsl(215 90% 60%)"],
};

function formatDay(iso: string): string {
  const d = new Date(`${iso}T00:00:00Z`);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(undefined, {
    day: "numeric",
    month: "short",
    timeZone: "UTC",
  });
}
