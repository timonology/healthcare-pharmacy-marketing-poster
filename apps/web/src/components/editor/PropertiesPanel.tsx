"use client";

import { useEffect, useRef, useState } from "react";
import type {
  CircleShape,
  ImageShape,
  LineShape,
  PageOrientation,
  PageSizeName,
  RectShape,
  Shape,
  ShapeId,
  TextShape,
} from "@acme/shared-types";
import {
  classifyPageSize,
  defaultSheet,
  DEFAULT_PRINT_FORMAT,
  PRINT_FORMATS,
  SIZES_FOR_FORMAT,
  type FoldPanels,
  type PrintFormat,
  DEFAULT_PAGE_SIZE,
  PAGE_SIZE_NAMES,
  pageDimensions,
  pageSizeLabel,
} from "@acme/shared-types";
import {
  PHARMACY_BOOKING_QR_ID,
  PHARMACY_LOGO_ID,
} from "@/lib/canvas/apply-pharmacy";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import { Slider } from "@/components/ui/slider";
import { prepareImage } from "@/lib/canvas/image-upload";
import { cn } from "@/lib/utils";
import { useEditorStore } from "@/store/editor-store";

export function PropertiesPanel() {
  const selectedId = useEditorStore((s) => s.selectedId);
  const shape = useEditorStore((s) =>
    selectedId ? s.doc.shapes.find((sh) => sh.id === selectedId) ?? null : null,
  );

  if (!shape) return <PageProperties />;

  return (
    <ScrollArea className="h-full">
      {/*
        Keyed by shape id so switching selection REMOUNTS the sections.

        The fields below are uncontrolled (defaultValue), which React only
        reads on mount. Without this key React reuses the same DOM nodes
        across selections, so picking a second text layer left the box showing
        the FIRST layer's text while edits were written to the second — which
        looks exactly like "this text won't edit".

        Uncontrolled is the right choice here: a controlled textarea driven by
        store updates on every keystroke fights the cursor. The key gives us
        correct reset behaviour without that cost.
      */}
      <div key={shape.id} className="space-y-4 p-3 text-sm">
        <CommonSection shape={shape} />
        <Separator />
        {shape.kind === "rect" && <RectSection shape={shape} />}
        {shape.kind === "circle" && <CircleSection shape={shape} />}
        {shape.kind === "text" && <TextSection shape={shape} />}
        {shape.kind === "line" && <LineSection shape={shape} />}
        {shape.kind === "image" && <ImageSection shape={shape} />}
      </div>
    </ScrollArea>
  );
}

function PageProperties() {
  const doc = useEditorStore((s) => s.doc);
  const setBackground = useEditorStore((s) => s.setBackground);
  const setPageSize = useEditorStore((s) => s.setPageSize);
  const setFormat = useEditorStore((s) => s.setFormat);

  // Posters made before the size selector existed (and any hand-resized ones)
  // may not match a paper size, so this can legitimately be null.
  const current = classifyPageSize(doc.width, doc.height);
  const orientation: PageOrientation = current?.orientation ?? "portrait";

  const applySize = (name: PageSizeName, o: PageOrientation) => {
    const dims = pageDimensions(name, o);
    setPageSize(dims.width, dims.height);
  };

  const format: PrintFormat = doc.format ?? DEFAULT_PRINT_FORMAT;
  const panels: FoldPanels = doc.foldPanels ?? 1;

  /**
   * Switching format moves to that format's usual sheet and fold, unless the
   * canvas is already a size that format uses.
   *
   * Choosing "Leaflet" ought to give you a leaflet, not a leaflet-shaped label
   * on an A3 sheet. But somebody who already picked A5 and then names it a
   * leaflet has made their choice, and resizing under them would throw away
   * work they can't get back.
   */
  const applyFormat = (next: PrintFormat) => {
    const sheet = defaultSheet(next);
    const alreadySuitable =
      current !== null && SIZES_FOR_FORMAT[next].includes(current.name);

    setFormat(next, alreadySuitable ? panels : sheet.panels);
    if (!alreadySuitable) applySize(sheet.name, sheet.orientation);
  };

  return (
    <ScrollArea className="h-full">
      <div className="space-y-4 p-3 text-sm">
        <div>
          <h3 className="font-medium">Page</h3>
          <p className="text-xs text-muted-foreground">
            {doc.width} × {doc.height} px
            {current ? ` · ${current.name} ${current.orientation}` : " · custom"}
          </p>
        </div>

        <Separator />

        <section className="space-y-2">
          <Label className="text-xs">Format</Label>
          <div className="grid grid-cols-3 gap-2">
            {PRINT_FORMATS.map((f) => (
              <button
                key={f}
                type="button"
                onClick={() => applyFormat(f)}
                aria-pressed={format === f}
                className={cn(
                  "rounded-md border px-2 py-1.5 text-xs transition-colors",
                  format === f
                    ? "border-primary bg-primary/10 text-primary"
                    : "border-input hover:bg-accent",
                )}
              >
                {f}
              </button>
            ))}
          </div>

          {format !== "Poster" && (
            <>
              <Label className="text-xs">Folds</Label>
              <div className="grid grid-cols-3 gap-2">
                {([1, 2, 3] as FoldPanels[]).map((n) => (
                  <button
                    key={n}
                    type="button"
                    onClick={() => setFormat(format, n)}
                    aria-pressed={panels === n}
                    title={
                      n === 1
                        ? "A flat sheet"
                        : n === 3
                          ? "Trifold — folds into three panels"
                          : "Folds in half"
                    }
                    className={cn(
                      "rounded-md border px-2 py-1.5 text-xs transition-colors",
                      panels === n
                        ? "border-primary bg-primary/10 text-primary"
                        : "border-input hover:bg-accent",
                    )}
                  >
                    {n === 1 ? "Flat" : `${n} panels`}
                  </button>
                ))}
              </div>
              <p className="text-[11px] text-muted-foreground">
                Fold lines show in the editor only — they are never printed.
              </p>
            </>
          )}

          <Separator />

          <Label className="text-xs">Size</Label>
          <div className="grid grid-cols-2 gap-2">
            {PAGE_SIZE_NAMES.map((name) => (
              <button
                key={name}
                type="button"
                onClick={() => applySize(name, orientation)}
                title={pageSizeLabel(name)}
                className={cn(
                  "rounded-md border px-2 py-1.5 text-xs transition-colors",
                  current?.name === name
                    ? "border-primary bg-primary/10 text-primary"
                    : "border-input hover:bg-accent",
                )}
              >
                {name}
              </button>
            ))}
          </div>

          <Label className="text-xs">Orientation</Label>
          <div className="grid grid-cols-2 gap-2">
            {(["portrait", "landscape"] as PageOrientation[]).map((o) => (
              <button
                key={o}
                type="button"
                onClick={() => applySize(current?.name ?? DEFAULT_PAGE_SIZE, o)}
                className={cn(
                  "rounded-md border px-2 py-1.5 text-xs capitalize transition-colors",
                  orientation === o
                    ? "border-primary bg-primary/10 text-primary"
                    : "border-input hover:bg-accent",
                )}
              >
                {o}
              </button>
            ))}
          </div>

          <p className="text-xs text-muted-foreground">
            Artwork is scaled to fit the new size. Undo reverses it.
          </p>
        </section>

        <Separator />
        <ColorRow label="Background" value={doc.background} onChange={setBackground} />
      </div>
    </ScrollArea>
  );
}

function CommonSection({ shape }: { shape: Shape }) {
  const updateShape = useEditorStore((s) => s.updateShape);
  const updateShapeLive = useEditorStore((s) => s.updateShapeLive);
  const commitHistory = useEditorStore((s) => s.commitHistory);

  return (
    <section className="space-y-3">
      <h3 className="font-medium capitalize">{shape.kind}</h3>

      <div className="grid grid-cols-2 gap-2">
        <NumberRow
          label="X"
          value={shape.position.x}
          onCommit={(v) =>
            updateShape(shape.id, { position: { ...shape.position, x: v } })
          }
        />
        <NumberRow
          label="Y"
          value={shape.position.y}
          onCommit={(v) =>
            updateShape(shape.id, { position: { ...shape.position, y: v } })
          }
        />
      </div>

      <NumberRow
        label="Rotation"
        value={shape.rotation}
        onCommit={(v) => updateShape(shape.id, { rotation: v })}
        suffix="°"
      />

      <div>
        <Label className="text-xs">Opacity</Label>
        <Slider
          value={[Math.round(shape.opacity * 100)]}
          min={0}
          max={100}
          step={1}
          onValueChange={([v]) =>
            updateShapeLive(shape.id, { opacity: (v ?? 0) / 100 })
          }
          onValueCommit={() => commitHistory()}
          className="mt-2"
        />
      </div>
    </section>
  );
}

function RectSection({ shape }: { shape: RectShape }) {
  const updateShape = useEditorStore((s) => s.updateShape);

  return (
    <section className="space-y-3">
      <div className="grid grid-cols-2 gap-2">
        <NumberRow
          label="Width"
          value={shape.width}
          onCommit={(v) => updateShape(shape.id, { width: v })}
        />
        <NumberRow
          label="Height"
          value={shape.height}
          onCommit={(v) => updateShape(shape.id, { height: v })}
        />
      </div>
      <NumberRow
        label="Corner radius"
        value={shape.cornerRadius ?? 0}
        onCommit={(v) => updateShape(shape.id, { cornerRadius: v })}
      />
      <ColorRow
        label="Fill"
        value={shape.fill}
        onChange={(c) => updateShape(shape.id, { fill: c })}
      />
      <ColorRow
        label="Stroke"
        value={shape.stroke ?? "#000000"}
        onChange={(c) => updateShape(shape.id, { stroke: c })}
      />
      <NumberRow
        label="Stroke width"
        value={shape.strokeWidth ?? 0}
        onCommit={(v) => updateShape(shape.id, { strokeWidth: v })}
      />
    </section>
  );
}

function CircleSection({ shape }: { shape: CircleShape }) {
  const updateShape = useEditorStore((s) => s.updateShape);
  return (
    <section className="space-y-3">
      <NumberRow
        label="Radius"
        value={shape.radius}
        onCommit={(v) => updateShape(shape.id, { radius: v })}
      />
      <ColorRow
        label="Fill"
        value={shape.fill}
        onChange={(c) => updateShape(shape.id, { fill: c })}
      />
      <ColorRow
        label="Stroke"
        value={shape.stroke ?? "#000000"}
        onChange={(c) => updateShape(shape.id, { stroke: c })}
      />
      <NumberRow
        label="Stroke width"
        value={shape.strokeWidth ?? 0}
        onCommit={(v) => updateShape(shape.id, { strokeWidth: v })}
      />
    </section>
  );
}

function TextSection({ shape }: { shape: TextShape }) {
  const updateShape = useEditorStore((s) => s.updateShape);
  const updateShapeLive = useEditorStore((s) => s.updateShapeLive);
  const commitHistory = useEditorStore((s) => s.commitHistory);

  return (
    <section className="space-y-3">
      <div>
        <Label className="text-xs">Text</Label>
        <textarea
          defaultValue={shape.text}
          onChange={(e) => updateShapeLive(shape.id, { text: e.currentTarget.value })}
          onBlur={() => commitHistory()}
          rows={3}
          className="mt-1 w-full rounded-md border border-input bg-background px-2 py-1 text-sm"
        />
        {/* The raw text keeps its {{tokens}} so they can fill in later; say
            what they are, rather than leave a pharmacist puzzling over them. */}
        {shape.text.includes("{{") && (
          <p className="mt-1 text-[11px] text-muted-foreground">
            Words in {"{{double braces}}"} fill in with your pharmacy&apos;s details from
            your profile, and show blank until those are added.
          </p>
        )}
      </div>
      <NumberRow
        label="Size"
        value={shape.fontSize}
        onCommit={(v) => updateShape(shape.id, { fontSize: v })}
      />
      <div>
        <Label className="text-xs">Font</Label>
        <Input
          defaultValue={shape.fontFamily}
          onBlur={(e) =>
            updateShape(shape.id, { fontFamily: e.currentTarget.value })
          }
          className="mt-1 h-8 text-sm"
        />
      </div>
      <ColorRow
        label="Color"
        value={shape.fill}
        onChange={(c) => updateShape(shape.id, { fill: c })}
      />
      <div>
        <Label className="text-xs">Align</Label>
        <div className="mt-1 flex gap-1">
          {(["left", "center", "right"] as const).map((align) => (
            <button
              key={align}
              type="button"
              onClick={() => updateShape(shape.id, { align })}
              className={`flex-1 rounded border px-2 py-1 text-xs capitalize ${
                shape.align === align
                  ? "border-primary bg-primary text-primary-foreground"
                  : "hover:bg-accent"
              }`}
            >
              {align}
            </button>
          ))}
        </div>
      </div>
    </section>
  );
}

function LineSection({ shape }: { shape: LineShape }) {
  const updateShape = useEditorStore((s) => s.updateShape);
  return (
    <section className="space-y-3">
      <ColorRow
        label="Stroke"
        value={shape.stroke}
        onChange={(c) => updateShape(shape.id, { stroke: c })}
      />
      <NumberRow
        label="Width"
        value={shape.strokeWidth}
        onCommit={(v) => updateShape(shape.id, { strokeWidth: v })}
      />
    </section>
  );
}

/**
 * The two shapes whose picture is not the pharmacist's to choose.
 *
 * <b>Keyed on the id, not the source.</b> These ids are exactly what
 * `applyPharmacyInfo` looks for, and what it does when it finds one is reset
 * `blobKey` to the brand path — unconditionally, because a slot that has
 * forgotten it was a logo never becomes one again. So "Replace image" on one of
 * these is a control that appears to work and whose result is wiped the next
 * time somebody applies pharmacy details: a picture chosen deliberately,
 * deleted by a button pressed elsewhere, with nothing said either time.
 *
 * Testing the source instead would have the control reappear the moment
 * somebody replaced the image, which is the one case it must not.
 */
const BRAND_SLOT_IDS: readonly string[] = [
  PHARMACY_LOGO_ID,
  PHARMACY_BOOKING_QR_ID,
];

function ImageSection({ shape }: { shape: ImageShape }) {
  const updateShape = useEditorStore((s) => s.updateShape);
  const fileRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const aspect = shape.width / Math.max(1, shape.height);

  const brandSlot = BRAND_SLOT_IDS.includes(shape.id);
  const isQr = shape.id === PHARMACY_BOOKING_QR_ID;

  async function onReplace(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      const next = await prepareImage(file);
      const newWidth = shape.width;
      const newHeight = Math.round(newWidth * (next.height / next.width));
      updateShape(shape.id, {
        blobKey: next.dataUrl,
        width: newWidth,
        height: newHeight,
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not load image.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="space-y-3">
      <div>
        <Label className="text-xs">Preview</Label>
        <div className="mt-1 flex items-center justify-center rounded-md border bg-muted p-2">
          {shape.blobKey ? (
            // eslint-disable-next-line @next/next/no-img-element
            <img
              src={shape.blobKey}
              alt=""
              className="max-h-32 max-w-full object-contain"
            />
          ) : (
            <span className="text-xs text-muted-foreground">No image</span>
          )}
        </div>
      </div>

      <div className="grid grid-cols-2 gap-2">
        <NumberRow
          label="Width"
          value={shape.width}
          onCommit={(v) =>
            updateShape(shape.id, {
              width: v,
              height: Math.round(v / aspect),
            })
          }
        />
        <NumberRow
          label="Height"
          value={shape.height}
          onCommit={(v) =>
            updateShape(shape.id, {
              height: v,
              width: Math.round(v * aspect),
            })
          }
        />
      </div>

      {brandSlot ? (
        // An explanation instead of a control. Size and position stay editable
        // above — those are the pharmacist's decisions, and `applyPharmacyInfo`
        // deliberately leaves them alone. Only the picture is ours.
        <p className="rounded-md border bg-muted/40 p-2 text-xs text-muted-foreground">
          {isQr ? (
            <>
              This code is built from the booking link on your profile, every
              time the poster is drawn. Change the link and every poster you have
              made points at the new one. Remove it and the code disappears
              rather than printing a square that scans to nothing.
            </>
          ) : (
            <>
              This is your pharmacy logo, taken from your brand kit every time
              the poster is drawn. Upload a new logo there and every poster you
              have made shows it.
            </>
          )}{" "}
          Move it and resize it freely — that part is yours.
        </p>
      ) : (
        <>
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="w-full"
            onClick={() => fileRef.current?.click()}
            disabled={busy}
          >
            {busy ? "Replacing…" : "Replace image"}
          </Button>
          <input
            ref={fileRef}
            type="file"
            accept="image/*"
            className="hidden"
            onChange={onReplace}
          />
        </>
      )}
      {error && (
        <p className="text-xs text-destructive" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}

// ---- low-level rows -------------------------------------------------------

function NumberRow({
  label,
  value,
  onCommit,
  suffix,
}: {
  label: string;
  value: number;
  onCommit: (v: number) => void;
  suffix?: string;
}) {
  // Keep internal text state so users can type freely; commit on blur/Enter.
  const [text, setText] = useState(formatNumber(value));
  useEffect(() => setText(formatNumber(value)), [value]);

  function commit() {
    const n = parseFloat(text);
    if (Number.isFinite(n)) onCommit(n);
    else setText(formatNumber(value));
  }

  return (
    <div>
      <Label className="text-xs">{label}{suffix ? ` (${suffix})` : ""}</Label>
      <Input
        value={text}
        onChange={(e) => setText(e.currentTarget.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") (e.currentTarget as HTMLInputElement).blur();
        }}
        inputMode="decimal"
        className="mt-1 h-8 text-sm"
      />
    </div>
  );
}

function ColorRow({
  label,
  value,
  onChange,
}: {
  label: string;
  value: string;
  onChange: (c: string) => void;
}) {
  return (
    <div>
      <Label className="text-xs">{label}</Label>
      <div className="mt-1 flex items-center gap-2">
        <Input
          type="color"
          value={value}
          onChange={(e) => onChange(e.currentTarget.value)}
          className="h-8 w-12 cursor-pointer p-1"
        />
        <Input
          value={value}
          onChange={(e) => onChange(e.currentTarget.value)}
          className="h-8 text-sm"
        />
      </div>
    </div>
  );
}

function formatNumber(n: number): string {
  return Number.isInteger(n) ? String(n) : n.toFixed(1);
}

// Suppress an unused-import warning if a section file doesn't reference all kinds.
export type _Unused = ShapeId;
