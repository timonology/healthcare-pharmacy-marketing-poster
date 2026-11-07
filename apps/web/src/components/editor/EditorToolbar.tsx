"use client";

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import {
  Circle,
  Image as ImageIcon,
  MousePointer2,
  QrCode,
  Slash,
  Square,
  Type,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { api } from "@/lib/api";
import { PHARMACY_BOOKING_QR_ID } from "@/lib/canvas/apply-pharmacy";
import { prepareImage } from "@/lib/canvas/image-upload";
import { makeBookingQr, makeImage, nextZIndex } from "@/lib/canvas/shape-factories";
import type { EditorTool } from "@/store/editor-store";
import { useEditorStore } from "@/store/editor-store";

const TOOLS: { id: EditorTool; label: string; shortcut: string; icon: React.ComponentType<{ className?: string }> }[] = [
  { id: "select", label: "Select", shortcut: "V", icon: MousePointer2 },
  { id: "rect", label: "Rectangle", shortcut: "R", icon: Square },
  { id: "circle", label: "Circle", shortcut: "C", icon: Circle },
  { id: "text", label: "Text", shortcut: "T", icon: Type },
  { id: "line", label: "Line", shortcut: "L", icon: Slash },
];

export function EditorToolbar() {
  const tool = useEditorStore((s) => s.tool);
  const setTool = useEditorStore((s) => s.setTool);
  const doc = useEditorStore((s) => s.doc);
  const addShape = useEditorStore((s) => s.addShape);
  const setSelected = useEditorStore((s) => s.setSelected);

  const fileRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  /**
   * Whether the pharmacy has a booking link saved.
   *
   * <b>Why the control is disabled without one.</b> Not merely because the
   * printed poster would have a gap — because `/api/brand-kit/booking-qr`
   * answers **404** when no link is set, so the image never loads and
   * `ImageShapeNode` renders nothing at all. The pharmacist would press the
   * button, watch nothing appear, and reasonably conclude it was broken.
   *
   * <b>Absent is a link, not a dead button.</b> A disabled control with a
   * `title` telling somebody to go and set a booking link is a dead end twice
   * over: a native tooltip cannot contain a link, and browsers show tooltips on
   * disabled buttons unreliably anyway — so the instruction may never be read,
   * and following it means knowing where Brand Kit is. In that state this
   * renders as a link straight there instead.
   *
   * <b>This does not contradict `applyPharmacyInfo`</b>, which deliberately
   * keeps the QR path on a slot even when there is no link, so posters start
   * working the moment one is added. That function serves a slot placed by a
   * *template author*, who cannot know whether a given pharmacy has a link.
   * This button serves a pharmacist placing one *now*, for whom an invisible
   * shape is just a broken button. Two actors, two right answers — worth saying
   * so nobody "fixes" one to match the other.
   */
  const [bookingLink, setBookingLink] = useState<
    "loading" | "present" | "absent" | "unknown"
  >("loading");

  useEffect(() => {
    let cancelled = false;

    function check() {
      api
        .getMe()
        // `/api/me` carries the link for display even though the brand kit
        // stores it, which is what lets this stay one request rather than two.
        .then((me) => {
          if (!cancelled) {
            setBookingLink(me.profile.bookingUrl ? "present" : "absent");
          }
        })
        // Distinct from "loading". Leaving it on "loading" forever would leave
        // the tooltip saying "checking…" when nothing is checking any more — a
        // button that is disabled and lying about why.
        .catch(() => {
          if (!cancelled) setBookingLink("unknown");
        });
    }

    check();

    // Re-checked when the tab regains focus, because this control sends people
    // to Brand Kit. They will go, set a link, and come back — and a check that
    // only ran on mount would still say no. A control that tells somebody what
    // to do and then ignores them having done it is worse than one that says
    // nothing.
    window.addEventListener("focus", check);
    return () => {
      cancelled = true;
      window.removeEventListener("focus", check);
    };
  }, []);

  // One per design: everything downstream finds the QR by this exact id, so a
  // second would overwrite the first the next time details are applied.
  const alreadyHasQr = doc.shapes.some((s) => s.id === PHARMACY_BOOKING_QR_ID);

  const qrTitle = alreadyHasQr
    ? "This poster already has a booking code"
    : bookingLink === "loading"
      ? "Checking your booking link…"
      : bookingLink === "present"
        ? "Add booking QR code"
        : bookingLink === "absent"
          ? "Set a booking link in your brand kit first — until then there is nothing for a code to point at"
          : "Could not check your booking link. Reload and try again.";

  const qrClassName =
    "flex h-10 w-10 items-center justify-center rounded-md border border-transparent text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:cursor-not-allowed disabled:opacity-40";

  function addBookingQr() {
    const size = 220;
    const shape = makeBookingQr(
      {
        x: Math.max(0, (doc.width - size) / 2),
        y: Math.max(0, (doc.height - size) / 2),
      },
      nextZIndex(doc.shapes),
      size,
    );
    addShape(shape);
    setSelected(shape.id);
  }

  async function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;

    setBusy(true);
    setError(null);
    try {
      const { dataUrl, width, height } = await prepareImage(file);

      const longest = Math.max(width, height);
      const fit = longest > 600 ? 600 / longest : 1;
      const w = Math.round(width * fit);
      const h = Math.round(height * fit);

      const shape = makeImage(
        {
          x: Math.max(0, (doc.width - w) / 2),
          y: Math.max(0, (doc.height - h) / 2),
        },
        nextZIndex(doc.shapes),
        dataUrl,
        w,
        h,
      );
      addShape(shape);
      setSelected(shape.id);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not add image.");
      window.setTimeout(() => setError(null), 4000);
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside className="flex w-14 shrink-0 flex-col gap-1 border-r bg-background p-2">
      {TOOLS.map(({ id, label, shortcut, icon: Icon }) => (
        <button
          key={id}
          type="button"
          onClick={() => setTool(id)}
          aria-label={`${label} (${shortcut})`}
          title={`${label} (${shortcut})`}
          className={cn(
            "flex h-10 w-10 items-center justify-center rounded-md border text-muted-foreground transition-colors",
            tool === id
              ? "border-primary bg-primary text-primary-foreground"
              : "border-transparent hover:bg-accent hover:text-accent-foreground",
          )}
        >
          <Icon className="h-5 w-5" />
        </button>
      ))}

      <button
        type="button"
        onClick={() => fileRef.current?.click()}
        disabled={busy}
        aria-label="Add image"
        title={busy ? "Adding image…" : "Add image"}
        className={cn(
          "mt-2 flex h-10 w-10 items-center justify-center rounded-md border border-transparent text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground disabled:opacity-60",
        )}
      >
        <ImageIcon className="h-5 w-5" />
      </button>
      <input
        ref={fileRef}
        type="file"
        accept="image/*"
        onChange={handleFile}
        className="hidden"
      />

      {bookingLink === "absent" && !alreadyHasQr ? (
        <Link
          href="/brand-kit"
          aria-label={qrTitle}
          title={qrTitle}
          className={cn(qrClassName, "opacity-60 hover:opacity-100")}
        >
          <QrCode className="h-5 w-5" />
        </Link>
      ) : (
        <button
          type="button"
          onClick={addBookingQr}
          disabled={alreadyHasQr || bookingLink !== "present"}
          aria-label={qrTitle}
          title={qrTitle}
          className={cn(qrClassName)}
        >
          <QrCode className="h-5 w-5" />
        </button>
      )}

      {error && (
        <div
          role="alert"
          className="mt-2 rounded-md bg-destructive/10 px-1 py-1 text-[10px] leading-tight text-destructive"
        >
          {error}
        </div>
      )}
    </aside>
  );
}
