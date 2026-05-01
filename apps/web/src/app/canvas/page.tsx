"use client";

import dynamic from "next/dynamic";
import { useEffect, useRef, useState } from "react";
import { CanvasToolbar } from "@/components/canvas/CanvasToolbar";

// React-Konva touches `window` on import — load it client-side only.
const CanvasStage = dynamic(
  () => import("@/components/canvas/CanvasStage").then((m) => m.CanvasStage),
  { ssr: false, loading: () => <div className="p-8">Loading canvas…</div> },
);

export default function CanvasPage() {
  const containerRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const r = entries[0]?.contentRect;
      if (r) setSize({ width: r.width, height: r.height });
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  return (
    <main className="flex h-screen flex-col">
      <CanvasToolbar />
      <div ref={containerRef} className="flex-1 overflow-hidden bg-muted">
        {size.width > 0 && (
          <CanvasStage width={size.width} height={size.height} />
        )}
      </div>
    </main>
  );
}
