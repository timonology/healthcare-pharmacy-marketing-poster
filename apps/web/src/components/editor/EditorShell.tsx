"use client";

import dynamic from "next/dynamic";
import { useEffect, useRef, useState } from "react";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useAutoSave } from "@/hooks/useAutoSave";
import { useKeyboard } from "@/hooks/useKeyboard";
import { useEditorStore } from "@/store/editor-store";
import { EditorToolbar } from "./EditorToolbar";
import { EditorTopBar } from "./EditorTopBar";
import { LayersPanel } from "./LayersPanel";
import { PropertiesPanel } from "./PropertiesPanel";

// Konva touches `window` on import — render the canvas client-side only.
const EditorCanvas = dynamic(
  () => import("./EditorCanvas").then((m) => m.EditorCanvas),
  { ssr: false, loading: () => <CanvasFallback /> },
);

export function EditorShell() {
  useKeyboard();
  useAutoSave();

  const posterName = useEditorStore((s) => s.posterName);
  const stageWrapperRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });

  useEffect(() => {
    const el = stageWrapperRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const r = entries[0]?.contentRect;
      if (r) setSize({ width: r.width, height: r.height });
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  function exportPng() {
    // Konva exports include the page background — that's what users want.
    const stage = document.querySelector("canvas");
    if (!(stage instanceof HTMLCanvasElement)) return;
    const url = stage.toDataURL("image/png");
    const a = document.createElement("a");
    a.href = url;
    a.download = `${posterName.replace(/\s+/g, "-").toLowerCase() || "poster"}.png`;
    a.click();
  }

  return (
    <div className="flex h-screen flex-col bg-muted">
      <EditorTopBar onExport={exportPng} />
      <div className="flex flex-1 overflow-hidden">
        <EditorToolbar />
        <main ref={stageWrapperRef} className="flex-1 overflow-hidden">
          {size.width > 0 && (
            <EditorCanvas width={size.width} height={size.height} />
          )}
        </main>
        <RightSidebar />
      </div>
    </div>
  );
}

function RightSidebar() {
  return (
    <aside className="flex w-72 shrink-0 flex-col border-l bg-background">
      <Tabs defaultValue="properties" className="flex h-full flex-col">
        <TabsList className="m-2 grid grid-cols-2">
          <TabsTrigger value="properties">Properties</TabsTrigger>
          <TabsTrigger value="layers">Layers</TabsTrigger>
        </TabsList>
        <TabsContent value="properties" className="m-0 flex-1 overflow-hidden">
          <PropertiesPanel />
        </TabsContent>
        <TabsContent value="layers" className="m-0 flex-1 overflow-hidden">
          <LayersPanel />
        </TabsContent>
      </Tabs>
    </aside>
  );
}

function CanvasFallback() {
  return (
    <div className="flex h-full w-full items-center justify-center text-sm text-muted-foreground">
      Loading editor…
    </div>
  );
}
