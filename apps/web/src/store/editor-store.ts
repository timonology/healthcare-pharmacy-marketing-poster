"use client";

import { create } from "zustand";
import {
  emptyCanvas,
  type CanvasDocument,
  type Shape,
  type ShapeId,
  type FoldPanels,
  type PrintFormat,
} from "@acme/shared-types";

export type EditorTool =
  | "select"
  | "rect"
  | "circle"
  | "text"
  | "line";

export type SaveStatus = "idle" | "dirty" | "saving" | "saved" | "error";

const HISTORY_LIMIT = 100;

interface EditorState {
  doc: CanvasDocument;
  posterId: string | null;
  posterName: string;

  selectedId: ShapeId | null;
  tool: EditorTool;

  past: CanvasDocument[];
  future: CanvasDocument[];

  saveStatus: SaveStatus;
  lastSavedAtUtc: string | null;

  loadDocument: (doc: CanvasDocument) => void;
  loadPoster: (input: { id: string; name: string; canvas: CanvasDocument }) => void;

  /**
   * Loads a document that belongs to no poster — a template being composed, or
   * a duplicate on its way to being one.
   *
   * Clears `posterId`, so autosave stays switched off and the caller owns
   * saving. See the implementation for what goes wrong without it.
   */
  loadDetached: (input: { name: string; canvas: CanvasDocument }) => void;
  resetEmpty: (ownerId: string) => void;

  setPosterName: (name: string) => void;
  replaceDoc: (doc: CanvasDocument) => void;

  setTool: (tool: EditorTool) => void;
  setSelected: (id: ShapeId | null) => void;

  addShape: (shape: Shape) => void;
  removeShape: (id: ShapeId) => void;
  duplicateShape: (id: ShapeId) => void;
  updateShape: (id: ShapeId, patch: Partial<Shape>) => void;
  setBackground: (color: string) => void;
  /**
   * Resize the page. `scaleContent` (default true) scales every shape by the
   * same factor so the design survives the change — without it an A4 layout
   * clings to the top-left corner of an A3 sheet.
   */
  setPageSize: (width: number, height: number, scaleContent?: boolean) => void;
  /**
   * What this artwork is, and how many panels it folds into.
   *
   * Separate from the page size on purpose: a trifold leaflet is A4 landscape,
   * so the dimensions cannot say whether folds are wanted. Changing the format
   * does not resize the canvas — that stays the designer's choice.
   */
  setFormat: (format: PrintFormat, foldPanels?: FoldPanels) => void;
  rename: (name: string) => void;

  updateShapeLive: (id: ShapeId, patch: Partial<Shape>) => void;
  commitHistory: () => void;

  bringForward: (id: ShapeId) => void;
  sendBackward: (id: ShapeId) => void;
  bringToFront: (id: ShapeId) => void;
  sendToBack: (id: ShapeId) => void;

  undo: () => void;
  redo: () => void;

  setSaveStatus: (s: SaveStatus, savedAtUtc?: string) => void;
}

export const useEditorStore = create<EditorState>()((set, get) => ({
  doc: emptyCanvas("local-doc", "anonymous"),
  posterId: null,
  posterName: "",
  selectedId: null,
  tool: "select",
  past: [],
  future: [],
  saveStatus: "idle",
  lastSavedAtUtc: null,

  loadDocument: (doc) =>
    set({
      doc,
      selectedId: null,
      past: [],
      future: [],
      saveStatus: "saved",
      lastSavedAtUtc: doc.updatedAtUtc,
    }),

  loadPoster: ({ id, name, canvas }) =>
    set({
      posterId: id,
      posterName: name,
      doc: canvas,
      selectedId: null,
      past: [],
      future: [],
      saveStatus: "saved",
      lastSavedAtUtc: canvas.updatedAtUtc,
    }),

  loadDetached: ({ name, canvas }) =>
    set({
      // The whole point of this action: no poster behind the document.
      //
      // `replaceDoc` leaves `posterId` alone and marks the store dirty, which
      // is correct when you are editing the poster that is already loaded. Hand
      // it a *different* document — a template being composed, a duplicate —
      // while a poster id is still in the store from earlier in the session,
      // and `useAutoSave` will write the new document over that poster 1.5
      // seconds later. The pharmacy's poster is silently replaced by something
      // it has nothing to do with.
      //
      // That could not happen while the only destination was /canvas, which
      // mounts no autosave. It became possible the moment the real editor was
      // opened on an unsaved design, so the detaching is explicit here rather
      // than remembered at each call site.
      posterId: null,
      posterName: name,
      doc: canvas,
      selectedId: null,
      past: [],
      future: [],
      saveStatus: "saved",
      lastSavedAtUtc: canvas.updatedAtUtc,
    }),

  setPosterName: (name) => set({ posterName: name, saveStatus: "dirty" }),

  replaceDoc: (doc) =>
    set((s) => ({
      past: capHistory([...s.past, snapshot(s.doc)]),
      future: [],
      doc,
      saveStatus: "dirty",
    })),

  resetEmpty: (ownerId) =>
    set({
      doc: emptyCanvas(crypto.randomUUID(), ownerId),
      selectedId: null,
      past: [],
      future: [],
      saveStatus: "dirty",
    }),

  setTool: (tool) => set({ tool }),
  setSelected: (id) => set({ selectedId: id }),

  addShape: (shape) =>
    pushHistoryAnd(set, get, (doc) => ({
      ...doc,
      shapes: [...doc.shapes, shape],
    })),

  removeShape: (id) =>
    pushHistoryAnd(set, get, (doc) => ({
      ...doc,
      shapes: doc.shapes.filter((s) => s.id !== id),
    }), () => ({ selectedId: get().selectedId === id ? null : get().selectedId })),

  duplicateShape: (id) => {
    const { doc } = get();
    const source = doc.shapes.find((s) => s.id === id);
    if (!source) return;
    const copy: Shape = {
      ...source,
      id: crypto.randomUUID(),
      position: { x: source.position.x + 24, y: source.position.y + 24 },
      zIndex: nextZIndex(doc.shapes),
    } as Shape;
    pushHistoryAnd(set, get, (d) => ({
      ...d,
      shapes: [...d.shapes, copy],
    }));
    set({ selectedId: copy.id });
  },

  updateShape: (id, patch) =>
    pushHistoryAnd(set, get, (doc) => ({
      ...doc,
      shapes: applyPatch(doc.shapes, id, patch),
    })),

  setBackground: (color) =>
    pushHistoryAnd(set, get, (doc) => ({ ...doc, background: color })),

  setFormat: (format, foldPanels) =>
    pushHistoryAnd(set, get, (doc) => {
      const panels = foldPanels ?? doc.foldPanels ?? 1;
      if (doc.format === format && doc.foldPanels === panels) return doc;
      return { ...doc, format, foldPanels: panels };
    }),

  setPageSize: (width, height, scaleContent = true) =>
    pushHistoryAnd(set, get, (doc) => {
      if (doc.width === width && doc.height === height) return doc;
      if (!scaleContent) return { ...doc, width, height };
      const kx = width / doc.width;
      const ky = height / doc.height;
      return {
        ...doc,
        width,
        height,
        shapes: doc.shapes.map((s) => scaleShape(s, kx, ky)),
      };
    }),

  rename: (name) =>
    pushHistoryAnd(set, get, (doc) => ({ ...doc, name })),

  updateShapeLive: (id, patch) =>
    set((s) => ({
      doc: {
        ...s.doc,
        shapes: applyPatch(s.doc.shapes, id, patch),
        updatedAtUtc: new Date().toISOString(),
      },
      saveStatus: "dirty",
    })),

  commitHistory: () => {
    const { doc, past } = get();
    set({
      past: capHistory([...past, snapshot(doc)]),
      future: [],
      saveStatus: "dirty",
    });
  },

  bringForward: (id) => reorder(set, get, id, "forward"),
  sendBackward: (id) => reorder(set, get, id, "backward"),
  bringToFront: (id) => reorder(set, get, id, "front"),
  sendToBack: (id) => reorder(set, get, id, "back"),

  undo: () => {
    const { past, future, doc } = get();
    if (past.length === 0) return;
    const previous = past[past.length - 1];
    set({
      past: past.slice(0, -1),
      doc: previous,
      future: [snapshot(doc), ...future],
      saveStatus: "dirty",
      selectedId: get().selectedId && previous.shapes.some((s) => s.id === get().selectedId)
        ? get().selectedId
        : null,
    });
  },

  redo: () => {
    const { past, future, doc } = get();
    if (future.length === 0) return;
    const next = future[0];
    set({
      past: capHistory([...past, snapshot(doc)]),
      doc: next,
      future: future.slice(1),
      saveStatus: "dirty",
      selectedId: get().selectedId && next.shapes.some((s) => s.id === get().selectedId)
        ? get().selectedId
        : null,
    });
  },

  setSaveStatus: (saveStatus, savedAtUtc) =>
    set({
      saveStatus,
      lastSavedAtUtc: savedAtUtc ?? get().lastSavedAtUtc,
    }),
}));

/**
 * Scale a shape's geometry about the page origin when the page is resized.
 *
 * Axes are scaled independently. A uniform factor looks tidier on paper but
 * leaves full-bleed artwork short of the edge: A4 -> A3 is 1.41452x across and
 * 1.41391x down, so a 1240px-wide hero scaled by the smaller factor lands at
 * 1753.2px on a 1754px page — a hairline of background down the side of every
 * template that bleeds off the edge, which is most of them. Per-axis factors
 * keep bleeds exact and anchored elements anchored.
 *
 * The cost is 0.04% of anisotropy between A4 and A3, which is invisible. Radius,
 * font size and stroke width have no meaningful per-axis reading, so they take
 * the geometric mean — that keeps circles circular and text proportional while
 * still tracking the overall size change.
 *
 * Intrinsic size is scaled per kind rather than by bumping `shape.scale`,
 * because `scale` is user-facing (the Transformer writes it, the properties
 * panel shows it) — folding a page resize into it would make every shape report
 * a scale the user never applied. Rotation and opacity are dimensionless.
 */
function scaleShape(shape: Shape, kx: number, ky: number): Shape {
  const k = Math.sqrt(kx * ky);
  const base = {
    ...shape,
    position: { x: shape.position.x * kx, y: shape.position.y * ky },
  };

  switch (base.kind) {
    case "rect":
      return {
        ...base,
        width: base.width * kx,
        height: base.height * ky,
        strokeWidth: base.strokeWidth == null ? base.strokeWidth : base.strokeWidth * k,
        cornerRadius:
          base.cornerRadius == null ? base.cornerRadius : base.cornerRadius * k,
      };
    case "circle":
      return {
        ...base,
        radius: base.radius * k,
        strokeWidth: base.strokeWidth == null ? base.strokeWidth : base.strokeWidth * k,
      };
    case "line":
      return {
        ...base,
        // points are a flat [x1,y1,x2,y2,...] run.
        points: base.points.map((p, i) => (i % 2 === 0 ? p * kx : p * ky)),
        strokeWidth: base.strokeWidth * k,
      };
    case "text":
      return {
        ...base,
        fontSize: base.fontSize * k,
        // The text box width is a horizontal measure, so it tracks kx — this is
        // what keeps centred headlines centred against a full-bleed band.
        width: base.width == null ? base.width : base.width * kx,
      };
    case "image":
      return { ...base, width: base.width * kx, height: base.height * ky };
    case "group":
      // Children are scaled in their own right as part of the same map.
      return base;
  }
}

function snapshot(doc: CanvasDocument): CanvasDocument {
  return JSON.parse(JSON.stringify(doc)) as CanvasDocument;
}

function capHistory(stack: CanvasDocument[]): CanvasDocument[] {
  if (stack.length <= HISTORY_LIMIT) return stack;
  return stack.slice(stack.length - HISTORY_LIMIT);
}

function applyPatch(
  shapes: Shape[],
  id: ShapeId,
  patch: Partial<Shape>,
): Shape[] {
  return shapes.map((s) => (s.id === id ? ({ ...s, ...patch } as Shape) : s));
}

function nextZIndex(shapes: Shape[]): number {
  if (shapes.length === 0) return 1;
  return Math.max(...shapes.map((s) => s.zIndex)) + 1;
}

type Setter = (
  partial:
    | Partial<EditorState>
    | ((s: EditorState) => Partial<EditorState>),
) => void;
type Getter = () => EditorState;

function pushHistoryAnd(
  set: Setter,
  get: Getter,
  mutator: (doc: CanvasDocument) => CanvasDocument,
  extra?: () => Partial<EditorState>,
) {
  const { doc, past } = get();
  const nextDoc = {
    ...mutator(doc),
    updatedAtUtc: new Date().toISOString(),
  };
  set({
    doc: nextDoc,
    past: capHistory([...past, snapshot(doc)]),
    future: [],
    saveStatus: "dirty",
    ...(extra ? extra() : {}),
  });
}

function reorder(
  set: Setter,
  get: Getter,
  id: ShapeId,
  direction: "forward" | "backward" | "front" | "back",
) {
  const { doc } = get();
  const sorted = [...doc.shapes].sort((a, b) => a.zIndex - b.zIndex);
  const i = sorted.findIndex((s) => s.id === id);
  if (i === -1) return;

  const target =
    direction === "forward" ? Math.min(sorted.length - 1, i + 1)
    : direction === "backward" ? Math.max(0, i - 1)
    : direction === "front" ? sorted.length - 1
    : 0;
  if (target === i) return;

  const moved = [...sorted];
  const [item] = moved.splice(i, 1);
  moved.splice(target, 0, item);

  const reindexed = moved.map((s, idx) => ({ ...s, zIndex: idx + 1 }));

  pushHistoryAnd(set, get, (d) => ({ ...d, shapes: reindexed }));
}
