"use client";

import { useEffect, useRef } from "react";
import type { CanvasDocument } from "@acme/shared-types";
import { api } from "@/lib/api";
import { useEditorStore } from "@/store/editor-store";

interface UseAutoSaveOptions {
  delay?: number;
}

/**
 * The current save request, shared by the autosave timer and by an explicit
 * save.
 *
 * Module-level rather than a ref, because two different callers now start
 * saves: this hook on a debounce, and `savePosterNow()` when somebody presses
 * Done. With a controller each, pressing Done while a debounced save was in
 * flight would leave two writes racing for the same poster — and the one that
 * landed last would not necessarily be the newer one. Sharing it means the
 * explicit save cancels the timed one, which is exactly the intent.
 */
let inFlightSave: AbortController | null = null;

export function useAutoSave({ delay = 1500 }: UseAutoSaveOptions = {}): void {
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const doc = useEditorStore((s) => s.doc);
  const posterId = useEditorStore((s) => s.posterId);
  const posterName = useEditorStore((s) => s.posterName);
  const status = useEditorStore((s) => s.saveStatus);
  const setSaveStatus = useEditorStore((s) => s.setSaveStatus);

  useEffect(() => {
    if (status !== "dirty" || !posterId) return;

    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      void runSave(posterId, posterName, doc, setSaveStatus);
    }, delay);

    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
  }, [doc, posterName, posterId, status, delay, setSaveStatus]);

  useEffect(() => {
    const handler = (e: BeforeUnloadEvent) => {
      if (useEditorStore.getState().saveStatus === "dirty") {
        e.preventDefault();
        e.returnValue = "";
      }
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, []);
}

/**
 * Saves right now and resolves once it has finished.
 *
 * Exists for the Done button. Autosave waits 1.5 seconds after the last edit,
 * so leaving the designer immediately after a change could drop it — the
 * `beforeunload` guard below only covers a full page load, and a click on a
 * link inside the app is not one. Rather than warn about unsaved work, the
 * button saves it.
 *
 * Returns whether the poster is safely stored, so the caller can decline to
 * navigate away on failure instead of silently losing the edit.
 */
export async function savePosterNow(): Promise<boolean> {
  const { posterId, posterName, doc, saveStatus, setSaveStatus } =
    useEditorStore.getState();

  // Nothing to save against, or nothing outstanding.
  if (!posterId) return true;
  if (saveStatus === "saved" || saveStatus === "idle") return true;

  await runSave(posterId, posterName, doc, setSaveStatus);
  return useEditorStore.getState().saveStatus === "saved";
}

async function runSave(
  posterId: string,
  posterName: string,
  doc: CanvasDocument,
  setSaveStatus: (s: "saving" | "saved" | "error", savedAtUtc?: string) => void,
) {
  inFlightSave?.abort();
  const controller = new AbortController();
  inFlightSave = controller;

  setSaveStatus("saving");
  try {
    const updated = await api.updatePoster(posterId, {
      name: posterName,
      canvas: doc,
    });
    if (controller.signal.aborted) return;
    setSaveStatus("saved", updated.updatedAtUtc);
  } catch (err) {
    if (controller.signal.aborted) return;
    console.error("Auto-save failed", err);
    setSaveStatus("error");
  }
}
