"use client";

import { useEffect } from "react";
import { useEditorStore } from "@/store/editor-store";

const isEditableTarget = (e: KeyboardEvent) => {
  const t = e.target as HTMLElement | null;
  if (!t) return false;
  if (t.isContentEditable) return true;
  const tag = t.tagName;
  return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
};

export function useKeyboard(): void {
  const undo = useEditorStore((s) => s.undo);
  const redo = useEditorStore((s) => s.redo);
  const removeShape = useEditorStore((s) => s.removeShape);
  const duplicateShape = useEditorStore((s) => s.duplicateShape);
  const setTool = useEditorStore((s) => s.setTool);
  const setSelected = useEditorStore((s) => s.setSelected);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (isEditableTarget(e)) return;

      const meta = e.metaKey || e.ctrlKey;

      if (meta && e.key.toLowerCase() === "z") {
        e.preventDefault();
        if (e.shiftKey) redo();
        else undo();
        return;
      }
      if (meta && e.key.toLowerCase() === "y") {
        e.preventDefault();
        redo();
        return;
      }
      if (meta && e.key.toLowerCase() === "d") {
        const id = useEditorStore.getState().selectedId;
        if (id) {
          e.preventDefault();
          duplicateShape(id);
        }
        return;
      }

      if (e.key === "Delete" || e.key === "Backspace") {
        const id = useEditorStore.getState().selectedId;
        if (id) {
          e.preventDefault();
          removeShape(id);
        }
        return;
      }

      if (e.key === "Escape") {
        setSelected(null);
        setTool("select");
        return;
      }

      switch (e.key.toLowerCase()) {
        case "v": setTool("select"); break;
        case "r": setTool("rect"); break;
        case "c": setTool("circle"); break;
        case "t": setTool("text"); break;
        case "l": setTool("line"); break;
      }
    };

    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [undo, redo, removeShape, duplicateShape, setTool, setSelected]);
}
