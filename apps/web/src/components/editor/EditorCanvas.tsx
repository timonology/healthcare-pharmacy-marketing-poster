"use client";

import { useEffect, useRef, useState } from "react";
import { Layer, Rect, Stage, Text as KonvaText, Transformer } from "react-konva";
import type Konva from "konva";
import type { Shape } from "@acme/shared-types";
import { ShapeNode } from "@/components/editor/shapes/ShapeNode";
import {
  makeCircle,
  makeLine,
  makeRect,
  makeText,
  nextZIndex,
} from "@/lib/canvas/shape-factories";
import { useEditorStore } from "@/store/editor-store";
import { useSubscriptionStore } from "@/store/subscription-store";

interface EditorCanvasProps {
  width: number;
  height: number;
}

export function EditorCanvas({ width, height }: EditorCanvasProps) {
  const stageRef = useRef<Konva.Stage>(null);
  const transformerRef = useRef<Konva.Transformer>(null);
  const [editingTextId, setEditingTextId] = useState<string | null>(null);

  const doc = useEditorStore((s) => s.doc);
  const tool = useEditorStore((s) => s.tool);
  const selectedId = useEditorStore((s) => s.selectedId);
  const setSelected = useEditorStore((s) => s.setSelected);
  const setTool = useEditorStore((s) => s.setTool);
  const addShape = useEditorStore((s) => s.addShape);
  const updateShape = useEditorStore((s) => s.updateShape);
  const updateShapeLive = useEditorStore((s) => s.updateShapeLive);
  const commitHistory = useEditorStore((s) => s.commitHistory);
  const watermark = useSubscriptionStore((s) => s.current?.plan.watermark ?? false);

  const [stageScale, setStageScale] = useState(1);
  const [stagePos, setStagePos] = useState({ x: 0, y: 0 });
  useEffect(() => {
    const fitScale = Math.min(
      (width - 80) / doc.width,
      (height - 80) / doc.height,
      1,
    );
    setStageScale(Number.isFinite(fitScale) && fitScale > 0 ? fitScale : 0.5);
    setStagePos({
      x: (width - doc.width * fitScale) / 2,
      y: (height - doc.height * fitScale) / 2,
    });
  }, [width, height, doc.width, doc.height]);

  useEffect(() => {
    const tr = transformerRef.current;
    const stage = stageRef.current;
    if (!tr || !stage) return;
    if (selectedId) {
      const node = stage.findOne(`#${selectedId}`);
      tr.nodes(node ? [node] : []);
    } else {
      tr.nodes([]);
    }
    tr.getLayer()?.batchDraw();
  }, [selectedId, doc.shapes]);

  function onStageMouseDown(e: Konva.KonvaEventObject<MouseEvent | TouchEvent>) {
    const stage = e.target.getStage();
    if (!stage) return;

    const clickedOnEmpty = e.target === stage || e.target.name() === "page-bg";
    if (!clickedOnEmpty) return;

    if (tool === "select") {
      setSelected(null);
      return;
    }

    const pointer = stage.getPointerPosition();
    if (!pointer) return;
    const at = {
      x: (pointer.x - stage.x()) / stage.scaleX(),
      y: (pointer.y - stage.y()) / stage.scaleY(),
    };
    const z = nextZIndex(doc.shapes);

    let shape: Shape | null = null;
    if (tool === "rect") shape = makeRect(at, z);
    else if (tool === "circle") shape = makeCircle(at, z);
    else if (tool === "text") shape = makeText(at, z);
    else if (tool === "line") shape = makeLine(at, z);

    if (shape) {
      addShape(shape);
      setSelected(shape.id);
      setTool("select");
    }
  }

  function onTransformEnd(e: Konva.KonvaEventObject<Event>) {
    const node = e.target;
    const id = node.id();
    const shape = doc.shapes.find((s) => s.id === id);
    if (!shape) return;

    const sX = node.scaleX();
    const sY = node.scaleY();
    node.scaleX(1);
    node.scaleY(1);

    const patch: Partial<Shape> = {
      position: { x: node.x(), y: node.y() },
      rotation: node.rotation(),
      scale: { x: 1, y: 1 },
    };

    if (shape.kind === "rect" || shape.kind === "image") {
      Object.assign(patch, {
        width: Math.max(5, shape.width * sX),
        height: Math.max(5, shape.height * sY),
      });
    } else if (shape.kind === "circle") {
      Object.assign(patch, {
        radius: Math.max(5, shape.radius * Math.max(sX, sY)),
      });
    } else if (shape.kind === "text") {
      Object.assign(patch, {
        fontSize: Math.max(8, shape.fontSize * sY),
      });
    }

    updateShape(id, patch);
  }

  function onWheel(e: Konva.KonvaEventObject<WheelEvent>) {
    e.evt.preventDefault();
    const stage = stageRef.current;
    if (!stage) return;

    const oldScale = stageScale;
    const pointer = stage.getPointerPosition();
    if (!pointer) return;

    const direction = e.evt.deltaY > 0 ? -1 : 1;
    const factor = 1.05;
    const newScale = clamp(
      direction > 0 ? oldScale * factor : oldScale / factor,
      0.1,
      4,
    );

    const mousePoint = {
      x: (pointer.x - stage.x()) / oldScale,
      y: (pointer.y - stage.y()) / oldScale,
    };
    setStageScale(newScale);
    setStagePos({
      x: pointer.x - mousePoint.x * newScale,
      y: pointer.y - mousePoint.y * newScale,
    });
  }

  const sortedShapes = [...doc.shapes].sort((a, b) => a.zIndex - b.zIndex);

  return (
    <div className="relative h-full w-full overflow-hidden bg-muted">
      <Stage
        ref={stageRef}
        width={width}
        height={height}
        x={stagePos.x}
        y={stagePos.y}
        scaleX={stageScale}
        scaleY={stageScale}
        onMouseDown={onStageMouseDown}
        onTouchStart={onStageMouseDown}
        onWheel={onWheel}
        draggable={tool === "select"}
        onDragEnd={(e) => {
          if (e.target === stageRef.current) {
            setStagePos({ x: e.target.x(), y: e.target.y() });
          }
        }}
        style={{ cursor: tool === "select" ? "default" : "crosshair" }}
      >
        <Layer>
          <Rect
            name="page-bg"
            x={0}
            y={0}
            width={doc.width}
            height={doc.height}
            fill={doc.background}
            shadowColor="rgba(0,0,0,0.15)"
            shadowBlur={20}
            shadowOffsetY={4}
            listening
          />

          {sortedShapes.map((shape) => (
            <ShapeNode
              key={shape.id}
              shape={shape}
              onSelect={() => setSelected(shape.id)}
              onDragMove={(pos) =>
                updateShapeLive(shape.id, { position: pos })
              }
              onDragEnd={(pos) => {
                updateShapeLive(shape.id, { position: pos });
                commitHistory();
              }}
              onDoubleClick={
                shape.kind === "text"
                  ? () => setEditingTextId(shape.id)
                  : undefined
              }
            />
          ))}

          {watermark && (
            <KonvaText
              x={doc.width - 320}
              y={doc.height - 40}
              text="Made with Pharmacy Poster · pharmacyposter.app"
              fontSize={16}
              fontFamily="Inter, sans-serif"
              fill="rgba(15, 23, 42, 0.55)"
              listening={false}
            />
          )}

          <Transformer
            ref={transformerRef}
            rotateEnabled
            keepRatio={false}
            ignoreStroke
            anchorSize={10}
            borderStroke="#2563eb"
            anchorStroke="#2563eb"
            anchorFill="#ffffff"
            onTransformEnd={onTransformEnd}
            boundBoxFunc={(oldBox, newBox) =>
              newBox.width < 5 || newBox.height < 5 ? oldBox : newBox
            }
          />
        </Layer>
      </Stage>

      {/* HTML overlay for inline text editing. */}
      {editingTextId && (
        <TextEditor
          shapeId={editingTextId}
          onClose={() => setEditingTextId(null)}
          stageRef={stageRef}
          stageScale={stageScale}
          stagePos={stagePos}
        />
      )}

      <ZoomBadge scale={stageScale} onReset={() => {
        setStageScale(1);
        setStagePos({ x: (width - doc.width) / 2, y: (height - doc.height) / 2 });
      }} />
    </div>
  );
}

function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v));
}

function ZoomBadge({ scale, onReset }: { scale: number; onReset: () => void }) {
  return (
    <button
      type="button"
      onClick={onReset}
      className="absolute bottom-3 right-3 rounded-md border bg-background/80 px-2 py-1 text-xs shadow backdrop-blur hover:bg-background"
    >
      {Math.round(scale * 100)}%
    </button>
  );
}

interface TextEditorProps {
  shapeId: string;
  onClose: () => void;
  stageRef: React.RefObject<Konva.Stage>;
  stageScale: number;
  stagePos: { x: number; y: number };
}

function TextEditor({ shapeId, onClose, stageRef, stageScale, stagePos }: TextEditorProps) {
  const shape = useEditorStore((s) => s.doc.shapes.find((sh) => sh.id === shapeId));
  const updateShapeLive = useEditorStore((s) => s.updateShapeLive);
  const commitHistory = useEditorStore((s) => s.commitHistory);

  if (!shape || shape.kind !== "text" || !stageRef.current) return null;

  const stage = stageRef.current;
  const node = stage.findOne(`#${shapeId}`);
  if (!node) return null;
  const absPos = node.getAbsolutePosition();

  return (
    <textarea
      autoFocus
      defaultValue={shape.text}
      onBlur={(e) => {
        updateShapeLive(shapeId, { text: e.currentTarget.value });
        commitHistory();
        onClose();
      }}
      onKeyDown={(e) => {
        if (e.key === "Escape") onClose();
      }}
      style={{
        position: "absolute",
        top: absPos.y,
        left: absPos.x,
        width: (shape.width ?? 320) * stageScale,
        fontSize: shape.fontSize * stageScale,
        fontFamily: shape.fontFamily,
        color: shape.fill,
        background: "rgba(255,255,255,0.95)",
        border: "1px solid #2563eb",
        outline: "none",
        padding: "2px 4px",
        resize: "none",
        transform: `rotate(${shape.rotation}deg)`,
        transformOrigin: "top left",
        zIndex: 30,
      }}
    />
  );
}
