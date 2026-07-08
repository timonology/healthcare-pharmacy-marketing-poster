/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
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
