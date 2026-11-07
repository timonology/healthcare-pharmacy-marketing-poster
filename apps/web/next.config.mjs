/**
 * Response headers (security review SEC-09).
 *
 * The CSP is deliberately the part that cannot break the app: it stops plugins,
 * <base> hijacks, forms posting elsewhere and — on every page but the kiosk —
 * other sites framing us (clickjacking). A script-src policy needs per-request
 * nonces through middleware, because Next.js inlines its bootstrap scripts; it
 * is a separate piece of work, not something to switch on with a guess.
 *
 * HSTS only matters over https; browsers ignore it on http://localhost.
 */
const baseCsp = "base-uri 'self'; object-src 'none'; form-action 'self'";

const securityHeaders = [
  { key: "X-Content-Type-Options", value: "nosniff" },
  { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
  { key: "Strict-Transport-Security", value: "max-age=31536000; includeSubDomains" },
  {
    key: "Permissions-Policy",
    value: "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
  },
];

/**
 * Everything except the kiosk may not be framed. The kiosk (/display) holds no
 * session and offers nothing to click a person into, and digital-signage
 * players commonly show a web page inside a frame — refusing that would break
 * screens for no gain. "/displays", the management page, stays protected.
 *
 * The logo route is left out too: it sets its own, stricter policy (sandboxed,
 * script-free, unframeable) and one CSP per response keeps that unambiguous.
 */
const NOT_KIOSK = "/((?!display(?:/|$)|api/brand-kit/logo$).*)";

/** @type {import('next').NextConfig} */
const nextConfig = {
  poweredByHeader: false,
  async headers() {
    return [
      { source: "/:path*", headers: securityHeaders },
      {
        source: NOT_KIOSK,
        headers: [
          { key: "Content-Security-Policy", value: `frame-ancestors 'none'; ${baseCsp}` },
          { key: "X-Frame-Options", value: "DENY" },
        ],
      },
      {
        source: "/display/:path*",
        headers: [{ key: "Content-Security-Policy", value: baseCsp }],
      },
    ];
  },
  reactStrictMode: true,
  // No floating "Next.js Dev Tools" button in development. It is a
  // <button aria-haspopup="menu"> in the corner of every page, which the
  // browser suite's account-menu lookup found first and opened instead of the
  // real one. Build errors still show in the overlay.
  devIndicators: false,
  // Emit a self-contained server in .next/standalone so the production image
  // doesn't need node_modules. Keeps the Docker image small enough to push
  // through registries without timing out.
  output: "standalone",
  // Standalone mode traces dependencies from this file's directory by default.
  // In a pnpm monorepo the workspace root is two levels up — point Next at it
  // so it picks up packages/shared-types and the hoisted node_modules.
  outputFileTracingRoot: new URL("../../", import.meta.url).pathname,
  transpilePackages: ["@acme/shared-types"],
  webpack: (config) => {
    config.externals = [...(config.externals || []), { canvas: "canvas" }];
    return config;
  },
};

export default nextConfig;
