"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import {
  TEMPLATE_CATEGORIES,
  type CanvasDocument,
  type PhotoOptions,
  type TemplateCategory,
} from "@acme/shared-types";
import { Loader2, PenLine, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { CanvasPreview } from "@/components/canvas/CanvasPreview";
import { EditorShell } from "@/components/editor/EditorShell";
import { ErrorNote, Panel } from "@/components/admin/AdminUi";
import { api } from "@/lib/api";
import {
  DEFAULT_PHOTO,
  photoBlockedReason,
  toRequestPhoto,
  type PhotoChoice,
} from "@/lib/poster-photo";
import { PhotoPicker } from "@/components/posters/PhotoPicker";
import { useEditorStore } from "@/store/editor-store";
import { cn } from "@/lib/utils";

/**
 * Generation is slow enough that a bare spinner reads as a hang. These rotate
 * so the page visibly stays alive, and they describe the real pipeline rather
 * than inventing reassurance.
 */
const PROGRESS_STEPS = [
  "Reading the brief…",
  "Preparing the photograph…",
  "Choosing a palette and layout…",
  "Placing type and artwork…",
  "Checking contrast, margins and draw order…",
  "Fixing anything that failed the print rules…",
];

const EXAMPLES = [
  "Walk-in flu clinic, bold: no appointment needed, every Saturday this autumn",
  "Trusted local pharmacy, circle portrait: advice, prescriptions and health checks",
  "Free blood pressure checks for the over-40s, calm and reassuring, checklist",
  "New travel vaccination service, diagonal: vaccinations and malaria advice",
];

export default function NewTemplatePage() {
  const router = useRouter();
  const loadDetached = useEditorStore((s) => s.loadDetached);

  const [prompt, setPrompt] = useState("");
  const [category, setCategory] = useState<TemplateCategory>("General");
  const [description, setDescription] = useState("");
  const [tags, setTags] = useState("");

  // Photograph: None, Stock (the default), Upload or AI — the same four
  // choices a pharmacy has. AI only appears when an image model exists.
  const [photo, setPhoto] = useState<PhotoChoice>(DEFAULT_PHOTO);
  const [photoOptions, setPhotoOptions] = useState<PhotoOptions | null>(null);
  const [uploading, setUploading] = useState(false);

  // Full-size preview overlay. Null when closed.
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);

  const [busy, setBusy] = useState(false);
  const [step, setStep] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  // What happened to the photo, when it was not what was asked for.
  const [photoNotes, setPhotoNotes] = useState<string[]>([]);
  const [canvas, setCanvas] = useState<CanvasDocument | null>(null);
  const [name, setName] = useState("");
  const [publishing, setPublishing] = useState(false);

  // Hand-finishing, in the real editor, without leaving this page.
  const [designerOpen, setDesignerOpen] = useState(false);

  // "Refine with AI": a note describing what to change.
  const [refineOpen, setRefineOpen] = useState(false);
  const [refineNote, setRefineNote] = useState("");

  /**
   * Adopts a design handed over in the editor store.
   *
   * Used by Duplicate on the template list, which loads a template, puts a copy
   * in the store and comes here — so the copy lands in a page that can name,
   * categorise and publish it. Before this it went to /canvas, where the only
   * possible outcome was losing it.
   *
   * `window.location.search` rather than `useSearchParams`, which forces the
   * whole page into a Suspense boundary for static rendering. This runs once,
   * on the client, after mount; there is nothing to suspend on.
   */
  useEffect(() => {
    if (!new URLSearchParams(window.location.search).has("from")) return;

    const handed = useEditorStore.getState().doc;
    if (!handed?.shapes?.length) return;

    setCanvas(handed);
    setName(handed.name || "Untitled template");
    // Not a generated design, so there are no correction notes to show.
    setNotes([]);
  }, []);

  // Advance the progress caption while the request is in flight.
  const timer = useRef<ReturnType<typeof setInterval> | null>(null);
  useEffect(() => {
    if (!busy) {
      if (timer.current) clearInterval(timer.current);
      return;
    }
    timer.current = setInterval(
      () => setStep((s) => Math.min(s + 1, PROGRESS_STEPS.length - 1)),
      9000,
    );
    return () => {
      if (timer.current) clearInterval(timer.current);
    };
  }, [busy]);

  useEffect(() => {
    let cancelled = false;
    api.photoOptions()
      .then((o) => { if (!cancelled) setPhotoOptions(o); })
      .catch(() => { if (!cancelled) setPhotoOptions({ ai: false, stock: true }); });
    return () => { cancelled = true; };
  }, []);

  /**
   * Why generation can't start yet, or null when it can.
   *
   * Returned as a message rather than a boolean so the UI can say which field
   * is missing. Ordered by where the field sits on the form, so the hint points
   * at the first thing to fix rather than an arbitrary one.
   */
  const blockedReason: string | null =
    prompt.trim().length < 8
      ? "Describe the poster to continue."
      : photoBlockedReason(photo, { uploading, aiAvailable: photoOptions?.ai === true });

  /**
   * Designs a poster from the brief, optionally with a change note.
   *
   * <b>A note produces a NEW design, not an edit of the one on screen.</b> The
   * generator takes a brief and builds a layout; nothing in the API or the
   * ai-service revises an existing canvas. So the note is appended to the brief
   * and the whole thing is designed again, around the same photograph. Said
   * plainly here and in the dialog, because "refine" implies the layout
   * survives and it does not — the button that promises otherwise is the one
   * that wastes somebody's afternoon.
   */
  async function generate(changeNote?: string) {
    setBusy(true);
    setStep(0);
    setError(null);
    setNotes([]);
    setPhotoNotes([]);
    setCanvas(null);
    try {
      const brief = changeNote?.trim()
        ? `${prompt.trim()}\n\nCHANGES REQUESTED: ${changeNote.trim()}`
        : prompt;
      const result = await api.admin.generateTemplate({
        prompt: brief,
        category,
        // An upload or a previewed AI image is already stored; a stock pick
        // travels as its id; an unpreviewed AI image is made with the design.
        photo: toRequestPhoto(photo),
      });
      setCanvas(JSON.parse(result.canvasJson) as CanvasDocument);
      setName(result.name);
      setNotes(result.notes ?? []);
      setPhotoNotes(result.photoNotes ?? []);
      if (!description) setDescription(prompt.trim());
    } catch (err) {
      setError(err instanceof Error ? err.message : "Generation failed.");
    } finally {
      setBusy(false);
    }
  }

  async function publish() {
    if (!canvas) return;
    setPublishing(true);
    setError(null);
    try {
      const created = await api.createTemplate({
        name: name.trim() || "Untitled template",
        description: description.trim(),
        category,
        tags: tags.split(",").map((t) => t.trim()).filter(Boolean),
        canvas,
        isPublished: true,
      });
      router.push(`/admin/templates?published=${created.id}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not publish.");
      setPublishing(false);
    }
  }

  /**
   * Opens the real editor on this design, in place.
   *
   * <b>The old "Refine" pushed to /canvas</b>, which is a scratch playground —
   * a bare stage with Add rect / Add circle / Add text and nothing else. It
   * looked like a broken page because, for this purpose, it was one: no
   * properties panel, no layers, no way back, and the design only appeared
   * there at all because both read the same store.
   *
   * This mounts <see cref="EditorShell"/> — the editor pharmacies use — over
   * the page rather than navigating. No route, and more importantly no way to
   * lose the work: the design has not been saved anywhere yet, so a navigation
   * that went wrong would have nothing to come back to.
   */
  function openDesigner() {
    if (!canvas) return;
    // Detached: this design belongs to no poster, and autosave must stay off.
    // Handing the editor a document with a stale posterId still in the store
    // would have it write this template over that poster.
    loadDetached({ name: name.trim() || "Untitled template", canvas });
    setDesignerOpen(true);
  }

  /**
   * Takes the edited document back out of the editor store.
   *
   * The store is the editor's working copy; this page owns what gets published.
   * Reading it back on close is what makes hand-edits survive to Publish.
   */
  function closeDesigner() {
    const { doc, posterName } = useEditorStore.getState();
    setCanvas(doc);

    // The editor's title field is the obvious place to rename a design while
    // you are looking at it. Without this it would be a control that appears
    // to work and is thrown away on close — so a rename there wins, and only
    // when the admin actually typed something.
    if (posterName.trim() && posterName !== "Untitled template") {
      setName(posterName.trim());
    }

    setDesignerOpen(false);
  }

  async function refineWithAi() {
    const note = refineNote.trim();
    if (!note) return;
    setRefineOpen(false);
    setRefineNote("");
    await generate(note);
  }

  return (
    <div className="space-y-4">
      {error && <ErrorNote message={error} />}

      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,420px)]">
        <Panel
          title="Describe the poster"
          description="Say what it advertises, who it's for, and the mood. The designer picks the palette, layout and typography."
        >
          <div className="space-y-4 p-4">
            <div>
              <Label className="text-xs">Brief</Label>
              <textarea
                value={prompt}
                onChange={(e) => setPrompt(e.target.value)}
                rows={4}
                placeholder="e.g. Walk-in flu vaccination clinic every Saturday through November. Bold and urgent, photo-led, aimed at over-65s."
                className="mt-1 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
              />
              <div className="mt-2 flex flex-wrap gap-1.5">
                {EXAMPLES.map((ex) => (
                  <button
                    key={ex}
                    type="button"
                    onClick={() => setPrompt(ex)}
                    className="rounded-full border border-border/60 px-2.5 py-1 text-[11px] text-muted-foreground hover:bg-accent"
                  >
                    {ex.split(",")[0]}
                  </button>
                ))}
              </div>
            </div>

            <div>
              <Label className="text-xs">Category</Label>
              <select
                value={category}
                onChange={(e) => setCategory(e.target.value as TemplateCategory)}
                className="mt-1 h-9 w-full rounded-md border border-input bg-background px-2 text-sm"
              >
                {TEMPLATE_CATEGORIES.map((c) => (
                  <option key={c} value={c}>{c}</option>
                ))}
              </select>
            </div>

            <PhotoPicker
              value={photo}
              onChange={setPhoto}
              options={photoOptions}
              search={api.admin.searchStockPhotos}
              upload={api.admin.uploadTemplateAsset}
              aiPreview={(description) =>
                api.admin.createPosterImage({ description, aspect: "portrait", provider: "ai" })
              }
              onUploadingChange={setUploading}
              disabled={busy}
            />
            {photo.previewUrl && (
              <button
                type="button"
                onClick={() => setPreviewUrl(photo.previewUrl ?? null)}
                className="text-[11px] text-primary hover:underline"
              >
                View the photo full size
              </button>
            )}

            <Button
              /*
                Wrapped, not passed. `generate` takes an optional change note,
                and React hands a click handler its MouseEvent — so
                `onClick={generate}` would post the event object into the brief.
                tsc caught it; at runtime it would have been a brief ending in
                "[object Object]".
              */
              onClick={() => void generate()}
              disabled={busy || blockedReason !== null}
              className="w-full"
            >
              {busy ? "Designing…" : canvas ? "Generate another" : "Generate design"}
            </Button>

            {/*
              A disabled button with no explanation is just a dead end — the
              missing field is often the photograph description, which is easy
              to miss after picking a mode and a shape.
            */}
            {!busy && blockedReason && (
              <p className="text-center text-xs text-muted-foreground">
                {blockedReason}
              </p>
            )}

          </div>
        </Panel>

        <Panel
          title="Preview"
          description={canvas ? "Publish as-is, or open it in the designer to adjust." : "Your design will appear here."}
        >
          <div className="space-y-3 p-4">
            <div
              className={cn(
                "flex aspect-[1240/1754] w-full items-center justify-center overflow-hidden rounded-md border border-border/60 bg-muted",
                busy && "animate-pulse",
              )}
            >
              {canvas && !busy ? (
                <CanvasPreview doc={canvas} className="h-full w-full" withoutBrandAssets />
              ) : busy ? (
                /*
                  The progress lives here, not under the Generate button.

                  It used to sit at the bottom of the left-hand panel while this
                  box — the thing an admin is actually staring at for a minute
                  and a half — said only "Designing…". The stage captions were
                  being written to a part of the screen nobody was looking at.
                */
                <div className="flex w-full flex-col items-center gap-3 px-6 text-center">
                  <Loader2 className="h-6 w-6 animate-spin text-muted-foreground" />
                  <p
                    className="text-sm font-medium text-foreground"
                    role="status"
                    aria-live="polite"
                  >
                    {PROGRESS_STEPS[step]}
                  </p>
                  <div className="h-1 w-40 overflow-hidden rounded-full bg-border">
                    <div
                      className="h-full rounded-full bg-primary transition-all duration-700"
                      style={{
                        width: `${((step + 1) / PROGRESS_STEPS.length) * 100}%`,
                      }}
                    />
                  </div>
                  <p className="text-xs text-muted-foreground">
                    Usually 30–90 seconds. You can leave this tab open.
                  </p>
                </div>
              ) : (
                <span className="px-6 text-center text-xs text-muted-foreground">
                  No design yet
                </span>
              )}
            </div>

            {photoNotes.map((n) => (
              <p key={n} className="rounded-md bg-amber-500/10 px-2.5 py-2 text-[11px] text-amber-700 dark:text-amber-400">
                {n}
              </p>
            ))}

            {notes.length > 0 && (
              <p className="rounded-md bg-amber-500/10 px-2.5 py-2 text-[11px] text-amber-700 dark:text-amber-400">
                The first attempt needed correcting ({notes.length} round
                {notes.length > 1 ? "s" : ""}). Worth a closer look before
                publishing.
              </p>
            )}

            {canvas && (
              <div className="space-y-3">
                <div>
                  <Label className="text-xs">Name</Label>
                  <Input
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    className="mt-1 h-9 text-sm"
                  />
                </div>
                <div>
                  <Label className="text-xs">Description</Label>
                  <Input
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    className="mt-1 h-9 text-sm"
                  />
                </div>
                <div>
                  <Label className="text-xs">Tags</Label>
                  <Input
                    value={tags}
                    onChange={(e) => setTags(e.target.value)}
                    placeholder="flu, vaccination, winter"
                    className="mt-1 h-9 text-sm"
                  />
                </div>

                <div className="grid grid-cols-2 gap-2">
                  <Button variant="outline" onClick={() => setRefineOpen(true)} disabled={busy}>
                    <Sparkles className="mr-1.5 h-3.5 w-3.5" />
                    Refine with AI
                  </Button>
                  <Button variant="outline" onClick={openDesigner} disabled={busy}>
                    <PenLine className="mr-1.5 h-3.5 w-3.5" />
                    Edit in designer
                  </Button>
                </div>

                <Button onClick={publish} disabled={publishing} className="w-full">
                  {publishing ? "Publishing…" : "Publish to gallery"}
                </Button>
                <p className="text-[11px] text-muted-foreground">
                  Publishing makes this visible to every pharmacy.
                </p>
              </div>
            )}
          </div>
        </Panel>
      </div>

      {/*
        The editor, over the page rather than at a route.

        Fixed and full-screen so it behaves like the editor it is, while this
        page keeps ownership of the design: nothing here is saved server-side
        until Publish, and closing reads the edited document back out of the
        store.
      */}
      {designerOpen && (
        <div className="fixed inset-0 z-50 bg-background">
          <EditorShell onBack={closeDesigner} />
        </div>
      )}

      {refineOpen && (
        <RefineDialog
          note={refineNote}
          onNote={setRefineNote}
          onCancel={() => {
            setRefineOpen(false);
            setRefineNote("");
          }}
          onConfirm={refineWithAi}
        />
      )}

      {previewUrl && (
        <ImagePreview
          url={previewUrl}
          caption={photo.description ?? null}
          onClose={() => setPreviewUrl(null)}
        />
      )}
    </div>
  );
}

/**
 * Asks what should change, then designs again.
 *
 * <b>The wording is deliberate.</b> This produces a new design rather than
 * editing the one on screen — the generator has no revise path — and a dialog
 * that said "refine" without saying so would have admins expecting their layout
 * to survive. Anyone who wants the layout kept should use Edit in designer,
 * which is why both are offered side by side.
 */
function RefineDialog({
  note,
  onNote,
  onCancel,
  onConfirm,
}: {
  note: string;
  onNote: (v: string) => void;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  // Escape closes it. A modal that traps you until you find the Cancel button
  // is a modal people learn to distrust, and every other overlay on this page
  // (the image preview) already does this.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onCancel();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onCancel]);

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      role="dialog"
      aria-modal="true"
      aria-label="Refine with AI"
      onClick={onCancel}
    >
      <div
        className="w-full max-w-md rounded-lg border border-border bg-background p-5 shadow-lg"
        onClick={(e) => e.stopPropagation()}
      >
        <h2 className="text-sm font-semibold">Refine with AI</h2>
        <p className="mt-1 text-xs text-muted-foreground">
          Say what should change. This designs the poster again from your brief
          plus this note, keeping the same photograph —{" "}
          <span className="font-medium text-foreground">
            the layout will be different
          </span>
          . To keep this layout and adjust it by hand, use Edit in designer.
        </p>

        <textarea
          value={note}
          onChange={(e) => onNote(e.target.value)}
          rows={3}
          autoFocus
          placeholder="e.g. Make the headline much bigger and drop the blue — too corporate."
          className="mt-3 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
        />

        <div className="mt-4 flex justify-end gap-2">
          <Button variant="ghost" size="sm" onClick={onCancel}>
            Cancel
          </Button>
          <Button size="sm" onClick={onConfirm} disabled={!note.trim()}>
            Design again
          </Button>
        </div>
      </div>
    </div>
  );
}

/**
 * Full-size overlay for inspecting a photograph before designing around it.
 *
 * Deliberately plain: no zoom or pan. The question being answered is "is this
 * the right picture?", which a large view settles on its own.
 */
function ImagePreview({
  url,
  caption,
  onClose,
}: {
  url: string;
  caption: string | null;
  onClose: () => void;
}) {
  // Escape closes, and body scroll is locked so the page behind doesn't move
  // under the overlay.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    const previous = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      window.removeEventListener("keydown", onKey);
      document.body.style.overflow = previous;
    };
  }, [onClose]);

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label="Image preview"
      onClick={onClose}
      className="fixed inset-0 z-50 flex flex-col items-center justify-center gap-3 bg-black/80 p-6 backdrop-blur-sm"
    >
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src={url}
        alt={caption ?? "Preview"}
        // Stop the click from bubbling to the backdrop, so clicking the picture
        // itself doesn't dismiss it.
        onClick={(e) => e.stopPropagation()}
        className="max-h-[80vh] max-w-full rounded-lg object-contain shadow-2xl"
      />
      {caption && (
        <p className="max-w-xl text-center text-xs text-white/80">{caption}</p>
      )}
      <button
        type="button"
        onClick={onClose}
        className="rounded-md bg-white/10 px-3 py-1.5 text-xs text-white hover:bg-white/20"
      >
        Close (Esc)
      </button>
    </div>
  );
}
