"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import {
  ArrowLeft,
  Building2,
  Check,
  Download,
  FileText,
  Loader2,
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
import { tokenValuesFrom } from "@/lib/canvas/display-tokens";
import { useTokenValuesStore } from "@/store/token-values-store";
import { api } from "@/lib/api";
import { savePosterNow } from "@/hooks/useAutoSave";
import type { SaveStatus } from "@/store/editor-store";
import { useEditorStore } from "@/store/editor-store";
import { ShareEmailDialog } from "./ShareEmailDialog";

interface EditorTopBarProps {
  onExport: () => void;

  /**
   * Where the back arrow goes, when it is not "the poster list".
   *
   * The shell is also used to hand-finish a design that has not been saved
   * anywhere yet — an admin refining a generated template. For that caller a
   * link to /posters is not a way out, it is a way to lose the work: there is
   * no poster behind this editor and nothing has autosaved (useAutoSave is a
   * no-op without a posterId). Given this, the arrow calls back instead, and
   * the caller decides what "done" means.
   */
  onBack?: () => void;
}

export function EditorTopBar({ onExport, onBack }: EditorTopBarProps) {
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
  const setTokenValues = useTokenValuesStore((s) => s.setValues);
  const [applying, setApplying] = useState(false);
  const [shareOpen, setShareOpen] = useState(false);
  const [finishing, setFinishing] = useState(false);
  const router = useRouter();

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const fetched = await api.getMe();
        if (!cancelled) {
          setMe(fetched);
          // What {{tokens}} show on the canvas: this pharmacy's details, or
          // blanks for any not filled in yet (e.g. onboarding skipped).
          setTokenValues(tokenValuesFrom(fetched.profile));
        }
      } catch {
        // ignore
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  /**
   * Puts the pharmacy's name, address, phone and logo onto the design.
   *
   * The brand kit is fetched here rather than when the editor opens. A pharmacy
   * that has not built one yet gets a 404 from that endpoint — which is the
   * correct answer to "show me my brand kit", but asking on every editor load
   * meant a 404 in the console and in monitoring on the most-used screen in the
   * product, for a question nobody had asked yet.
   *
   * Runnable repeatedly: the applier rewrites its own shapes in place rather
   * than stacking a second footer and a second logo.
   */
  async function applyInfo() {
    if (!me) return;

    setApplying(true);
    try {
      // Null is the ordinary answer for a pharmacy with no kit, not a failure.
      const kit = await api.getBrandKit().catch(() => null);
      const next = applyPharmacyInfo(doc, me.profile, {
        hasLogo: Boolean(kit?.logoBlobKey),
      });
      replaceDoc(next);
    } catch (err) {
      // Nothing here can reach the user — this bar has no error surface — but an
      // async click handler that rejects becomes an unhandled rejection, which
      // is worse than a logged one: it is invisible in development and reported
      // as a page error in production.
      console.error("Applying pharmacy details failed", err);
    } finally {
      setApplying(false);
    }
  }

  /**
   * Save, then leave.
   *
   * The designer autosaves, but on a 1.5 second debounce — so clicking the
   * back arrow within a moment of an edit could lose it. This saves first and
   * only navigates once the poster is stored; if the save fails it stays put,
   * with the badge showing the error, rather than quietly dropping the work.
   */
  async function finish() {
    setFinishing(true);
    const saved = await savePosterNow();
    if (!saved) {
      setFinishing(false);
      return;
    }

    // "Done" means "hand it back", not "go to /posters".
    //
    // The back arrow was given an override and this was missed, which is worse
    // than it sounds: Done is the prominent, obvious way to finish, and for a
    // caller editing an unsaved design it would have navigated to the pharmacy
    // poster list and taken the whole design with it. `savePosterNow` returns
    // true without a posterId, so nothing would even have failed — it would
    // just be gone.
    if (onBack) {
      onBack();
      return;
    }

    router.push("/posters");
  }

  function downloadPdf() {
    if (!posterId) return;
    window.location.href = api.posterPdfUrl(posterId);
  }

  return (
    <>
      <header className="flex h-14 shrink-0 items-center gap-3 border-b bg-background px-4">
        {onBack ? (
          <Button variant="ghost" size="sm" onClick={onBack} aria-label="Done editing">
            <ArrowLeft className="h-4 w-4" />
          </Button>
        ) : (
          <Button asChild variant="ghost" size="sm">
            <Link href="/posters" aria-label="Back to posters">
              <ArrowLeft className="h-4 w-4" />
            </Link>
          </Button>
        )}

        <Input
          value={posterName}
          onChange={(e) => setPosterName(e.target.value)}
          className="h-8 w-full min-w-0 max-w-xs"
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
          disabled={applying || !me?.profile.pharmacyName}
          title="Insert your pharmacy details into the poster"
        >
          <Building2 className="mr-1.5 h-4 w-4" />
          {applying ? "Applying…" : "Apply pharmacy info"}
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

          {/* The way out, said plainly. The only exit before this was an
              unlabelled arrow in the far corner, which testers reported not
              recognising as "I have finished with this poster". */}
          <Button
            size="sm"
            onClick={finish}
            disabled={finishing}
            className="shrink-0"
            title="Save this poster and go back to your posters"
          >
            {finishing ? (
              <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
            ) : (
              <Check className="mr-1.5 h-4 w-4" />
            )}
            {finishing ? "Saving…" : "Done"}
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
