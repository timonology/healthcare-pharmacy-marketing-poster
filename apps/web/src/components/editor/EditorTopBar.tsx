"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import {
  ArrowLeft,
  Building2,
  Download,
  FileText,
  Redo2,
  Share2,
  Undo2,
} from "lucide-react";
import type { Me } from "@acme/shared-types";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Separator } from "@/components/ui/separator";
import { ThemeToggle } from "@/components/theme-toggle";
import { applyPharmacyInfo } from "@/lib/canvas/apply-pharmacy";
import { api } from "@/lib/api";
import type { SaveStatus } from "@/store/editor-store";
import { useEditorStore } from "@/store/editor-store";
import { ShareEmailDialog } from "./ShareEmailDialog";

interface EditorTopBarProps {
  onExport: () => void;
}

export function EditorTopBar({ onExport }: EditorTopBarProps) {
  const posterId = useEditorStore((s) => s.posterId);
  const posterName = useEditorStore((s) => s.posterName);
  const doc = useEditorStore((s) => s.doc);
  const setPosterName = useEditorStore((s) => s.setPosterName);
  const replaceDoc = useEditorStore((s) => s.replaceDoc);
  const undo = useEditorStore((s) => s.undo);
  const redo = useEditorStore((s) => s.redo);
  const canUndo = useEditorStore((s) => s.past.length > 0);
  const canRedo = useEditorStore((s) => s.future.length > 0);
  const status = useEditorStore((s) => s.saveStatus);
  const lastSavedAtUtc = useEditorStore((s) => s.lastSavedAtUtc);

  const [me, setMe] = useState<Me | null>(null);
  const [shareOpen, setShareOpen] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const fetched = await api.getMe();
        if (!cancelled) setMe(fetched);
      } catch {
        // ignore
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  function applyInfo() {
    if (!me) return;
    const next = applyPharmacyInfo(doc, me.profile);
    replaceDoc(next);
  }

  function downloadPdf() {
    if (!posterId) return;
    window.location.href = api.posterPdfUrl(posterId);
  }

  return (
    <>
      <header className="flex h-14 shrink-0 items-center gap-3 border-b bg-background px-4">
        <Button asChild variant="ghost" size="sm">
          <Link href="/posters" aria-label="Back to posters">
            <ArrowLeft className="h-4 w-4" />
          </Link>
        </Button>

        <Input
          value={posterName}
          onChange={(e) => setPosterName(e.target.value)}
          className="h-8 max-w-xs"
          aria-label="Poster name"
        />

        <Separator orientation="vertical" className="h-6" />

        <div className="flex items-center gap-1">
          <Button
            size="sm"
            variant="ghost"
            onClick={undo}
            disabled={!canUndo}
            aria-label="Undo"
            title="Undo (⌘Z)"
          >
            <Undo2 className="h-4 w-4" />
          </Button>
          <Button
            size="sm"
            variant="ghost"
            onClick={redo}
            disabled={!canRedo}
            aria-label="Redo"
            title="Redo (⇧⌘Z)"
          >
            <Redo2 className="h-4 w-4" />
          </Button>
        </div>

        <Separator orientation="vertical" className="h-6" />

        <Button
          size="sm"
          variant="ghost"
          onClick={applyInfo}
          disabled={!me?.profile.pharmacyName}
          title="Insert your pharmacy details into the poster"
        >
          <Building2 className="mr-1.5 h-4 w-4" />
          Apply pharmacy info
        </Button>

        <SaveBadge status={status} lastSavedAtUtc={lastSavedAtUtc} />

        <div className="ml-auto flex items-center gap-2">
          <ThemeToggle />
          <Button
            size="sm"
            variant="outline"
            onClick={() => setShareOpen(true)}
            disabled={!posterId}
          >
            <Share2 className="mr-1.5 h-4 w-4" />
            Share
          </Button>
          <Button
            size="sm"
            variant="outline"
            onClick={downloadPdf}
            disabled={!posterId}
          >
            <FileText className="mr-1.5 h-4 w-4" />
            PDF
          </Button>
          <Button size="sm" variant="outline" onClick={onExport}>
            <Download className="mr-1.5 h-4 w-4" />
            PNG
          </Button>
        </div>
      </header>

      {posterId && (
        <ShareEmailDialog
          open={shareOpen}
          onOpenChange={setShareOpen}
          posterId={posterId}
          posterName={posterName}
        />
      )}
    </>
  );
}

function SaveBadge({
  status,
  lastSavedAtUtc,
}: {
  status: SaveStatus;
  lastSavedAtUtc: string | null;
}) {
  const label =
    status === "saving" ? "Saving…"
    : status === "saved" ? `Saved${lastSavedAtUtc ? ` ${formatTime(lastSavedAtUtc)}` : ""}`
    : status === "dirty" ? "Unsaved changes"
    : status === "error" ? "Save failed"
    : "Saved";

  const dot =
    status === "saving" ? "bg-amber-500"
    : status === "error" ? "bg-destructive"
    : status === "dirty" ? "bg-amber-500"
    : "bg-emerald-500";

  return (
    <div
      role="status"
      aria-live="polite"
      className="flex items-center gap-2 rounded-md bg-muted px-2 py-1 text-xs text-muted-foreground"
    >
      <span className={`h-1.5 w-1.5 rounded-full ${dot}`} />
      {label}
    </div>
  );
}

function formatTime(iso: string) {
  try {
    return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  } catch {
    return "";
  }
}
