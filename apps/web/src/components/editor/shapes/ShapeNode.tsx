"use client";

import { useEffect, useRef, useState } from "react";
import { Circle, Group, Image as KonvaImage, Line, Rect, Text } from "react-konva";
import type Konva from "konva";
import type { Shape } from "@acme/shared-types";

interface ShapeNodeProps {
  shape: Shape;
  onSelect: () => void;
  onDragMove: (pos: { x: number; y: number }) => void;
  onDragEnd: (pos: { x: number; y: number }) => void;
  onDoubleClick?: () => void;
}

/**
 * Renders one shape. Common props (position / rotation / scale / opacity /
 * draggable) are mapped to Konva attributes; everything Konva-specific
 * (e.g. drag handlers) lives here so the store stays UI-free.
 */
export function ShapeNode({
  shape,
  onSelect,
  onDragMove,
  onDragEnd,
  onDoubleClick,
}: ShapeNodeProps) {
  const common = {
    id: shape.id,
    name: "shape",
    x: shape.position.x,
    y: shape.position.y,
    rotation: shape.rotation,
    scaleX: shape.scale.x,
    scaleY: shape.scale.y,
    opacity: shape.opacity,
    draggable: shape.draggable,
    onMouseDown: onSelect,
    onTouchStart: onSelect,
    onDblClick: onDoubleClick,
    onDblTap: onDoubleClick,
    onDragMove: (e: Konva.KonvaEventObject<DragEvent>) =>
      onDragMove({ x: e.target.x(), y: e.target.y() }),
    onDragEnd: (e: Konva.KonvaEventObject<DragEvent>) =>
      onDragEnd({ x: e.target.x(), y: e.target.y() }),
  } as const;

  switch (shape.kind) {
    case "rect":
      return (
        <Rect
          {...common}
          width={shape.width}
          height={shape.height}
          fill={shape.fill}
          stroke={shape.stroke}
          strokeWidth={shape.strokeWidth}
          cornerRadius={shape.cornerRadius}
        />
      );
    case "circle":
      return (
        <Circle
          {...common}
          radius={shape.radius}
          fill={shape.fill}
          stroke={shape.stroke}
          strokeWidth={shape.strokeWidth}
        />
      );
    case "line":
      return (
        <Line
          {...common}
          points={shape.points}
          stroke={shape.stroke}
          strokeWidth={shape.strokeWidth}
          closed={shape.closed}
          lineCap="round"
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
      return <ImageShapeNode common={common} blobKey={shape.blobKey} width={shape.width} height={shape.height} />;
    case "group":
      return <Group {...common} />;
  }
}

function ImageShapeNode({
  common,
  blobKey,
  width,
  height,
}: {
  common: Record<string, unknown>;
  blobKey: string;
  width: number;
  height: number;
}) {
  const [img, setImg] = useState<HTMLImageElement | null>(null);
  const lastUrl = useRef<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    if (lastUrl.current === blobKey) return;
    lastUrl.current = blobKey;

    const url = blobKey; // server returns SAS URLs in shape data; treat blobKey as resolvable
    const next = new window.Image();
    next.crossOrigin = "anonymous";
    next.src = url;
    next.onload = () => {
      if (!cancelled) setImg(next);
    };
    return () => {
      cancelled = true;
    };
  }, [blobKey]);

  if (!img) return null;
  return <KonvaImage {...common} image={img} width={width} height={height} />;
}
