"use client";

import {
  Circle,
  MousePointer2,
  Slash,
  Square,
  Type,
} from "lucide-react";
import { cn } from "@/lib/utils";
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
    </aside>
  );
}
