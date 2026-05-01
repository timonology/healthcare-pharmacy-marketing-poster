"use client";

import {
  ArrowDown,
  ArrowDownToLine,
  ArrowUp,
  ArrowUpToLine,
  Circle,
  Copy,
  Image as ImageIcon,
  Slash,
  Square,
  Trash2,
  Type,
} from "lucide-react";
import type { Shape } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Separator } from "@/components/ui/separator";
import { cn } from "@/lib/utils";
import { useEditorStore } from "@/store/editor-store";

export function LayersPanel() {
  const shapes = useEditorStore((s) => s.doc.shapes);
  const selectedId = useEditorStore((s) => s.selectedId);
  const setSelected = useEditorStore((s) => s.setSelected);
  const remove = useEditorStore((s) => s.removeShape);
  const duplicate = useEditorStore((s) => s.duplicateShape);
  const bringForward = useEditorStore((s) => s.bringForward);
  const sendBackward = useEditorStore((s) => s.sendBackward);
  const bringToFront = useEditorStore((s) => s.bringToFront);
  const sendToBack = useEditorStore((s) => s.sendToBack);

  // Top-most layer first (highest zIndex).
  const ordered = [...shapes].sort((a, b) => b.zIndex - a.zIndex);

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center justify-between p-2 text-xs text-muted-foreground">
        <span>{shapes.length} layer{shapes.length === 1 ? "" : "s"}</span>
      </div>
      <Separator />
      <ScrollArea className="flex-1">
        {ordered.length === 0 ? (
          <p className="p-4 text-sm text-muted-foreground">
            No layers yet. Pick a tool to add a shape.
          </p>
        ) : (
          <ul className="p-1">
            {ordered.map((s) => (
              <li
                key={s.id}
                onMouseDown={() => setSelected(s.id)}
                className={cn(
                  "group flex cursor-pointer items-center gap-2 rounded px-2 py-1.5 text-sm",
                  selectedId === s.id
                    ? "bg-primary/10 text-foreground"
                    : "hover:bg-accent",
                )}
              >
                <ShapeIcon shape={s} />
                <span className="flex-1 truncate">{shapeLabel(s)}</span>
                <span className="text-xs text-muted-foreground">{s.zIndex}</span>
              </li>
            ))}
          </ul>
        )}
      </ScrollArea>

      <Separator />
      <div className="grid grid-cols-6 gap-1 p-2">
        <IconAction
          label="Bring to front"
          disabled={!selectedId}
          onClick={() => selectedId && bringToFront(selectedId)}
        >
          <ArrowUpToLine className="h-4 w-4" />
        </IconAction>
        <IconAction
          label="Bring forward"
          disabled={!selectedId}
          onClick={() => selectedId && bringForward(selectedId)}
        >
          <ArrowUp className="h-4 w-4" />
        </IconAction>
        <IconAction
          label="Send backward"
          disabled={!selectedId}
          onClick={() => selectedId && sendBackward(selectedId)}
        >
          <ArrowDown className="h-4 w-4" />
        </IconAction>
        <IconAction
          label="Send to back"
          disabled={!selectedId}
          onClick={() => selectedId && sendToBack(selectedId)}
        >
          <ArrowDownToLine className="h-4 w-4" />
        </IconAction>
        <IconAction
          label="Duplicate"
          disabled={!selectedId}
          onClick={() => selectedId && duplicate(selectedId)}
        >
          <Copy className="h-4 w-4" />
        </IconAction>
        <IconAction
          label="Delete"
          disabled={!selectedId}
          onClick={() => selectedId && remove(selectedId)}
          variant="destructive"
        >
          <Trash2 className="h-4 w-4" />
        </IconAction>
      </div>
    </div>
  );
}

function IconAction({
  label,
  disabled,
  onClick,
  children,
  variant = "ghost",
}: {
  label: string;
  disabled?: boolean;
  onClick: () => void;
  children: React.ReactNode;
  variant?: "ghost" | "destructive";
}) {
  return (
    <Button
      size="icon"
      variant={variant === "destructive" && !disabled ? "destructive" : "ghost"}
      disabled={disabled}
      onClick={onClick}
      title={label}
      aria-label={label}
      className="h-8 w-full"
    >
      {children}
    </Button>
  );
}

function shapeLabel(s: Shape): string {
  switch (s.kind) {
    case "text": return s.text.slice(0, 28) || "Text";
    case "rect": return "Rectangle";
    case "circle": return "Circle";
    case "line": return "Line";
    case "image": return "Image";
    case "group": return "Group";
  }
}

function ShapeIcon({ shape }: { shape: Shape }) {
  const Icon =
    shape.kind === "rect" ? Square :
    shape.kind === "circle" ? Circle :
    shape.kind === "text" ? Type :
    shape.kind === "line" ? Slash :
    shape.kind === "image" ? ImageIcon :
    Square;
  return <Icon className="h-4 w-4 shrink-0 text-muted-foreground" />;
}
