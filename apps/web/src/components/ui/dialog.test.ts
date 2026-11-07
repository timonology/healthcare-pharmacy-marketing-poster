import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

/**
 * Read from the source rather than rendered: the content is portalled by Radix
 * and its layout is the whole point, which no test DOM computes.
 */
const source = readFileSync(path.join(__dirname, "dialog.tsx"), "utf8");
// The dialog panel's own class list: the one string that centres it.
const contentClasses = source.match(/"fixed left-\[50%\] top-\[50%\][^"]*"/)?.[0] ?? "";

describe("a dialog taller than the window can still be used", () => {
  // Centred with a transform and no height limit, the create-video dialog with
  // four framed photos ran off both edges of a laptop screen — title and
  // Generate button unreachable, nothing to scroll.
  it("caps its height at the viewport", () => {
    expect(contentClasses).toMatch(/max-h-\[calc\(100dvh-[^\]]+\)\]/);
  });

  it("scrolls its own content", () => {
    expect(contentClasses).toContain("overflow-y-auto");
  });
});
