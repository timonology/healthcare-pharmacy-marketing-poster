"use client";

import { useEffect, useRef } from "react";
import type { CanvasDocument } from "@acme/shared-types";
import { api } from "@/lib/api";
import { useEditorStore } from "@/store/editor-store";

interface UseAutoSaveOptions {
  delay?: number;
}

export function useAutoSave({ delay = 1500 }: UseAutoSaveOptions = {}): void {
  const inFlight = useRef<AbortController | null>(null);
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
      void runSave(posterId, posterName, doc, inFlight, setSaveStatus);
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

async function runSave(
  posterId: string,
  posterName: string,
  doc: CanvasDocument,
  inFlight: React.MutableRefObject<AbortController | null>,
  setSaveStatus: (s: "saving" | "saved" | "error", savedAtUtc?: string) => void,
) {
  inFlight.current?.abort();
  const controller = new AbortController();
  inFlight.current = controller;

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
