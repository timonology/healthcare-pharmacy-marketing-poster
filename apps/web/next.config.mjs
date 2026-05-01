/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  transpilePackages: ["@acme/shared-types"],
  experimental: {
    typedRoutes: true,
  },
  // Konva pulls in `canvas` for SSR; we render canvas client-side only.
  webpack: (config) => {
    config.externals = [...(config.externals || []), { canvas: "canvas" }];
    return config;
  },
};

export default nextConfig;
