"use client";

import Link from "next/link";
import { ArrowLeft, Download, Redo2, Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Separator } from "@/components/ui/separator";
import type { SaveStatus } from "@/store/editor-store";
import { useEditorStore } from "@/store/editor-store";

interface EditorTopBarProps {
  onExport: () => void;
}

export function EditorTopBar({ onExport }: EditorTopBarProps) {
  const posterName = useEditorStore((s) => s.posterName);
  const setPosterName = useEditorStore((s) => s.setPosterName);
  const undo = useEditorStore((s) => s.undo);
  const redo = useEditorStore((s) => s.redo);
  const canUndo = useEditorStore((s) => s.past.length > 0);
  const canRedo = useEditorStore((s) => s.future.length > 0);
  const status = useEditorStore((s) => s.saveStatus);
  const lastSavedAtUtc = useEditorStore((s) => s.lastSavedAtUtc);

  return (
    <header className="flex h-14 shrink-0 items-center gap-3 border-b bg-background px-4">
      <Button asChild variant="ghost" size="sm">
        <Link href="/posters" aria-label="Back to posters">
          <ArrowLeft className="h-4 w-4" />
        </Link>
      </Button>

      <Input
        value={posterName}
        onChange={(e) => setPosterName(e.target.value)}
        className="h-8 max-w-xs"
        aria-label="Poster name"
      />

      <Separator orientation="vertical" className="h-6" />

      <div className="flex items-center gap-1">
        <Button
          size="sm"
          variant="ghost"
          onClick={undo}
          disabled={!canUndo}
          aria-label="Undo"
          title="Undo (⌘Z)"
        >
          <Undo2 className="h-4 w-4" />
        </Button>
        <Button
          size="sm"
          variant="ghost"
          onClick={redo}
          disabled={!canRedo}
          aria-label="Redo"
          title="Redo (⇧⌘Z)"
        >
          <Redo2 className="h-4 w-4" />
        </Button>
      </div>

      <SaveBadge status={status} lastSavedAtUtc={lastSavedAtUtc} />

      <div className="ml-auto flex items-center gap-2">
        <Button size="sm" variant="outline" onClick={onExport}>
          <Download className="mr-1.5 h-4 w-4" />
          Export PNG
        </Button>
      </div>
    </header>
  );
}

function SaveBadge({
  status,
  lastSavedAtUtc,
}: {
  status: SaveStatus;
  lastSavedAtUtc: string | null;
}) {
  const label =
    status === "saving" ? "Saving…"
    : status === "saved" ? `Saved${lastSavedAtUtc ? ` ${formatTime(lastSavedAtUtc)}` : ""}`
    : status === "dirty" ? "Unsaved changes"
    : status === "error" ? "Save failed"
    : "Saved";

  const dot =
    status === "saving" ? "bg-amber-500"
    : status === "error" ? "bg-destructive"
    : status === "dirty" ? "bg-amber-500"
    : "bg-emerald-500";

  return (
    <div
      role="status"
      aria-live="polite"
      className="flex items-center gap-2 rounded-md bg-muted px-2 py-1 text-xs text-muted-foreground"
    >
      <span className={`h-1.5 w-1.5 rounded-full ${dot}`} />
      {label}
    </div>
  );
}

function formatTime(iso: string) {
  try {
    return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  } catch {
    return "";
  }
}
