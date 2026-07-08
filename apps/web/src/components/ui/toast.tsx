"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { AlertCircle, CheckCircle2, Info, X } from "lucide-react";
import { cn } from "@/lib/utils";

type ToastKind = "success" | "error" | "info";

interface ToastEntry {
  id: number;
  kind: ToastKind;
  title: string;
  description?: string;
}

interface ToastContextValue {
  toast: (input: Omit<ToastEntry, "id">) => void;
  success: (title: string, description?: string) => void;
  error: (title: string, description?: string) => void;
  info: (title: string, description?: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

export function ToastProvider({ children }: { children: React.ReactNode }) {
  const [items, setItems] = useState<ToastEntry[]>([]);
  const idRef = useRef(0);

  const dismiss = useCallback((id: number) => {
    setItems(prev => prev.filter(t => t.id !== id));
  }, []);

  const toast = useCallback((input: Omit<ToastEntry, "id">) => {
    const id = ++idRef.current;
    setItems(prev => [...prev, { id, ...input }]);
    setTimeout(() => dismiss(id), 4500);
  }, [dismiss]);

  const value = useMemo<ToastContextValue>(() => ({
    toast,
    success: (title, description) => toast({ kind: "success", title, description }),
    error: (title, description) => toast({ kind: "error", title, description }),
    info: (title, description) => toast({ kind: "info", title, description }),
  }), [toast]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="pointer-events-none fixed inset-x-0 bottom-4 z-[100] flex flex-col items-center gap-2 px-4 sm:items-end sm:right-4 sm:left-auto">
        {items.map(t => (
          <ToastItem key={t.id} entry={t} onDismiss={() => dismiss(t.id)} />
        ))}
      </div>
    </ToastContext.Provider>
  );
}

function ToastItem({ entry, onDismiss }: { entry: ToastEntry; onDismiss: () => void }) {
  const [open, setOpen] = useState(false);
  useEffect(() => {
    const id = requestAnimationFrame(() => setOpen(true));
    return () => cancelAnimationFrame(id);
  }, []);

  const Icon =
    entry.kind === "success" ? CheckCircle2 :
    entry.kind === "error" ? AlertCircle : Info;

  return (
    <div
      role="status"
      className={cn(
        "pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-lg border bg-background p-4 shadow-lg transition",
        open ? "translate-y-0 opacity-100" : "translate-y-2 opacity-0",
        entry.kind === "success" && "border-emerald-500/30",
        entry.kind === "error" && "border-destructive/40",
        entry.kind === "info" && "border-border",
      )}
    >
      <Icon className={cn(
        "mt-0.5 h-5 w-5 shrink-0",
        entry.kind === "success" && "text-emerald-500",
        entry.kind === "error" && "text-destructive",
        entry.kind === "info" && "text-muted-foreground",
      )} />
      <div className="flex-1 text-sm">
        <div className="font-medium">{entry.title}</div>
        {entry.description && (
          <div className="mt-0.5 text-muted-foreground">{entry.description}</div>
        )}
      </div>
      <button
        type="button"
        onClick={onDismiss}
        className="text-muted-foreground hover:text-foreground"
        aria-label="Dismiss"
      >
        <X className="h-4 w-4" />
      </button>
    </div>
  );
}

export function useToast() {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used within ToastProvider");
  return ctx;
}
