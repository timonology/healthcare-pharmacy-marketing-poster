"use client";

import { Stage, Layer, Rect, Circle, Text, Line } from "react-konva";
import type Konva from "konva";
import { useCanvasStore } from "@/store/canvas-store";
import type { Shape } from "@acme/shared-types";

interface CanvasStageProps {
  width: number;
  height: number;
}

export function CanvasStage({ width, height }: CanvasStageProps) {
  const doc = useCanvasStore((s) => s.doc);
  const selectedId = useCanvasStore((s) => s.selectedId);
  const select = useCanvasStore((s) => s.select);
  const updateShape = useCanvasStore((s) => s.updateShape);

  return (
    <Stage
      width={width}
      height={height}
      x={doc.viewport.pan.x}
      y={doc.viewport.pan.y}
      scaleX={doc.viewport.zoom}
      scaleY={doc.viewport.zoom}
      onMouseDown={(e) => {
        if (e.target === e.target.getStage()) select(null);
      }}
      style={{ background: doc.background }}
    >
      <Layer>
        {[...doc.shapes]
          .sort((a, b) => a.zIndex - b.zIndex)
          .map((shape) => (
            <ShapeRenderer
              key={shape.id}
              shape={shape}
              selected={selectedId === shape.id}
              onSelect={() => select(shape.id)}
              onDragEnd={(e) =>
                updateShape(shape.id, {
                  position: { x: e.target.x(), y: e.target.y() },
                })
              }
            />
          ))}
      </Layer>
    </Stage>
  );
}

interface ShapeRendererProps {
  shape: Shape;
  selected: boolean;
  onSelect: () => void;
  onDragEnd: (e: Konva.KonvaEventObject<DragEvent>) => void;
}

function ShapeRenderer({
  shape,
  selected,
  onSelect,
  onDragEnd,
}: ShapeRendererProps) {
  const common = {
    x: shape.position.x,
    y: shape.position.y,
    rotation: shape.rotation,
    scaleX: shape.scale.x,
    scaleY: shape.scale.y,
    opacity: shape.opacity,
    draggable: shape.draggable,
    onClick: onSelect,
    onTap: onSelect,
    onDragEnd,
    stroke: selected ? "#2563eb" : undefined,
    strokeWidth: selected ? 2 : undefined,
  } as const;

  switch (shape.kind) {
    case "rect":
      return (
        <Rect
          {...common}
          width={shape.width}
          height={shape.height}
          fill={shape.fill}
          cornerRadius={shape.cornerRadius}
        />
      );
    case "circle":
      return <Circle {...common} radius={shape.radius} fill={shape.fill} />;
    case "line":
      return (
        <Line
          {...common}
          points={shape.points}
          stroke={shape.stroke}
          strokeWidth={shape.strokeWidth}
          closed={shape.closed}
        />
      );
    case "text":
      return (
        <Text
          {...common}
          text={shape.text}
          fontSize={shape.fontSize}
          fontFamily={shape.fontFamily}
          fill={shape.fill}
          align={shape.align}
          width={shape.width}
        />
      );
    case "image":
    case "group":
      // Stub: image and group rendering would live here.
      return null;
  }
}
