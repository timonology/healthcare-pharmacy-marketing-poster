import { ImageResponse } from "next/og";
import { BRAND_SPARK, BRAND_TEAL, MARK_PATH } from "@/components/brand/LogoMark";
import { SITE_NAME, SITE_TAGLINE } from "@/lib/site";

/**
 * The picture shown when a link to the site is shared (Facebook, LinkedIn,
 * WhatsApp, X, Slack). Drawn from text, in the house colours, so it needs no
 * asset and cannot go stale against a redesigned poster.
 */
export const alt = `${SITE_NAME} — ${SITE_TAGLINE} for UK community pharmacies`;
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

const NAVY = "#0b2e59";
const TEAL = "#0fa3b1";
const SKY = "#e8f5fb";

const MAKES = ["Posters", "Videos", "Email campaigns", "Social posts", "In-store screens"];

export default function OpenGraphImage() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          flexDirection: "column",
          justifyContent: "space-between",
          padding: "72px 80px",
          background: `linear-gradient(135deg, #ffffff 0%, ${SKY} 100%)`,
          color: NAVY,
          fontFamily: "sans-serif",
        }}
      >
        <div style={{ display: "flex", alignItems: "center", gap: 18 }}>
          {/* The Marketing25 mark (components/brand/LogoMark.tsx). */}
          <svg width="64" height="64" viewBox="0 0 64 64">
            <rect width="64" height="64" rx="16" fill={BRAND_TEAL} />
            <path d={MARK_PATH} fill="#FFFFFF" stroke="#FFFFFF" strokeWidth="4" strokeLinejoin="round" />
            <circle cx="51" cy="13" r="4" fill={BRAND_SPARK} />
          </svg>
          <div style={{ fontSize: 34, fontWeight: 700 }}>{SITE_NAME}</div>
        </div>

        <div style={{ display: "flex", flexDirection: "column" }}>
          <div style={{ fontSize: 84, fontWeight: 800, lineHeight: 1.05, letterSpacing: -2 }}>
            Professional pharmacy
          </div>
          <div style={{ fontSize: 84, fontWeight: 800, lineHeight: 1.05, color: TEAL, letterSpacing: -2 }}>
            marketing
          </div>
          <div style={{ marginTop: 22, fontSize: 30, color: "#334155" }}>
            For UK community pharmacies. No design skills needed.
          </div>
        </div>

        <div style={{ display: "flex", gap: 14, flexWrap: "wrap" }}>
          {MAKES.map((m) => (
            <div
              key={m}
              style={{
                display: "flex",
                padding: "10px 22px",
                borderRadius: 999,
                background: "#ffffff",
                border: `2px solid ${TEAL}`,
                fontSize: 26,
                color: NAVY,
              }}
            >
              {m}
            </div>
          ))}
        </div>
      </div>
    ),
    size,
  );
}
