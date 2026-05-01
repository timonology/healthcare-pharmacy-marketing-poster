"use client";

import { useEffect, useState } from "react";
import type {
  CircleShape,
  LineShape,
  RectShape,
  Shape,
  ShapeId,
  TextShape,
} from "@acme/shared-types";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import { Slider } from "@/components/ui/slider";
import { useEditorStore } from "@/store/editor-store";

export function PropertiesPanel() {
  const selectedId = useEditorStore((s) => s.selectedId);
  const shape = useEditorStore((s) =>
    selectedId ? s.doc.shapes.find((sh) => sh.id === selectedId) ?? null : null,
  );

  if (!shape) return <PageProperties />;

  return (
    <ScrollArea className="h-full">
      <div className="space-y-4 p-3 text-sm">
        <CommonSection shape={shape} />
        <Separator />
        {shape.kind === "rect" && <RectSection shape={shape} />}
        {shape.kind === "circle" && <CircleSection shape={shape} />}
        {shape.kind === "text" && <TextSection shape={shape} />}
        {shape.kind === "line" && <LineSection shape={shape} />}
      </div>
    </ScrollArea>
  );
}

function PageProperties() {
  const doc = useEditorStore((s) => s.doc);
  const setBackground = useEditorStore((s) => s.setBackground);

  return (
    <ScrollArea className="h-full">
      <div className="space-y-4 p-3 text-sm">
        <div>
          <h3 className="font-medium">Page</h3>
          <p className="text-xs text-muted-foreground">
            {doc.width} × {doc.height} px
          </p>
        </div>
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
