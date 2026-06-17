const MAX_DIMENSION = 1600;
const JPEG_QUALITY = 0.85;
const MAX_RAW_BYTES = 8 * 1024 * 1024;

interface PreparedImage {
  dataUrl: string;
  width: number;
  height: number;
}

export async function prepareImage(file: File): Promise<PreparedImage> {
  if (file.size > MAX_RAW_BYTES) {
    throw new Error(`Image is larger than ${MAX_RAW_BYTES / (1024 * 1024)} MB.`);
  }
  if (!file.type.startsWith("image/")) {
    throw new Error("File is not an image.");
  }

  const bitmap = await loadBitmap(file);
  const { width, height, scale } = scaleToFit(bitmap.width, bitmap.height);

  if (scale === 1 && file.type !== "image/heic" && file.type !== "image/heif") {
    const dataUrl = await readAsDataUrl(file);
    return { dataUrl, width, height };
  }

  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext("2d");
  if (!ctx) throw new Error("Canvas 2D context unavailable.");
  ctx.drawImage(bitmap, 0, 0, width, height);

  const outputType = pickOutputType(file.type);
  const dataUrl = canvas.toDataURL(outputType, JPEG_QUALITY);

  return { dataUrl, width, height };
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
  if (input === "image/png") return "image/png";
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
