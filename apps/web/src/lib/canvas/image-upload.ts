const MAX_DIMENSION = 1600;
const JPEG_QUALITY = 0.85;
const MAX_RAW_BYTES = 8 * 1024 * 1024;

/**
 * Most characters one picture may add to a saved poster (security review
 * R2-12). The editor stores pictures inline, and the server refuses a poster
 * over about 1.9 MB, so a single phone photo passed through untouched (often
 * 3-5 MB as a data URL) used to make a poster impossible to save. Pictures over
 * this are re-encoded, and shrunk if they must be, until they fit.
 */
export const MAX_INLINE_CHARS = 700_000;

/** A file this small is kept byte-for-byte when its format allows it. */
const PASS_THROUGH_BYTES = 400 * 1024;

interface PreparedImage {
  dataUrl: string;
  width: number;
  height: number;
}

export async function prepareImage(
  file: File,
  options: {
    maxRawBytes?: number;
    /**
     * The picture is uploaded as a file, not stored inside the poster, so the
     * inline budget does not apply: keep full quality up to MAX_DIMENSION.
     */
    forUpload?: boolean;
  } = {},
): Promise<PreparedImage> {
  // The editor's own images keep the 8MB ceiling; the AI design photo accepts
  // the same 12MB as the admin upload, because a phone photo is often 9-10MB
  // before it is downscaled here.
  const maxRaw = options.maxRawBytes ?? MAX_RAW_BYTES;
  if (file.size > maxRaw) {
    throw new Error(`Image is larger than ${maxRaw / (1024 * 1024)} MB.`);
  }
  if (!file.type.startsWith("image/")) {
    throw new Error("File is not an image.");
  }

  const bitmap = await loadBitmap(file);
  if (!bitmap.width || !bitmap.height) {
    throw new Error("Could not read that image's size. Try a PNG or JPEG.");
  }
  const { width, height, scale } = scaleToFit(bitmap.width, bitmap.height);

  // Passed through untouched only for the formats the server prints as they
  // are. Anything else — SVG above all, which can reference other URLs — is
  // redrawn here into a plain bitmap, because the server refuses to render it
  // (security review SEC-01).
  const passThrough = ["image/jpeg", "image/png", "image/webp"].includes(file.type);
  const budget = options.forUpload ? Number.POSITIVE_INFINITY : MAX_INLINE_CHARS;
  if (scale === 1 && passThrough && (options.forUpload || file.size <= PASS_THROUGH_BYTES)) {
    const dataUrl = await readAsDataUrl(file);
    return { dataUrl, width, height };
  }

  // Re-encode, then shrink in steps until the picture fits its share of a
  // poster. A large PNG goes to WebP, which keeps transparency at a fraction
  // of the size; a browser that cannot encode WebP hands back PNG, and the
  // shrinking steps cover that.
  let outputType = pickOutputType(file.type);
  if (!options.forUpload && outputType === "image/png" && file.size > PASS_THROUGH_BYTES) {
    outputType = "image/webp";
  }

  let w = width;
  let h = height;
  let dataUrl = draw(bitmap, w, h, outputType);
  for (let step = 0; dataUrl.length > budget && step < 6; step++) {
    w = Math.max(1, Math.round(w * 0.8));
    h = Math.max(1, Math.round(h * 0.8));
    dataUrl = draw(bitmap, w, h, outputType);
  }
  if (dataUrl.length > budget) {
    throw new Error("That picture is too detailed to add. Try a smaller or simpler image.");
  }

  // The size it is placed at stays the original's proportions; only the
  // stored pixels shrank.
  return { dataUrl, width, height };
}

function draw(bitmap: HTMLImageElement, w: number, h: number, type: string): string {
  const canvas = document.createElement("canvas");
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("Canvas 2D context unavailable.");
  ctx.drawImage(bitmap, 0, 0, w, h);
  return canvas.toDataURL(type, JPEG_QUALITY);
}

function scaleToFit(w: number, h: number) {
  const longest = Math.max(w, h);
  if (longest <= MAX_DIMENSION) return { width: w, height: h, scale: 1 };
  const scale = MAX_DIMENSION / longest;
  return {
    width: Math.round(w * scale),
    height: Math.round(h * scale),
    scale,
  };
}

function pickOutputType(input: string): string {
  // Keeps transparency for formats that may have it.
  if (input === "image/png" || input === "image/svg+xml" || input === "image/gif") return "image/png";
  if (input === "image/webp") return "image/webp";
  return "image/jpeg";
}

function loadBitmap(file: File): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(file);
    const img = new window.Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      resolve(img);
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      reject(new Error("Could not decode image."));
    };
    img.src = url;
  });
}

function readAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(new Error("Could not read image."));
    reader.readAsDataURL(file);
  });
}
