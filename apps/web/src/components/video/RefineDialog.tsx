"use client";

import { useEffect, useRef, useState } from "react";
import { AlertCircle, CheckCircle2, ImagePlus, Loader2, Wand2, X } from "lucide-react";
import {
  MAX_REFINE_INSTRUCTION_CHARS,
  DEFAULT_VIDEO_IMAGE_FOCUS,
  VIDEO_IMAGE_ZOOM_DEFAULT,
  MAX_VIDEO_IMAGES,
  type RefinePlan,
  type Video,
  type VideoImage,
  whyVideoImageRefused,
} from "@acme/shared-types";
import { FramingHint, PhotoFramer } from "@/components/video/PhotoFramer";
import { toImagePayload, toShotChoices } from "@/lib/video-refine";
import { ShotPicker } from "@/components/video/ShotPicker";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { api } from "@/lib/api";

/**
 * Ask for a change to a finished video, in the pharmacist's own words.
 *
 * <b>Two steps, and the second one is the point.</b> What a sentence means for
 * the pipeline is decided by a model, and a wrong answer is expensive in one
 * direction and invisible in the other: a caption fix routed to the
 * scriptwriter rewrites the narration and bills three providers, while "make
 * it friendlier" routed to the editor re-cuts identical footage and reports
 * success. So the first press asks what would happen, and nothing runs until
 * somebody has read the answer and agreed to it.
 *
 * <b>Two lanes are open, and the closed one is worded carefully.</b> Captions,
 * timing and the closing card are a re-cut; changing what is on screen at a
 * named moment re-searches stock footage. Both reuse the stored narration, so
 * both are free and neither changes the video's length. An instruction that
 * means "say something different" still comes back as a plan that cannot run —
 * that is a normal answer, not a failure, and it is shown as one.
 */
export function RefineDialog({
  video,
  onClose,
  onQueued,
}: {
  /** The video being changed. Null closes the dialog. */
  video: Video | null;
  onClose: () => void;
  onQueued: () => Promise<void> | void;
}) {
  const [instruction, setInstruction] = useState("");
  const [plan, setPlan] = useState<RefinePlan | null>(null);

  /**
   * The wording the plan was made for.
   *
   * Kept because the plan is stored server-side against this exact sentence and
   * the confirmation is checked against it. Editing the box after previewing
   * has to invalidate the plan, or the button would offer to run something
   * other than what is written above it.
   */
  const [plannedFor, setPlannedFor] = useState<string | null>(null);

  const [busy, setBusy] = useState<"preview" | "confirm" | "pictures" | "shots" | null>(null);
  /** Shots tapped in the picker → what each should show. */
  const [shotsChosen, setShotsChosen] = useState<Map<number, string>>(new Map());
  const [error, setError] = useState<string | null>(null);

  /**
   * Pictures chosen in this dialog, already uploaded but not yet on the video.
   *
   * `preview` is a local object URL for the thumbnail, so nothing has to be
   * fetched back to show what was just picked.
   */
  const [pictures, setPictures] = useState<
    (VideoImage & {
      preview: string;
      label: string;
      focalX: number;
      focalY: number;
      zoom: number;
    })[]
  >([]);
  const [attaching, setAttaching] = useState(false);
  const pictureInput = useRef<HTMLInputElement>(null);

  const open = video !== null;

  // What the video already carries, and therefore what is left.
  const already = video?.imageCount ?? 0;
  const room = Math.max(0, MAX_VIDEO_IMAGES - already - pictures.length);

  /**
   * AI-imagery videos cannot take pictures after the fact.
   *
   * Re-cutting one regenerates every other beat through Replicate, which the
   * plan pays for — so the server refuses it. Said here too, before they pick a
   * file, rather than after they have chosen four and pressed the button.
   */
  const picturesAllowed = video?.visualStyle !== "AiImagery";

  useEffect(() => {
    if (!open) return;
    setInstruction("");
    setPlan(null);
    setPlannedFor(null);
    setBusy(null);
    setError(null);
    setShotsChosen(new Map());
    // Revoked rather than dropped: an object URL pins the file in memory until
    // it is released, and this dialog is opened repeatedly in a session.
    setPictures((previous) => {
      previous.forEach((p) => URL.revokeObjectURL(p.preview));
      return [];
    });
  }, [open, video?.id]);

  const typed = instruction.trim();

  /**
   * Whether the plan on screen still describes what is in the box.
   *
   * Trimmed and lower-cased to match the server's comparison, which is trimmed
   * and case-insensitive so that capitalising a word between seeing the plan
   * and pressing the button is not a refusal. The two need not agree perfectly:
   * a false "no" costs one extra preview call, and a false "yes" is caught by
   * the server, which says plainly that this is not the change it described.
   */
  const planIsCurrent =
    plan !== null && plannedFor !== null && plannedFor === typed.toLowerCase();

  /**
   * A "no" that asking again cannot turn into a "yes".
   *
   * <b>Only when the refusal names a stage.</b> That means the sentence was
   * understood and routed to a lane we have not opened, and whether a lane is
   * open is a constant on the server — so re-asking the identical sentence is a
   * wasted model call, and worse than wasted if classification answers
   * differently the second time. A button that changes its mind about identical
   * input is a slot machine.
   *
   * A stageless refusal is left re-askable, because the two things that produce
   * one can both stop being true: an instruction we could not interpret, and a
   * video that was not finished when we looked. Neither is settled, and the
   * pharmacist should not have to retype a sentence to find out.
   */
  const settled =
    planIsCurrent && plan !== null && !plan.canRun && plan.stage !== null;

  async function attachPictures(files: FileList | null) {
    if (!files?.length || !video) return;
    setError(null);

    if (room <= 0) {
      setError(
        already >= MAX_VIDEO_IMAGES
          ? `This video already has ${MAX_VIDEO_IMAGES} pictures, which is the most it can carry.`
          : `There is no room for more — ${MAX_VIDEO_IMAGES} is the limit.`,
      );
      return;
    }

    const chosen = Array.from(files).slice(0, room);
    if (chosen.length < files.length) {
      setError(`Only the first ${chosen.length} were added — ${MAX_VIDEO_IMAGES} is the limit.`);
    }

    setAttaching(true);
    try {
      for (const file of chosen) {
        // Checked before the upload, not after. The server's size limit is
        // enforced by the framework before our code runs, so it answers a
        // too-big file with a bare 413 and nothing to explain itself — after
        // the whole file has gone up.
        const refusal = whyVideoImageRefused(file);
        if (refusal) {
          setError(refusal);
          continue;
        }

        try {
          const stored = await api.uploadVideoImage(file);
          setPictures((current) => [
            ...current,
            {
              ...stored,
              preview: URL.createObjectURL(file),
              label: file.name,
              focalX: DEFAULT_VIDEO_IMAGE_FOCUS,
              focalY: DEFAULT_VIDEO_IMAGE_FOCUS,
              zoom: VIDEO_IMAGE_ZOOM_DEFAULT,
            },
          ]);
        } catch (err) {
          // Per file, so one rejected picture does not discard the others.
          setError(
            err instanceof Error ? err.message : `${file.name} could not be attached.`,
          );
        }
      }
    } finally {
      setAttaching(false);
      // Cleared so choosing the same file again still fires a change event.
      if (pictureInput.current) pictureInput.current.value = "";
    }
  }

  function removePicture(blobKey: string) {
    setPictures((current) => {
      const going = current.find((p) => p.blobKey === blobKey);
      if (going) URL.revokeObjectURL(going.preview);
      return current.filter((p) => p.blobKey !== blobKey);
    });
    setError(null);
  }

  /**
   * Add the chosen pictures and re-cut.
   *
   * No preview and no confirmation, unlike the sentence path. That handshake
   * exists because a model decided what a sentence meant and could have decided
   * wrongly — there is nothing here for it to decide.
   */
  /**
   * Re-cut with new footage at the tapped shots. No preview: the shot is the
   * tile they tapped, so nothing was interpreted that they would need to check.
   */
  async function changeShots() {
    if (!video || shotsChosen.size === 0) return;
    setBusy("shots");
    setError(null);
    try {
      await api.changeVideoShots(video.id, toShotChoices(shotsChosen));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Those shots could not be changed.");
      return;
    } finally {
      setBusy(null);
    }
    // Outside the try, as with pictures: the work is queued by now.
    onClose();
    await onQueued();
  }

  async function addPictures() {
    if (!video || pictures.length === 0) return;
    setBusy("pictures");
    setError(null);
    try {
      await api.addVideoPictures(
        video.id,
        toImagePayload(pictures),
      );
    } catch (err) {
      setError(
        err instanceof Error ? err.message : "Those pictures could not be added.",
      );
      return;
    } finally {
      setBusy(null);
    }

    // Outside the try, matching `runChange` below: by this point the work is
    // queued, and reporting a failure to refresh as a failure to queue would
    // have them ask for it a second time.
    onClose();
    await onQueued();
  }

  async function preview() {
    if (!video || !typed) {
      setError("Say what you would like changed.");
      return;
    }
    setBusy("preview");
    setError(null);
    try {
      const result = await api.previewRefine(video.id, typed);
      setPlan(result);
      setPlannedFor(typed.toLowerCase());
    } catch (err) {
      setPlan(null);
      setPlannedFor(null);
      setError(
        err instanceof Error
          ? err.message
          : "Could not work out what that change would do.",
      );
    } finally {
      setBusy(null);
    }
  }

  /**
   * Named for what it does, not "confirm" — that is a global this component
   * would otherwise shadow, and a `confirm()` in a dialog that does not open a
   * browser confirmation is a trap for the next person editing this file.
   */
  async function runChange() {
    if (!video || !planIsCurrent) return;
    setBusy("confirm");
    setError(null);
    try {
      // The same sentence goes back up, and has to match the stored plan. That
      // is what makes agreeing to it mean something.
      await api.refineVideo(video.id, typed);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Could not make that change.");
      return;
    } finally {
      setBusy(null);
    }

    // Outside the try, so a failure while refreshing the list cannot be
    // reported as a failure to make the change. By this point the work is
    // queued, and telling somebody it was not is worse than telling them
    // nothing — they would ask for it a second time.
    onClose();
    await onQueued();
  }

  return (
    <Dialog
      open={open}
      // Escape and a click outside are refused while a request is in flight,
      // matching the disabled Cancel button beside them. They were not, and the
      // gap mattered on the confirm: closing mid-request left `setError` writing
      // into a dialog nobody could see, wiped on the next open — so a refine
      // that failed looked exactly like a refine that had been queued, and the
      // pharmacist would sit waiting for a video that was never going to change.
      onOpenChange={(v) => { if (!v && busy === null) onClose(); }}
    >
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Wand2 className="h-4 w-4" />
            Ask for a change
          </DialogTitle>
          <DialogDescription>
            {video
              ? picturesAllowed
                ? `Tap a shot of "${video.name}" to change it, or describe another change and we'll tell you what it would do first.`
                : `We'll tell you what the change would do before anything happens to "${video.name}".`
              : null}
          </DialogDescription>
        </DialogHeader>

        {/*
          The shot picker comes first: changing a shot is the commonest ask,
          and tapping it is the one way to say which shot that cannot be
          misread. Not offered on AI-imagery videos, for the same reason
          pictures are not (see `picturesAllowed`).
        */}
        {picturesAllowed && video && (
          <div className="space-y-2">
            <Label>Change a shot — tap it</Label>
            <ShotPicker
              videoId={video.id}
              version={video.updatedAtUtc}
              selected={shotsChosen}
              onChange={(next) => {
                setShotsChosen(next);
                setError(null);
              }}
              disabled={busy !== null}
            />
          </div>
        )}

        <div className={picturesAllowed ? "space-y-2 border-t pt-4" : "space-y-2"}>
          <Label htmlFor="refine-instruction">
            {picturesAllowed ? "Or describe another change" : "What would you like changed?"}
          </Label>
          <Textarea
            id="refine-instruction"
            value={instruction}
            // Note what this does *not* do: clear `plan`. The panel stays on
            // screen while the sentence it was made for is edited, greyed out
            // rather than vanishing — one that disappears on the first
            // keystroke takes the explanation with it, and the explanation is
            // the part worth reading. `planIsCurrent` handles the rest.
            onChange={(e) => {
              setInstruction(e.target.value);
              setError(null);
            }}
            rows={3}
            maxLength={MAX_REFINE_INSTRUCTION_CHARS}
            placeholder="e.g. make the captions bigger, or change the last shot to a pharmacy counter"
            disabled={busy !== null}
          />
          <p className="text-xs text-muted-foreground">
            Plain English, for the captions, timing and closing card. To change
            a shot, tap it above — or name it by position: &ldquo;change the last
            shot to a pharmacy counter&rdquo;, &ldquo;shot 2 to people walking&rdquo;.
            We don&apos;t pick a shot from a description of what is in it. The
            spoken words stay as they are, so the video keeps its length;
            changing what is <em>said</em> needs a new video.
          </p>
        </div>

        {/*
          Adding pictures is a separate action, not a variation of the sentence
          above — it goes to its own endpoint, needs no plan and no agreement,
          and the two are never sent together. Kept in the same dialog because
          "change this video" is one thought for the person having it.
        */}
        {picturesAllowed && (
          <div className="space-y-2 border-t pt-4">
            <div className="flex items-center justify-between gap-2">
              <Label htmlFor="refine-pictures">Or add your own pictures</Label>
              <span className="text-xs text-muted-foreground">
                {already > 0 ? `${already} already on this video · ` : ""}
                {room > 0 ? `room for ${room} more` : "no room for more"}
              </span>
            </div>

            <input
              ref={pictureInput}
              id="refine-pictures"
              type="file"
              accept="image/jpeg,image/png,image/webp"
              multiple
              className="hidden"
              onChange={(e) => void attachPictures(e.target.files)}
            />

            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => pictureInput.current?.click()}
              disabled={attaching || busy !== null || room <= 0}
            >
              {attaching ? (
                <>
                  <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
                  Uploading…
                </>
              ) : (
                <>
                  <ImagePlus className="mr-1.5 h-4 w-4" />
                  Choose pictures
                </>
              )}
            </Button>

            {pictures.length > 0 && (
              // Framed here as well as on the create dialog. A photograph added
              // to a finished video is cropped exactly the same way, so leaving
              // the choice out of this path would mean the same landscape photo
              // behaving differently depending on which screen attached it.
              <div className="space-y-1.5">
                <FramingHint />
                <div className="grid grid-cols-2 gap-2 pt-1 sm:grid-cols-4">
                  {pictures.map((picture) => (
                    <div key={picture.blobKey} className="relative">
                      <PhotoFramer
                        src={picture.preview}
                        alt={picture.label}
                        focalX={picture.focalX}
                        focalY={picture.focalY}
                        zoom={picture.zoom}
                        onChange={(focalX, focalY, zoom) =>
                          setPictures((current) =>
                            current.map((p) =>
                              p.blobKey === picture.blobKey ? { ...p, focalX, focalY, zoom } : p,
                            ),
                          )
                        }
                      />
                      <button
                        type="button"
                        onClick={() => removePicture(picture.blobKey)}
                        className="absolute -right-1.5 -top-1.5 rounded-full bg-background p-0.5 shadow ring-1 ring-border"
                        aria-label={`Remove ${picture.label}`}
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            )}

            <p className="text-xs text-muted-foreground">
              Your photos go into the video alongside the footage already there.
              The words and the voice are kept, and this costs nothing — no extra
              AI-image charge.
            </p>
          </div>
        )}

        {/*
          The live region is separate from the panel, mounted always, and only
          its text comes and goes.

          Putting `aria-live` on the panel itself did not work: a region
          inserted into the page at the same moment as its content is commonly
          not announced at all, because screen readers watch regions that were
          already there. Announcing this matters more than most — it arrives
          after a wait, it is the entire reason the first button exists, and
          without it a screen-reader user gets a button that silently changed
          its label.

          `sr-only` is `position: absolute`, so this is out of flow and does not
          become an empty row in the dialog's `grid gap-4`.
        */}
        <div role="status" aria-live="polite" className="sr-only">
          {plan ? (plan.canRun ? plan.summary : plan.reason) : ""}
        </div>

        {plan && (
          <div
            className={[
              "rounded-md border p-3 text-sm transition-opacity",
              plan.canRun
                ? "border-emerald-500/30 bg-emerald-500/5"
                : "border-amber-500/30 bg-amber-500/5",
              planIsCurrent ? "opacity-100" : "opacity-50",
            ].join(" ")}
          >
            <div className="flex items-start gap-2">
              {plan.canRun ? (
                <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-emerald-600 dark:text-emerald-400" />
              ) : (
                <AlertCircle className="mt-0.5 h-4 w-4 shrink-0 text-amber-600 dark:text-amber-400" />
              )}
              <div className="space-y-1">
                {/* Summary when it can run, reason when it cannot. The server
                    leaves Summary empty on a refusal and always populates
                    Reason, so this covers both without either being blank. */}
                <p className="font-medium leading-snug">
                  {plan.canRun ? plan.summary : plan.reason}
                </p>
                {plan.canRun && (
                  <p className="text-xs text-muted-foreground">
                    {plan.free
                      ? "This costs nothing and does not use another video from your allowance."
                      : "This uses the generation services your plan pays for."}
                  </p>
                )}
              </div>
            </div>

            {!planIsCurrent && (
              <p className="mt-2 border-t pt-2 text-xs text-muted-foreground">
                You have changed the wording since this was worked out. Check it
                again to see what the new wording would do.
              </p>
            )}
          </div>
        )}

        {error && (
          <p className="text-sm text-destructive" role="alert">
            {error}
          </p>
        )}

        <DialogFooter>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy !== null}>
            Cancel
          </Button>

          {/* Pictures take the primary button when any are chosen: that is the
              most recent thing they did, and the sentence path still needs a
              preview before it can run anyway. */}
          {shotsChosen.size > 0 ? (
            <Button
              type="button"
              onClick={changeShots}
              disabled={busy !== null}
              className="bg-brand-gradient text-primary-foreground hover:opacity-90"
            >
              {busy === "shots" ? (
                <>
                  <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
                  Starting…
                </>
              ) : (
                `Change ${shotsChosen.size === 1 ? "this shot" : `${shotsChosen.size} shots`} and re-cut`
              )}
            </Button>
          ) : pictures.length > 0 ? (
            <Button
              type="button"
              onClick={addPictures}
              disabled={busy !== null || attaching}
              className="bg-brand-gradient text-primary-foreground hover:opacity-90"
            >
              {busy === "pictures" ? (
                <>
                  <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
                  Starting…
                </>
              ) : (
                `Add ${pictures.length === 1 ? "picture" : `${pictures.length} pictures`} and re-cut`
              )}
            </Button>
          ) : planIsCurrent && plan?.canRun ? (
            <Button
              type="button"
              onClick={runChange}
              disabled={busy !== null}
              className="bg-brand-gradient text-primary-foreground hover:opacity-90"
            >
              {busy === "confirm" ? (
                <>
                  <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
                  Starting…
                </>
              ) : (
                "Make this change"
              )}
            </Button>
          ) : (
            <Button
              type="button"
              onClick={preview}
              disabled={busy !== null || typed.length === 0 || settled}
              title={
                settled
                  ? "Change the wording to ask something else."
                  : undefined
              }
            >
              {busy === "preview" ? (
                <>
                  <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
                  Working it out…
                </>
              ) : (
                "Check what this will do"
              )}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
