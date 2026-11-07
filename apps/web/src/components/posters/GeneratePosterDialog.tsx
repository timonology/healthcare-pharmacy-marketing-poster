"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { Loader2, Sparkles } from "lucide-react";
import {
  MAX_POSTER_PROMPT_CHARS,
  MIN_POSTER_PROMPT_CHARS,
  type CurrentSubscription,
  type PhotoOptions,
} from "@acme/shared-types";
import { isUnlimited } from "@acme/shared-types";
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
import {
  DEFAULT_PHOTO,
  photoBlockedReason,
  toRequestPhoto,
  type PhotoChoice,
} from "@/lib/poster-photo";
import { PhotoPicker } from "./PhotoPicker";

/**
 * Describe a poster; get one back, designed and saved.
 *
 * <b>The allowance is shown before it is spent, not after.</b> "3 AI designs a
 * month" is a number a pharmacist is entitled to see while deciding whether to
 * use one — finding out by being refused is how a limit feels like a fault.
 * Pro is unlimited and shown as such rather than as a count, because counting
 * something unlimited invites the question of what happens at the top.
 */
export function GeneratePosterDialog({
  open,
  onOpenChange,
  subscription,
  onGenerated,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  /** Drives the allowance line. Null while it is still loading. */
  subscription: CurrentSubscription | null;
  onGenerated: () => Promise<void> | void;
}) {
  const router = useRouter();
  const [prompt, setPrompt] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Stock by default: a pharmacy that does not upload still gets a photo.
  const [photo, setPhoto] = useState<PhotoChoice>(DEFAULT_PHOTO);
  const [uploading, setUploading] = useState(false);
  const [options, setOptions] = useState<PhotoOptions | null>(null);
  // Set when the design is ready but its photo is not what was asked for.
  const [done, setDone] = useState<{ id: string; notes: string[] } | null>(null);

  useEffect(() => {
    if (!open) return;
    setPrompt("");
    setBusy(false);
    setError(null);
    setPhoto(DEFAULT_PHOTO);
    setDone(null);
    let cancelled = false;
    // Which sources exist decides whether AI is offered at all. A failure here
    // reads as "none": the AI mode stays hidden rather than offered and broken.
    api.photoOptions()
      .then((o) => { if (!cancelled) setOptions(o); })
      .catch(() => { if (!cancelled) setOptions({ ai: false, stock: true }); });
    return () => { cancelled = true; };
  }, [open]);

  const cap = subscription?.plan.maxAiGenerationsPerMonth ?? null;
  const used = subscription?.usage.aiGenerationsThisMonth ?? 0;
  const unlimited = cap !== null && isUnlimited(cap);
  const remaining = cap === null || unlimited ? null : Math.max(0, cap - used);

  // Only a known-zero remaining blocks the button. A null cap means the
  // subscription has not loaded, and refusing on missing information would
  // make a slow network look like an exhausted plan.
  const exhausted = remaining === 0;
  const typed = prompt.trim();
  const photoBlocked = photoBlockedReason(photo, { uploading, aiAvailable: options?.ai === true });

  async function generate() {
    if (typed.length < MIN_POSTER_PROMPT_CHARS) {
      setError("Describe the poster you want in a sentence or two.");
      return;
    }
    if (photoBlocked) {
      setError(photoBlocked);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const { poster, photoNotes } = await api.generatePoster({
        prompt: typed,
        photo: toRequestPhoto(photo),
      });
      await onGenerated();
      if (photoNotes.length > 0) {
        // The photo is not what was asked for — say so before opening it,
        // rather than letting a photo-free poster appear without a word.
        setDone({ id: poster.id, notes: photoNotes });
        return;
      }
      onOpenChange(false);
      // Straight into the editor. The design is a first draft — the value is
      // in editing it, and a pharmacist returned to a grid of thumbnails has
      // to work out which one is new.
      router.push(`/posters/${poster.id}`);
    } catch (err) {
      // Shown verbatim. The API's refusals are written to be read by a
      // pharmacist — which plan they are on, how many designs are left, when
      // it resets — and replacing them with "something went wrong" throws all
      // of that away.
      setError(err instanceof Error ? err.message : "Could not design that poster.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Dialog
      open={open}
      onOpenChange={(v) => { if (!v && !busy) onOpenChange(false); }}
    >
      <DialogContent className="max-w-xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Sparkles className="h-4 w-4" />
            Design a poster with AI
          </DialogTitle>
          <DialogDescription>
            Describe what you need. We&apos;ll design it in our modern pharmacy
            style with your pharmacy&apos;s details, logo and booking code, ready
            for you to edit.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-2">
          <Label htmlFor="poster-prompt">What is the poster for?</Label>
          <Textarea
            id="poster-prompt"
            value={prompt}
            onChange={(e) => { setPrompt(e.target.value); setError(null); }}
            rows={4}
            maxLength={MAX_POSTER_PROMPT_CHARS}
            placeholder="e.g. walk-in flu clinic, bold — no appointment needed, open until 7pm on weekdays"
            disabled={busy || exhausted}
          />
          <p className="text-xs text-muted-foreground">
            Say who it is for and what you want them to do. You can name a
            layout too: circle portrait, diagonal, wave or checklist. Add
            &ldquo;bold&rdquo; for larger type.
          </p>
        </div>

        <PhotoPicker
          value={photo}
          onChange={(next) => { setPhoto(next); setError(null); }}
          options={options}
          search={api.searchPosterStockPhotos}
          upload={api.uploadPosterPhoto}
          onUploadingChange={setUploading}
          disabled={busy || exhausted}
        />

        {/* The allowance, stated plainly whichever way it falls. */}
        <div
          role="status"
          className={[
            "rounded-md border px-3 py-2 text-xs",
            exhausted
              ? "border-amber-500/30 bg-amber-500/5 text-amber-700 dark:text-amber-400"
              : "bg-muted/40 text-muted-foreground",
          ].join(" ")}
        >
          {subscription === null
            ? "Checking your plan…"
            : unlimited
              ? `Unlimited AI designs on ${subscription.plan.displayName}.`
              : exhausted
                ? `You've used all ${cap} AI designs on ${subscription.plan.displayName} this month. Your allowance resets on the 1st, or upgrade for more.`
                : `${remaining} of ${cap} AI designs left this month on ${subscription.plan.displayName}.`}
        </div>

        {done && (
          <div role="status" className="space-y-2 rounded-md border border-amber-500/30 bg-amber-500/5 p-3 text-sm">
            <p className="font-medium">Your poster is ready.</p>
            {done.notes.map((n) => (
              <p key={n} className="text-xs text-muted-foreground">{n}</p>
            ))}
            <Button
              type="button"
              size="sm"
              onClick={() => {
                onOpenChange(false);
                router.push(`/posters/${done.id}`);
              }}
            >
              Open the poster
            </Button>
          </div>
        )}

        {error && (
          <p className="text-sm text-destructive" role="alert">
            {error}
          </p>
        )}

        <DialogFooter>
          <Button
            type="button"
            variant="ghost"
            onClick={() => onOpenChange(false)}
            disabled={busy}
          >
            Cancel
          </Button>
          <Button
            type="button"
            onClick={generate}
            disabled={
              busy || exhausted || typed.length < MIN_POSTER_PROMPT_CHARS || photoBlocked !== null ||
              done !== null
            }
            className="bg-brand-gradient text-primary-foreground hover:opacity-90"
          >
            {busy ? (
              <>
                <Loader2 className="mr-1.5 h-4 w-4 animate-spin" />
                Designing…
              </>
            ) : (
              "Design it"
            )}
          </Button>
        </DialogFooter>

        {!busy && photoBlocked && typed.length >= MIN_POSTER_PROMPT_CHARS && (
          <p className="text-center text-xs text-muted-foreground">{photoBlocked}</p>
        )}

        {busy && (
          <p className="text-center text-xs text-muted-foreground">
            This takes up to a minute. Leaving this open keeps your place.
          </p>
        )}
      </DialogContent>
    </Dialog>
  );
}
