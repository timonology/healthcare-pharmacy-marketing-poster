import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

/**
 * A refused action looked like a dead button.
 *
 * The dialog's error rendered at the top of a scrolling dialog, usually off
 * screen. The pharmacist pressed the button, nothing visible happened, and the
 * sentence explaining why sat above the fold. The fix was placement: the
 * form's `error` goes after the last field, next to the button that was
 * refused. This holds every dialog to that, including ones not written yet.
 *
 * Scope: the form-level `{error && (` in a component with a `<DialogFooter>`.
 * Errors named for one control (`imageError` beside the photo picker) belong
 * next to that control and are not checked.
 */

const SRC = path.resolve(__dirname, "../..");
const FIELDS = /<(Input|Textarea|Select|PhotoFramer|input|textarea|select)\b/;

function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);
    if (statSync(full).isDirectory()) return name === "node_modules" ? [] : files(full);
    return full.endsWith(".tsx") && !full.endsWith(".test.tsx") ? [full] : [];
  });
}

/** Form errors rendered above a field that still follows before the buttons. */
export function misplacedErrors(source: string): number[] {
  const lines: number[] = [];
  for (const m of source.matchAll(/\{error && \(/g)) {
    const at = m.index ?? 0;
    const footer = source.indexOf("<DialogFooter", at);
    if (footer < 0) continue;
    const between = source.slice(at, footer);
    // A page-level error before a dialog opens is not this dialog's error.
    if (between.includes("<DialogContent")) continue;
    if (FIELDS.test(between)) lines.push(source.slice(0, at).split("\n").length);
  }
  return lines;
}

describe("a refusal is shown where the pharmacist is looking", () => {
  it("every dialog's error sits after its last field, beside the buttons", () => {
    const offenders = files(SRC)
      .map((f) => ({ f: path.relative(SRC, f), lines: misplacedErrors(readFileSync(f, "utf8")) }))
      .filter((o) => o.lines.length);

    expect(offenders).toEqual([]);
  });

  it("the check itself catches an error placed above the fields", () => {
    const bad = `{error && (<p role="alert">{error}</p>)}\n<Input id="name" />\n<DialogFooter>`;
    const good = `<Input id="name" />\n{error && (<p role="alert">{error}</p>)}\n<DialogFooter>`;

    expect(misplacedErrors(bad)).toEqual([1]);
    expect(misplacedErrors(good)).toEqual([]);
  });
});
