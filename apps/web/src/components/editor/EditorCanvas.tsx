"use client";

import { useEffect, useRef, useState } from "react";
import { Layer, Line, Rect, Stage, Text as KonvaText, Transformer } from "react-konva";
import type Konva from "konva";
import { foldGuides, type Shape } from "@acme/shared-types";
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
      // Traverse the tree instead of stage.findOne('#id'), which resolves via
      // Konva's process-global id registry — see the note on TextEditor. A miss
      // there silently detaches the Transformer, so the layer looks selected in
      // the layers panel but shows no handles and cannot be resized.
      const node = stage.find((n: Konva.Node) => n.id() === selectedId)[0];
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

          {/*
            Fold guides. Drawn from the document's own foldPanels, above the
            page and below the artwork, and deliberately `listening={false}` so
            they cannot be clicked, selected or dragged.

            They exist only here. The exporter renders from the canvas JSON and
            knows nothing about them, which is what keeps a guide off the
            printed sheet — a fold line that prints is a ruined print run.

            Stroke and dash are divided by the zoom so a guide stays a hairline
            at any magnification instead of becoming a stripe when zoomed out.
          */}
          {foldGuides(doc.width, doc.foldPanels ?? 1).map((x) => (
            <Line
              key={`fold-${x}`}
              points={[x, 0, x, doc.height]}
              stroke="#0ea5e9"
              strokeWidth={1 / stageScale}
              dash={[8 / stageScale, 8 / stageScale]}
              opacity={0.7}
              listening={false}
            />
          ))}

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
              text="Made with Marketing25"
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
          key={editingTextId}
          shapeId={editingTextId}
          onClose={() => setEditingTextId(null)}
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
  stageScale: number;
  stagePos: { x: number; y: number };
}

/**
 * Inline text editing overlay.
 *
 * Position is derived from the shape's own document coordinates and the stage
 * transform, NOT from `stage.findOne('#id')`.
 *
 * `findOne` resolves through Konva's PROCESS-GLOBAL id registry (`Konva._ids`),
 * which has two failure modes that both end in a silent `return null` — i.e.
 * double-click appears to do nothing, on some layers but not others:
 *
 *  1. Ids must be unique across every Konva stage on the page. Our seeded
 *     templates reuse generic ids ("headline-1", "footer", "cta-bar" — "footer"
 *     appears in all 24), so any second stage mounting the same id wins the
 *     registry and the lookup fails the ancestry check.
 *  2. React StrictMode double-mounts in dev. The throwaway mount's unregister
 *     deletes the id the surviving mount registered, so the node is simply
 *     absent from the registry.
 *
 * screen = stagePos + docPos * stageScale is exact, has no global state, and is
 * what the (previously unused) `stagePos` prop was passed in for.
 */
function TextEditor({ shapeId, onClose, stageScale, stagePos }: TextEditorProps) {
  const shape = useEditorStore((s) => s.doc.shapes.find((sh) => sh.id === shapeId));
  const updateShapeLive = useEditorStore((s) => s.updateShapeLive);
  const commitHistory = useEditorStore((s) => s.commitHistory);

  if (!shape || shape.kind !== "text") return null;

  // Konva applies scale about the node origin, and Text's origin is its
  // top-left, so the same origin works for the CSS transform below.
  const scaleX = shape.scale?.x ?? 1;
  const scaleY = shape.scale?.y ?? 1;

  const left = stagePos.x + shape.position.x * stageScale;
  const top = stagePos.y + shape.position.y * stageScale;
  const fontSize = shape.fontSize * scaleY * stageScale;

  const commit = (value: string) => {
    updateShapeLive(shapeId, { text: value });
    commitHistory();
    onClose();
  };

  return (
    <textarea
      autoFocus
      defaultValue={shape.text}
      onFocus={(e) => e.currentTarget.select()}
      onBlur={(e) => commit(e.currentTarget.value)}
      onKeyDown={(e) => {
        // Escape reverts; the store was never touched, so just close.
        if (e.key === "Escape") {
          e.stopPropagation();
          onClose();
          return;
        }
        // Enter commits. Shift+Enter inserts a newline, matching every other
        // design tool — posters legitimately need multi-line headlines.
        if (e.key === "Enter" && !e.shiftKey) {
          e.preventDefault();
          commit(e.currentTarget.value);
        }
      }}
      style={{
        position: "absolute",
        top,
        left,
        width: (shape.width ?? 320) * scaleX * stageScale,
        // Headlines run to 118px; a default 2-row box hid most of the text and
        // made the field look broken. Track the real line height instead.
        minHeight: fontSize * 1.4,
        height: "auto",
        fontSize,
        lineHeight: 1.2,
        fontFamily: shape.fontFamily,
        textAlign: shape.align ?? "left",
        color: shape.fill,
        background: "rgba(255,255,255,0.97)",
        border: "1px solid #2563eb",
        borderRadius: 2,
        outline: "none",
        padding: 0,
        margin: 0,
        overflow: "hidden",
        resize: "none",
        transform: `rotate(${shape.rotation}deg)`,
        transformOrigin: "top left",
        zIndex: 30,
      }}
    />
  );
}
