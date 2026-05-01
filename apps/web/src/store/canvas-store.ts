"use client";

import { create } from "zustand";
import {
  CANVAS_SCHEMA_VERSION,
  emptyCanvas,
  type CanvasDocument,
  type Shape,
  type ShapeId,
} from "@acme/shared-types";

interface CanvasState {
  doc: CanvasDocument;
  selectedId: ShapeId | null;

  loadDocument: (doc: CanvasDocument) => void;
  reset: (ownerId: string) => void;

  addShape: (shape: Shape) => void;
  updateShape: (id: ShapeId, patch: Partial<Shape>) => void;
  removeShape: (id: ShapeId) => void;
  select: (id: ShapeId | null) => void;
}

export const useCanvasStore = create<CanvasState>((set) => ({
  doc: emptyCanvas("local-doc", "anonymous"),
  selectedId: null,

  loadDocument: (doc) => {
    if (doc.schemaVersion !== CANVAS_SCHEMA_VERSION) {
      // eslint-disable-next-line no-console
      console.warn(
        `Loaded canvas v${doc.schemaVersion}, app expects v${CANVAS_SCHEMA_VERSION}.`,
      );
    }
    set({ doc, selectedId: null });
  },

  reset: (ownerId) =>
    set({ doc: emptyCanvas(crypto.randomUUID(), ownerId), selectedId: null }),

  addShape: (shape) =>
    set((s) => ({
      doc: {
        ...s.doc,
        shapes: [...s.doc.shapes, shape],
        updatedAtUtc: new Date().toISOString(),
      },
    })),

  updateShape: (id, patch) =>
    set((s) => ({
      doc: {
        ...s.doc,
        shapes: s.doc.shapes.map((sh) =>
          sh.id === id ? ({ ...sh, ...patch } as Shape) : sh,
        ),
        updatedAtUtc: new Date().toISOString(),
      },
    })),

  removeShape: (id) =>
    set((s) => ({
      doc: {
        ...s.doc,
        shapes: s.doc.shapes.filter((sh) => sh.id !== id),
        updatedAtUtc: new Date().toISOString(),
      },
      selectedId: s.selectedId === id ? null : s.selectedId,
    })),

  select: (id) => set({ selectedId: id }),
}));
