import path from "node:path";
import { defineConfig } from "vitest/config";

/**
 * Web unit and regression tests.
 *
 * Node environment, deliberately: no jsdom or happy-dom dependency. Pure
 * modules need nothing, and component tests import `src/test/dom.ts`, a small
 * DOM that is exactly what React touches — enough to mount, commit, run ref
 * callbacks and effects, and dispatch the handlers a test calls. It is how the
 * "Maximum update depth exceeded" crash in the photo framer was reproduced in
 * the first place, so it is known to catch the bugs these tests exist for.
 */
export default defineConfig({
  esbuild: { jsx: "automatic" },
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "src"),
      "@acme/shared-types": path.resolve(__dirname, "../../packages/shared-types/src/index.ts"),
    },
  },
  test: {
    environment: "node",
    include: ["src/**/*.test.{ts,tsx}"],
  },
});
