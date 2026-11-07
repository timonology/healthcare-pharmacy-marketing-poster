/**
 * X's post length rule: 280, counted X's way.
 *
 * Mirrors `Acme.Domain.Social.XText` on the server exactly — change both
 * together. Latin and most scripts below U+1100 count 1, other characters
 * (Chinese, Japanese, Korean, emoji) count 2, and every link — including a
 * bare `pharmacy.co.uk` — counts 23. Errs on the side of caution where X's
 * own rules are intricate, so a caption this accepts is one X accepts.
 */

export const X_MAX_WEIGHTED_LENGTH = 280;
export const X_LINK_LENGTH = 23;

// Same pattern as the server: optional scheme, dotted labels ending in a
// 2+ letter TLD, optional port and path; not inside an email or a word.
const LINK =
  /(?<![@A-Za-z0-9_.\-])(?:https?:\/\/)?(?:[a-z0-9](?:[a-z0-9\-]*[a-z0-9])?\.)+[a-z]{2,}(?::\d{1,5})?(?:\/[^\s]*)?/gi;

function weight(cp: number): number {
  return cp <= 0x10ff ||
    (cp >= 0x2000 && cp <= 0x200d) ||
    (cp >= 0x2010 && cp <= 0x201f) ||
    (cp >= 0x2032 && cp <= 0x2037)
    ? 1
    : 2;
}

function codePoints(s: string): number[] {
  const out: number[] = [];
  for (const ch of s) out.push(ch.codePointAt(0)!);
  return out;
}

function isEmoji(cluster: string): boolean {
  return codePoints(cluster).some(
    (cp) =>
      (cp >= 0x1f000 && cp <= 0x1faff) ||
      (cp >= 0x2600 && cp <= 0x27bf) ||
      cp === 0xfe0f ||
      cp === 0x200d,
  );
}

function graphemes(s: string): string[] {
  if (typeof Intl !== "undefined" && "Segmenter" in Intl) {
    const seg = new Intl.Segmenter(undefined, { granularity: "grapheme" });
    return Array.from(seg.segment(s), (x) => x.segment);
  }
  return Array.from(s);
}

function plain(s: string): number {
  let total = 0;
  for (const g of graphemes(s)) {
    if (isEmoji(g)) {
      total += 2;
      continue;
    }
    for (const cp of codePoints(g)) total += weight(cp);
  }
  return total;
}

/** The text's length as X counts it. */
export function xWeightedLength(text: string | null | undefined): number {
  if (!text) return 0;
  const s = text.normalize("NFC");
  let total = 0;
  let last = 0;
  for (const m of s.matchAll(LINK)) {
    const at = m.index ?? 0;
    total += plain(s.slice(last, at)) + X_LINK_LENGTH;
    last = at + m[0].length;
  }
  return total + plain(s.slice(last));
}

/** True when X will take this text as a post. */
export function fitsX(text: string | null | undefined): boolean {
  return xWeightedLength(text) <= X_MAX_WEIGHTED_LENGTH;
}
