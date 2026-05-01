"use client";

import { create } from "zustand";
import {
  emptyCanvas,
  type CanvasDocument,
  type Shape,
  type ShapeId,
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
  resetEmpty: (ownerId: string) => void;

  setPosterName: (name: string) => void;

  setTool: (tool: EditorTool) => void;
  setSelected: (id: ShapeId | null) => void;

  addShape: (shape: Shape) => void;
  removeShape: (id: ShapeId) => void;
  duplicateShape: (id: ShapeId) => void;
  updateShape: (id: ShapeId, patch: Partial<Shape>) => void;
  setBackground: (color: string) => void;
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

  setPosterName: (name) => set({ posterName: name, saveStatus: "dirty" }),

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
