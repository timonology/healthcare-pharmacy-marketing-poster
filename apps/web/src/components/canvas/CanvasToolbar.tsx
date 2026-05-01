"use client";

import { Button } from "@/components/ui/button";
import { useCanvasStore } from "@/store/canvas-store";
import type { Shape } from "@acme/shared-types";

const baseShape = (overrides: Partial<Shape> = {}) =>
  ({
    id: crypto.randomUUID(),
    rotation: 0,
    scale: { x: 1, y: 1 },
    opacity: 1,
    draggable: true,
    zIndex: Date.now(),
    ...overrides,
  }) as Shape;

export function CanvasToolbar() {
  const addShape = useCanvasStore((s) => s.addShape);
  const removeShape = useCanvasStore((s) => s.removeShape);
  const selectedId = useCanvasStore((s) => s.selectedId);

  return (
    <div className="flex flex-wrap items-center gap-2 border-b bg-background p-2">
      <Button
        size="sm"
        onClick={() =>
          addShape(
            baseShape({
              kind: "rect",
              position: { x: 100, y: 100 },
              width: 160,
              height: 100,
              fill: "#3b82f6",
              cornerRadius: 8,
            }) as Shape,
          )
        }
      >
        Add rect
      </Button>
      <Button
        size="sm"
        onClick={() =>
          addShape(
            baseShape({
              kind: "circle",
              position: { x: 200, y: 200 },
              radius: 60,
              fill: "#10b981",
            }) as Shape,
          )
        }
      >
        Add circle
      </Button>
      <Button
        size="sm"
        onClick={() =>
          addShape(
            baseShape({
              kind: "text",
              position: { x: 120, y: 320 },
              text: "Hello, canvas",
              fontSize: 28,
              fontFamily: "Inter, sans-serif",
              fill: "#0f172a",
            }) as Shape,
          )
        }
      >
        Add text
      </Button>
      <Button
        size="sm"
        variant="destructive"
        disabled={!selectedId}
        onClick={() => selectedId && removeShape(selectedId)}
      >
        Delete
      </Button>
    </div>
  );
}
