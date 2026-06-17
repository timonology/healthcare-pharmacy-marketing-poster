"use client";

import { useRef, useState } from "react";
import {
  Circle,
  Image as ImageIcon,
  MousePointer2,
  Slash,
  Square,
  Type,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { prepareImage } from "@/lib/canvas/image-upload";
import { makeImage, nextZIndex } from "@/lib/canvas/shape-factories";
import type { EditorTool } from "@/store/editor-store";
import { useEditorStore } from "@/store/editor-store";

const TOOLS: { id: EditorTool; label: string; shortcut: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: "select", label: "Select", shortcut: "V", icon: MousePointer2 },
  { id: "rect", label: "Rectangle", shortcut: "R", icon: Square },
  { id: "circle", label: "Circle", shortcut: "C", icon: Circle },
  { id: "text", label: "Text", shortcut: "T", icon: Type },
  { id: "line", label: "Line", shortcut: "L", icon: Slash },
];

export function EditorToolbar() {
  const tool = useEditorStore((s) => s.tool);
  const setTool = useEditorStore((s) => s.setTool);
  const doc = useEditorStore((s) => s.doc);
  const addShape = useEditorStore((s) => s.addShape);
  const setSelected = useEditorStore((s) => s.setSelected);

  const fileRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;

    setBusy(true);
    setError(null);
    try {
      const { dataUrl, width, height } = await prepareImage(file);

      const longest = Math.max(width, height);
      const fit = longest > 600 ? 600 / longest : 1;
      const w = Math.round(width * fit);
      const h = Math.round(height * fit);

      const shape = makeImage(
        {
          x: Math.max(0, (doc.width - w) / 2),
          y: Math.max(0, (doc.height - h) / 2),
        },
        nextZIndex(doc.shapes),
        dataUrl,
        w,
        h,
      );
      addShape(shape);
      setSelected(shape.id);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not add image.");
      window.setTimeout(() => setError(null), 4000);
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside className="flex w-14 shrink-0 flex-col gap-1 border-r bg-background p-2">
      {TOOLS.map(({ id, label, shortcut, icon: Icon }) => (
        <button
          key={id}
          type="button"
          onClick={() => setTool(id)}
          aria-label={`${label} (${shortcut})`}
          title={`${label} (${shortcut})`}
          className={cn(
            "flex h-10 w-10 items-center justify-center rounded-md border text-muted-foreground transition-colors",
            tool === id
              ? "border-primary bg-primary text-primary-foreground"
              : "border-transparent hover:bg-accent hover:text-accent-foreground",
          )}
        >
          <Icon className="h-5 w-5" />
        </button>
      ))}

      <button
        type="button"
        onClick={() => fileRef.current?.click()}
        disabled={busy}
        aria-label="Add image"
        title={busy ? "Adding image…" : "Add image"}
        className={cn(
          "mt-2 flex h-10 w-10 items-center justify-center rounded-md border border-transparent text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:opacity-60",
        )}
      >
        <ImageIcon className="h-5 w-5" />
      </button>
      <input
        ref={fileRef}
        type="file"
        accept="image/*"
        onChange={handleFile}
        className="hidden"
      />

      {error && (
        <div
          role="alert"
          className="mt-2 rounded-md bg-destructive/10 px-1 py-1 text-[10px] leading-tight text-destructive"
        >
          {error}
        </div>
      )}
    </aside>
  );
}
