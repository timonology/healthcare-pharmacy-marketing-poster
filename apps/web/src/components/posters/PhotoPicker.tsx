"use client";

import { useRef, useState } from "react";
import { Check, Loader2, Search } from "lucide-react";
import type {
  PhotoOptions,
  PosterImage,
  PosterPhotoMode,
  StockPhoto,
} from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { prepareImage } from "@/lib/canvas/image-upload";
import { cn } from "@/lib/utils";
import {
  ACCEPTED_PHOTO_TYPES,
  MAX_PHOTO_BYTES,
  checkPhotoFile,
  type PhotoChoice,
} from "@/lib/poster-photo";

const MODES: { mode: PosterPhotoMode; label: string; hint: string }[] = [
  { mode: "None", label: "None", hint: "A typographic, geometric design in the house style." },
  {
    mode: "Stock",
    label: "Stock",
    hint: "A stock photograph. Pick one, or leave it to the designer to match the brief.",
  },
  { mode: "Upload", label: "Upload", hint: "Your own photo. The design is built around it." },
  { mode: "Ai", label: "AI image", hint: "A photograph generated for this poster." },
];

/**
 * The photo choice for "Design with AI": None, Stock, Upload or AI.
 *
 * Shared by the admin designer and the pharmacy dialog so both offer exactly
 * the same choices and rules.
 *
 * <b>AI is only shown when it works.</b> Without an image model it is hidden
 * and a line says why — a mode that is offered and then fails, or quietly
 * returns a stock photo, is worse than one that is not there.
 *
 * <b>Uploads go up on pick.</b> The pharmacist sees the thumbnail, or the
 * reason it was refused, straight away — not a minute into a failed design.
 */
export function PhotoPicker({
  value,
  onChange,
  options,
  search,
  upload,
  aiPreview,
  onUploadingChange,
  disabled,
}: {
  value: PhotoChoice;
  onChange: (next: PhotoChoice) => void;
  /** Null while loading: AI stays hidden until the service says it exists. */
  options: PhotoOptions | null;
  search: (query: string) => Promise<StockPhoto[]>;
  upload: (file: File) => Promise<{ url: string }>;
  /**
   * Admin only: make the AI image now, so it can be seen and rejected before
   * a layout is built around it. Each call is a separate charge, so the
   * pharmacy dialog does not pass this — its AI photo is made inside the
   * design, under the same allowance.
   */
  aiPreview?: (description: string) => Promise<PosterImage>;
  onUploadingChange?: (uploading: boolean) => void;
  disabled?: boolean;
}) {
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<StockPhoto[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [previewing, setPreviewing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileInput = useRef<HTMLInputElement | null>(null);

  const aiAvailable = options?.ai === true;
  const modes = MODES.filter((m) => m.mode !== "Ai" || aiAvailable);
  const current = MODES.find((m) => m.mode === value.mode) ?? MODES[1];

  function pick(mode: PosterPhotoMode) {
    setError(null);
    // A switch discards the previous pick: leaving a stale photo behind would
    // design around an image the user thought they had replaced.
    onChange({ mode });
  }

  async function runSearch() {
    const q = query.trim();
    if (q.length < 2) return;
    setSearching(true);
    setError(null);
    try {
      setResults(await search(q));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Search failed.");
      setResults(null);
    } finally {
      setSearching(false);
    }
  }

  async function onPickFile(file: File | undefined) {
    if (!file) return;
    // The bad file is refused here, before anything is uploaded or generated.
    const problem = checkPhotoFile(file);
    if (problem) {
      setError(problem);
      onChange({ mode: "Upload" });
      if (fileInput.current) fileInput.current.value = "";
      return;
    }
    setUploading(true);
    onUploadingChange?.(true);
    setError(null);
    try {
      // Downscaled in the browser first: a phone photo carries far more
      // detail than an A4 poster prints, and it makes the upload instant.
      const prepared = await prepareImage(file, { maxRawBytes: MAX_PHOTO_BYTES, forUpload: true });
      const blob = await (await fetch(prepared.dataUrl)).blob();
      const stored = await upload(new File([blob], file.name, { type: blob.type }));
      onChange({
        mode: "Upload",
        assetUrl: stored.url,
        previewUrl: stored.url,
        source: "upload",
        description: value.description,
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not upload that photo.");
      onChange({ mode: "Upload" });
    } finally {
      setUploading(false);
      onUploadingChange?.(false);
      if (fileInput.current) fileInput.current.value = "";
    }
  }

  async function makePreview() {
    if (!aiPreview) return;
    const description = value.description?.trim() ?? "";
    if (description.length < 4) return;
    setPreviewing(true);
    setError(null);
    try {
      const image = await aiPreview(description);
      onChange({
        mode: "Ai",
        description,
        assetUrl: image.url,
        previewUrl: image.url,
        source: "ai",
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not make that image.");
    } finally {
      setPreviewing(false);
    }
  }

  return (
    <div className="space-y-2 rounded-md border border-border/60 p-3">
      <Label className="text-xs">Photograph</Label>
      <div
        role="radiogroup"
        aria-label="Photograph"
        className={cn("grid gap-1.5", modes.length === 4 ? "grid-cols-4" : "grid-cols-3")}
      >
        {modes.map((m) => (
          <button
            key={m.mode}
            type="button"
            role="radio"
            aria-checked={value.mode === m.mode}
            disabled={disabled}
            onClick={() => pick(m.mode)}
            className={cn(
              "rounded-md border px-2 py-1.5 text-xs transition-colors",
              value.mode === m.mode
                ? "border-primary bg-primary/10 text-primary"
                : "border-input hover:bg-accent",
            )}
          >
            {m.label}
          </button>
        ))}
      </div>
      <p className="text-[11px] text-muted-foreground">{current.hint}</p>
      {options !== null && !aiAvailable && (
        <p className="text-[11px] text-muted-foreground">
          AI images aren&apos;t set up on this service, so that option is hidden.
        </p>
      )}

      {value.mode === "Stock" && (
        <div className="space-y-2">
          <form
            className="flex gap-1.5"
            onSubmit={(e) => {
              e.preventDefault();
              void runSearch();
            }}
          >
            <Input
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              placeholder="e.g. pharmacist talking to a patient"
              className="h-8 text-xs"
              aria-label="Search stock photos"
              disabled={disabled}
            />
            <Button
              type="submit"
              size="sm"
              variant="outline"
              className="h-8"
              disabled={disabled || searching || query.trim().length < 2}
              aria-label="Search"
            >
              {searching ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
            </Button>
          </form>
          {results && results.length === 0 && (
            <p className="text-[11px] text-muted-foreground">No photos matched. Try simpler words.</p>
          )}
          {results && results.length > 0 && (
            <div className="grid max-h-56 grid-cols-4 gap-1.5 overflow-y-auto">
              {results.slice(0, 16).map((photo) => {
                const chosen = value.stockPhotoId === photo.id;
                return (
                  <button
                    key={photo.id}
                    type="button"
                    disabled={disabled}
                    onClick={() =>
                      onChange(
                        chosen
                          ? { mode: "Stock" }
                          : {
                              mode: "Stock",
                              stockPhotoId: photo.id,
                              description: photo.alt || undefined,
                              previewUrl: photo.thumbnailUrl,
                              source: "stock",
                            },
                      )
                    }
                    aria-pressed={chosen}
                    title={photo.alt || "Stock photo"}
                    className={cn(
                      "relative aspect-square overflow-hidden rounded border",
                      chosen ? "border-primary ring-2 ring-primary" : "border-border/60",
                    )}
                  >
                    {/* The provider's thumbnail, for choosing only. The chosen
                        photo is copied into our storage when the poster is made. */}
                    {/* eslint-disable-next-line @next/next/no-img-element */}
                    <img src={photo.thumbnailUrl} alt={photo.alt} className="h-full w-full object-cover" />
                    {chosen && (
                      <span className="absolute right-1 top-1 rounded-full bg-primary p-0.5 text-primary-foreground">
                        <Check className="h-3 w-3" />
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
          )}
          <p className="text-[11px] text-muted-foreground">
            {value.stockPhotoId
              ? "Using the photo you picked."
              : "Nothing picked: the designer finds a photo to suit the brief."}
          </p>
        </div>
      )}

      {value.mode === "Upload" && (
        <div className="space-y-2">
          <input
            ref={fileInput}
            type="file"
            accept={ACCEPTED_PHOTO_TYPES.join(",")}
            className="hidden"
            aria-label="Photo file"
            onChange={(e) => void onPickFile(e.target.files?.[0])}
          />
          {value.assetUrl ? (
            <div className="flex items-center gap-2">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={value.previewUrl ?? value.assetUrl}
                alt="Uploaded"
                className="h-16 w-16 rounded border border-border/60 object-cover"
              />
              <button
                type="button"
                onClick={() => fileInput.current?.click()}
                className="text-[11px] text-primary hover:underline"
                disabled={disabled || uploading}
              >
                Replace
              </button>
            </div>
          ) : (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="w-full text-xs"
              disabled={disabled || uploading}
              onClick={() => fileInput.current?.click()}
            >
              {uploading ? "Uploading…" : "Choose a photo"}
            </Button>
          )}
          <Label className="text-xs" htmlFor="photo-description">
            What&apos;s in the photo? <span className="text-muted-foreground">(recommended)</span>
          </Label>
          <Input
            id="photo-description"
            value={value.description ?? ""}
            onChange={(e) => onChange({ ...value, description: e.target.value })}
            placeholder="e.g. our pharmacist at the counter"
            maxLength={300}
            className="h-8 text-xs"
            disabled={disabled}
          />
          <p className="text-[11px] text-muted-foreground">
            JPEG, PNG or WebP, up to 12MB. The designer can&apos;t see the photo, so
            describing it means it is placed and cropped well — never stretched.
          </p>
        </div>
      )}

      {value.mode === "Ai" && aiAvailable && (
        <div className="space-y-2">
          <Label className="text-xs" htmlFor="ai-photo-description">
            What should the photo show? <span className="text-destructive">*</span>
          </Label>
          <Input
            id="ai-photo-description"
            value={value.description ?? ""}
            onChange={(e) => onChange({ mode: "Ai", description: e.target.value })}
            placeholder="e.g. a pharmacist giving a flu jab to an older man"
            maxLength={300}
            className="h-8 text-xs"
            disabled={disabled}
          />
          {aiPreview && (
            <div className="flex items-center gap-2">
              {value.assetUrl && (
                // eslint-disable-next-line @next/next/no-img-element
                <img
                  src={value.assetUrl}
                  alt={value.description ?? "Generated"}
                  className="h-16 w-16 rounded border border-border/60 object-cover"
                />
              )}
              <Button
                type="button"
                size="sm"
                variant="outline"
                className="text-xs"
                disabled={disabled || previewing || (value.description?.trim().length ?? 0) < 4}
                onClick={() => void makePreview()}
              >
                {previewing ? "Generating…" : value.assetUrl ? "Try another" : "Preview image"}
              </Button>
            </div>
          )}
          <p className="text-[11px] text-muted-foreground">
            {aiPreview
              ? "Preview it first if you like; otherwise it is made with the design."
              : "Made with the design, as part of the same AI design — no extra charge."}{" "}
            Avoid naming real people or brands.
          </p>
        </div>
      )}

      {error && (
        <p className="text-xs text-destructive" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}
