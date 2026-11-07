"use client";

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import Link from "next/link";
import {
  AlertCircle,
  Download,
  Film,
  Image as ImageIcon,
  Loader2,
  Plus,
  RefreshCw,
  Repeat,
  Share2,
  Sparkles,
  Trash2,
  Upload,
  X,
  Wand2,
} from "lucide-react";
import type {
  CurrentSubscription,
  Video,
  VideoScriptSource,
  NarrationOptions,
  VideoImage,
} from "@acme/shared-types";
import {
  DEFAULT_VIDEO_IMAGE_FOCUS,
  VIDEO_IMAGE_ZOOM_DEFAULT,
  isUnlimited,
  MAX_VIDEO_IMAGES,
  whyVideoImageRefused,
} from "@acme/shared-types";
import { PublishDialog } from "@/components/social/PublishDialog";
import { RecurringDialog } from "@/components/social/RecurringDialog";
import { finishedChanges, lastChangeNotMade, toImagePayload } from "@/lib/video-refine";
import { FramingHint, PhotoFramer } from "@/components/video/PhotoFramer";
import { RefineDialog } from "@/components/video/RefineDialog";
import { ActionMenu } from "@/components/ui/action-menu";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  DropdownMenuItem,
  DropdownMenuSeparator,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api";
import { NoResultsInRange, SortDateControls } from "@/components/lists/SortDateControls";
import { useListWindow } from "@/hooks/useListWindow";
import { hasDateRange, listWindowKey, listWindowParams } from "@/lib/list-window";
import { cn } from "@/lib/utils";

export default function VideosPage() {
  const toast = useToast();
  const [videos, setVideos] = useState<Video[]>([]);
  const [subscription, setSubscription] = useState<CurrentSubscription | null>(null);
  const [loading, setLoading] = useState(true);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);

  /** The video whose share dialog is open, or null. */
  const [sharing, setSharing] = useState<Video | null>(null);
  /** The video being made into a recurring post, or null. */
  const [recurring, setRecurring] = useState<Video | null>(null);

  /** The video whose refine dialog is open, or null. */
  const [refining, setRefining] = useState<Video | null>(null);

  /**
   * Each video's status at the last refresh, to notice one *finishing*.
   *
   * A refine that the worker refuses puts the video straight back to Ready
   * with the reason in `note`. Without saying so, the pharmacist watched it
   * load, saw the same video, and concluded the change had silently failed —
   * which it had, with a perfectly good sentence explaining why that nobody
   * showed them.
   */
  const lastStatus = useRef(new Map<string, Video["status"]>());

  const { listWindow, setListWindow, clearDates, ready } = useListWindow();
  const windowKey = listWindowKey(listWindow);
  /** Latest request wins: a slow answer for the old range must not replace the new one. */
  const requestSeq = useRef(0);

  const refresh = useCallback(async () => {
    const mine = ++requestSeq.current;
    const [v, s] = await Promise.all([
      api.listVideos(listWindowParams(listWindow)).catch(() => [] as Video[]),
      api.getCurrentSubscription().catch(() => null),
    ]);
    if (mine !== requestSeq.current) return;
    for (const outcome of finishedChanges(lastStatus.current, v)) {
      if (outcome.kind === "not-made") toast.info(outcome.title, outcome.detail);
      else toast.success(outcome.title, outcome.detail);
    }
    lastStatus.current = new Map(v.map((video) => [video.id, video.status]));
    setVideos(v);
    setSubscription(s);
    // listWindow is covered by windowKey.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [toast, windowKey]);

  useEffect(() => {
    // Wait for the URL's sort and dates, so a shared link fetches once.
    if (!ready) return;
    (async () => {
      try { await refresh(); }
      finally { setLoading(false); }
    })();
  }, [refresh, ready]);

  const anyPending = useMemo(
    () => videos.some(v => v.status === "Pending" || v.status === "Rendering"),
    [videos],
  );
  useEffect(() => {
    if (!anyPending) return;
    const handle = setInterval(() => { void refresh(); }, 2000);
    return () => clearInterval(handle);
  }, [anyPending, refresh]);

  const plan = subscription?.plan;
  const used = subscription?.usage.videosThisMonth ?? 0;
  const cap = plan?.maxVideosPerMonth ?? 0;
  const withinQuota = plan ? cap === -1 || used < cap : false;
  const aiImageryEnabled = plan?.aiVideoEnabled ?? false;
  const canCreate = withinQuota;
  const remaining = cap === -1 ? Infinity : Math.max(0, cap - used);

  const disabledReason = !plan
    ? undefined
    : cap === 0
      ? "Videos require Starter or Pro"
      : !withinQuota
        ? "Monthly limit reached"
        : undefined;

  async function remove(id: string) {
    if (!confirm("Delete this video?")) return;
    setBusyId(id);
    try {
      await api.deleteVideo(id);
      toast.success("Video deleted");
      await refresh();
    } catch (err) {
      toast.error("Delete failed", err instanceof Error ? err.message : undefined);
    } finally {
      setBusyId(null);
    }
  }

  async function retry(id: string) {
    setBusyId(id);
    try {
      await api.retryVideo(id);
      toast.success("Retrying", "We'll try that video again.");
      await refresh();
    } catch (err) {
      toast.error("Retry failed", err instanceof Error ? err.message : undefined);
    } finally {
      setBusyId(null);
    }
  }

  return (
    <main className="container max-w-5xl py-10">
      <header className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Marketing videos</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Type a topic — our agent researches, writes, narrates, and edits a 15-second
            9:16 marketing video ready for Instagram Reels, TikTok and YouTube Shorts.
          </p>
        </div>
        <div className="flex flex-col items-end gap-1">
          <Button
            onClick={() => setDialogOpen(true)}
            disabled={!canCreate}
            className="bg-brand-gradient text-primary-foreground shadow-md hover:opacity-90"
            title={disabledReason}
          >
            <Plus className="mr-1.5 h-4 w-4" />
            Create video
          </Button>
          {disabledReason && (
            <span className="text-xs text-muted-foreground">{disabledReason}</span>
          )}
        </div>
      </header>

      {plan && (
        <Card className="mt-6">
          <CardContent className="flex flex-col gap-3 pt-6 sm:flex-row sm:items-center sm:justify-between">
            <div>
              <p className="text-xs font-semibold uppercase tracking-wider text-muted-foreground">
                Videos this month
              </p>
              <p className="mt-1 text-2xl font-bold tracking-tight">
                {used}
                <span className="text-base font-normal text-muted-foreground">
                  {" / "}{isUnlimited(cap) ? "Unlimited" : cap}
                </span>
              </p>
            </div>
            {!canCreate && cap === 0 && (
              <Badge variant="outline" className="self-start">Videos require Starter or Pro</Badge>
            )}
            {!canCreate && cap > 0 && (
              <Badge variant="outline" className="self-start">Monthly limit reached</Badge>
            )}
            {aiImageryEnabled && (
              <Badge className="gap-1 bg-primary/15 text-primary hover:bg-primary/15 self-start">
                <Sparkles className="h-3 w-3" />
                AI-generated visuals unlocked
              </Badge>
            )}
            <Button asChild variant="outline" size="sm">
              <Link href="/pricing">Upgrade</Link>
            </Button>
          </CardContent>
        </Card>
      )}

      <div className="mt-6 flex flex-col gap-4 md:flex-row md:items-center">
        <SortDateControls value={listWindow} onChange={setListWindow} dateField="Created" />
      </div>

      <section className="mt-6">
        {loading ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : videos.length === 0 && hasDateRange(listWindow) ? (
          <NoResultsInRange onClear={clearDates} />
        ) : videos.length === 0 ? (
          <Card>
            <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
              <Film className="h-8 w-8 opacity-40" />
              <h3 className="text-base font-semibold">No videos yet</h3>
              <p className="max-w-sm text-sm text-muted-foreground">
                Type a topic (e.g. "why the flu jab this year") and our agent will research,
                script, narrate and edit a 15-second marketing video.
              </p>
              <Button
                onClick={() => setDialogOpen(true)}
                disabled={!canCreate}
                className="mt-2"
                title={disabledReason}
              >
                Create your first video
              </Button>
              {disabledReason && (
                <span className="text-xs text-muted-foreground">{disabledReason}</span>
              )}
            </CardContent>
          </Card>
        ) : (
          <ul className="grid grid-cols-2 gap-3 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
            {videos.map(v => (
              <li key={v.id}>
                <VideoCard
                  video={v}
                  onDelete={() => remove(v.id)}
                  onRetry={() => retry(v.id)}
                  onShare={() => setSharing(v)}
                  onMakeRecurring={() => setRecurring(v)}
                  onRefine={() => setRefining(v)}
                  busy={busyId === v.id}
                />
              </li>
            ))}
          </ul>
        )}
      </section>

      <PublishDialog
        open={sharing !== null}
        onClose={() => setSharing(null)}
        sourceKind="Video"
        sourceId={sharing?.id ?? ""}
        sourceName={sharing?.name ?? ""}
        isVideo
      />
      <RecurringDialog
        open={recurring !== null}
        onClose={() => setRecurring(null)}
        source={recurring ? { kind: "Video", id: recurring.id, name: recurring.name, imageUrl: recurring.thumbnailUrl } : null}
        onSaved={() => { window.location.href = "/social?tab=recurring"; }}
      />

      {/* Refreshes on success, which is what starts the polling: a confirmed
          change puts the video back to Pending, and `anyPending` above is what
          turns the two-second poll back on. Without the refresh the card would
          sit on "Ready" until something else happened to reload the list. */}
      <RefineDialog
        video={refining}
        onClose={() => setRefining(null)}
        onQueued={async () => {
          toast.success("Working on it", "We're re-cutting your video now.");
          await refresh();
        }}
      />

      <CreateVideoDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        remaining={remaining}
        aiImageryEnabled={aiImageryEnabled}
        onQueued={async () => {
          await refresh();
        }}
      />
    </main>
  );
}

function VideoCard({
  video,
  onDelete,
  onRetry,
  onShare,
  onMakeRecurring,
  onRefine,
  busy,
}: {
  video: Video;
  onDelete: () => void;
  onRetry: () => void;
  onShare: () => void;
  onMakeRecurring: () => void;
  onRefine: () => void;
  busy: boolean;
}) {
  const isReady = video.status === "Ready" && video.downloadUrl;
  const isFailed = video.status === "Failed";
  const isPending = video.status === "Pending" || video.status === "Rendering";

  // Not `isFailed`. A render whose worker died stays on "Rendering" forever and
  // never reaches Failed — reaching it takes the worker that died writing it —
  // so gating the button on Failed left the one case that needed recovery as
  // the one case with no way to ask for it. The server decides, because the
  // test is a lapsed lease and that is not something out here can see.
  const canRetry = video.canRetry;

  // Said plainly, because "Rendering" with a Retry button underneath is a
  // confusing thing to hand somebody with no explanation.
  const isStuck = canRetry && !isFailed;

  // Status alone, deliberately not `isReady` — that one also requires a
  // download URL, which is what Share and the <video> element need. Asking for
  // a change needs neither: the server's own guard is `Status == Ready`, and
  // matching it here keeps the greyed-out reason honest.
  const canRefine = video.status === "Ready";

  const pctMatch = video.note?.match(/(\d{1,3})\s*%/);
  const percent = pctMatch ? Math.min(100, Math.max(0, parseInt(pctMatch[1], 10))) : null;

  return (
    <Card className={cn("overflow-hidden", isFailed && "border-destructive/40")}>
      {/* Preview area — 3:4 keeps the portrait feel without the 9:16 tower. */}
      <div className="relative aspect-[3/4] w-full overflow-hidden bg-muted">
        {isReady ? (
          <>
            {video.thumbnailUrl && (
              /* eslint-disable-next-line @next/next/no-img-element */
              <img
                src={video.thumbnailUrl}
                alt={video.name}
                className="h-full w-full object-cover"
              />
            )}
            {video.downloadUrl && (
              <video
                controls
                className="absolute inset-0 h-full w-full object-cover"
                src={video.downloadUrl}
                poster={video.thumbnailUrl ?? undefined}
              />
            )}
          </>
        ) : isPending ? (
          <div className="flex h-full w-full flex-col items-center justify-center gap-2 p-3 text-muted-foreground">
            <Loader2 className="h-5 w-5 animate-spin" />
            <span className="text-[10px] leading-tight">
              {video.status === "Pending"
                ? "Queued"
                : percent !== null ? `Rendering… ${percent}%` : (video.note ?? "Rendering")}
            </span>
            {video.status === "Rendering" && (
              <div className="w-4/5 max-w-[120px]">
                <div className="h-1 w-full overflow-hidden rounded-full bg-muted-foreground/20">
                  <div
                    className="h-full bg-primary transition-all duration-500"
                    style={{ width: `${percent ?? 3}%` }}
                  />
                </div>
              </div>
            )}
          </div>
        ) : (
          <div className="flex h-full flex-col items-center justify-center gap-1.5 p-3 text-center text-destructive">
            <AlertCircle className="h-5 w-5" />
            <span className="line-clamp-3 text-[10px] leading-tight">
              {video.note ?? "Failed"}
            </span>
          </div>
        )}
      </div>
      <CardHeader className="p-3 pb-1.5">
        <CardTitle className="line-clamp-1 text-xs">{video.name}</CardTitle>
        {/* On a Ready video, `note` is only ever the reason the last requested
            change was not made — a successful render clears it. */}
        {lastChangeNotMade(video) && (
          <p
            className="line-clamp-4 rounded bg-amber-500/10 px-1.5 py-1 text-[10px] leading-tight text-amber-700 dark:text-amber-400"
            title={video.note ?? undefined}
          >
            {lastChangeNotMade(video)}
          </p>
        )}
        <CardDescription className="flex items-center gap-1 text-[10px]">
          {video.visualStyle === "AiImagery" ? (
            <><Sparkles className="h-2.5 w-2.5" /> AI</>
          ) : (
            <><ImageIcon className="h-2.5 w-2.5" /> Stock</>
          )}
          <span>·</span>
          <span>{video.durationSeconds}s</span>
        </CardDescription>
      </CardHeader>
      {/*
        One visible action, everything else behind a menu.

        Five columns at desktop width leaves roughly 170px per card. Download +
        Share + Delete already had to wrap to fit, and the note that used to sit
        here said wrapping was right "rather than a menu: there are only three
        actions". Asking for a change is the fourth, so by that same reasoning
        it is a menu now. Four controls at `text-[10px]` in a card that clips
        its contents is how the poster cards lost their Delete button off the
        right-hand edge — present in the DOM, unreachable on screen.

        The menu also lets a disabled action explain itself. Share simply
        vanished on an unfinished video before; now it is there, greyed, with
        the reason underneath.
      */}
      <CardContent className="flex items-center justify-between gap-1 p-3 pt-0">
        {isReady && video.downloadUrl ? (
          <Button asChild variant="outline" size="sm" className="h-7 px-2 text-[10px]">
            <a href={video.downloadUrl} download={`${video.name}.mp4`}>
              <Download className="mr-1 h-3 w-3" />
              Download
            </a>
          </Button>
        ) : canRetry ? (
          <Button
            variant="outline"
            size="sm"
            className="h-7 px-2 text-[10px]"
            onClick={onRetry}
            disabled={busy}
            title={
              isStuck
                ? "This render stopped without finishing — the server it was running on went away. Trying again does not use another video from your allowance."
                : undefined
            }
          >
            <RefreshCw className={cn("mr-1 h-3 w-3", busy && "animate-spin")} />
            {isStuck ? "Stalled — try again" : "Retry"}
          </Button>
        ) : <span />}

        <ActionMenu label={`More actions for ${video.name}`} className="h-7 w-7">
          {/*
            Enabled only on a finished video, and the dialog says the same thing
            again if one slips through — a render can finish between this list
            being drawn and the item being clicked, and it can also fail.
          */}
          <DropdownMenuItem disabled={!canRefine || busy} onSelect={onRefine}>
            <Wand2 className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            <span className="min-w-0 flex-1">
              <span className="block">Ask for a change</span>
              <span className="block text-[11px] text-muted-foreground">
                {canRefine
                  ? "Re-cut the captions, timing or closing card"
                  : isPending
                    ? "Wait for it to finish first"
                    : "This one did not finish — retry it first"}
              </span>
            </span>
          </DropdownMenuItem>

          {/* Only for Ready videos — a still-rendering one has no blob to
              fetch, and the platforms would reject the URL. */}
          <DropdownMenuItem disabled={!isReady || busy} onSelect={onShare}>
            <Share2 className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            <span className="min-w-0 flex-1">
              <span className="block">Share to social</span>
              {!isReady && (
                <span className="block text-[11px] text-muted-foreground">
                  There is nothing to post yet
                </span>
              )}
            </span>
          </DropdownMenuItem>

          <DropdownMenuItem disabled={!isReady || busy} onSelect={onMakeRecurring}>
            <Repeat className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden />
            <span className="min-w-0 flex-1">
              <span className="block">Make recurring</span>
              {!isReady && (
                <span className="block text-[11px] text-muted-foreground">
                  There is nothing to post yet
                </span>
              )}
            </span>
          </DropdownMenuItem>

          {/* Separated, and last. */}
          <DropdownMenuSeparator />
          <DropdownMenuItem
            className="items-center text-destructive focus:bg-destructive/10 focus:text-destructive"
            disabled={busy}
            onSelect={onDelete}
          >
            <Trash2 className="h-4 w-4 shrink-0" aria-hidden />
            Delete video
          </DropdownMenuItem>
        </ActionMenu>
      </CardContent>
    </Card>
  );
}

type ScriptTab = "ai" | "guided" | "custom";

/**
 * A picture the pharmacist attached, as the dialog holds it.
 *
 * `preview` is a local object URL rather than a server address. The file is
 * already in the browser, so showing it needs no read endpoint and no signed
 * URL — and the dialog stays the only new surface this feature adds.
 */
interface AttachedImage extends VideoImage {
  preview: string;
  label: string;
  // Required here, optional on the wire. The framer needs a number to position
  // the preview, and "unset" would have to become 0.5 somewhere anyway — so it
  // becomes 0.5 once, on the way in, rather than at every use.
  focalX: number;
  focalY: number;
  zoom: number;
}

// The voice catalogue is NOT here any more.
//
// It was two arrays — Edge voices and ElevenLabs voices — and the estimate
// below worked out from a `11labs:` prefix whether a render would cost
// anything. Both were correct only while the voice id chose the synthesiser.
// The API key chooses it now, so on every hosted deployment this dialog offered
// Edge voices that the render silently swapped for ElevenLabs, and priced those
// renders at about a tenth of what they cost.
//
// ai-service owns the answer because it owns the key. See `GET /api/videos/narration`.

function CreateVideoDialog({
  open,
  onOpenChange,
  remaining,
  aiImageryEnabled,
  onQueued,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  remaining: number;
  aiImageryEnabled: boolean;
  onQueued: () => Promise<void>;
}) {
  const toast = useToast();
  const [tab, setTab] = useState<ScriptTab>("ai");
  const [topic, setTopic] = useState("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [customScript, setCustomScript] = useState("");
  const [styleTone, setStyleTone] = useState("friendly, informative");
  const [voiceId, setVoiceId] = useState<string>("");
  // null = not asked yet or ai-service unreachable. Either way the dialog
  // offers no voice choice rather than a guessed one.
  const [narration, setNarration] = useState<NarrationOptions | null>(null);
  // AI imagery is Pro-only. Realistic (Pexels) is free-cost from our side.
  const [useAiImagery, setUseAiImagery] = useState(false);

  // The pharmacy's own pictures. Stored as they are chosen — `preview` is a
  // local object URL, so no read endpoint is needed to show a thumbnail.
  const [images, setImages] = useState<AttachedImage[]>([]);
  const [imageError, setImageError] = useState<string | null>(null);
  const [attaching, setAttaching] = useState(false);
  const imageInput = useRef<HTMLInputElement | null>(null);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setTab("ai");
      setTopic("");
      setName("");
      setDescription("");
      setCustomScript("");
      setStyleTone("friendly, informative");
      setVoiceId("");
      setUseAiImagery(false);
      // Revoked, not just dropped: an object URL holds the file in memory until
      // it is released, and this dialog can be opened many times in a session.
      setImages((previous) => {
        previous.forEach((i) => URL.revokeObjectURL(i.preview));
        return [];
      });
      setImageError(null);
      setError(null);

      // Asked each time the dialog opens. The API caches it for five minutes,
      // so this is cheap, and a key set on the server appears without a reload.
      let cancelled = false;
      void api
        .getNarrationOptions()
        .then((options) => {
          if (cancelled || !options) return;
          setNarration(options);
          setVoiceId(options.defaultVoiceId);
        })
        .catch(() => {
          // Deliberately silent. Narration is not something the pharmacist
          // asked about, and a red banner about a voice list would read as "the
          // video feature is broken" when it is not: the server picks a voice.
          setNarration(null);
        });
      return () => {
        cancelled = true;
      };
    }
  }, [open]);

  // Cost estimate — surface Pro-tier AI imagery + ElevenLabs so the user
  // knows what they're spending before they queue. Defaults to $0.00 for
  // the free path (Edge-TTS + Pexels).
  const estimate = useMemo(() => {
    let usd = 0;
    // AI imagery: 8 segments × $0.003 = ~$0.024 per Pro video.
    if (useAiImagery) usd += 0.024;
    // Narration, priced by whoever will actually do it. Reading the voice id
    // here is what made this wrong: the id stopped deciding the provider.
    usd += narration?.estimateUsdPerVideo ?? 0;
    // Anthropic Haiku 4.5 script + Tavily search — negligible at ~$0.005.
    usd += 0.005;
    return usd;
  }, [useAiImagery, narration]);

  /**
   * Stores a chosen file and keeps a local preview.
   *
   * Uploaded immediately rather than at submit: a picture that is too big or
   * the wrong format should be refused while the pharmacist is still looking at
   * the file they picked, not after they have written a brief and pressed
   * Create.
   */
  async function attachFiles(files: FileList | null) {
    if (!files?.length) return;
    setImageError(null);

    const room = MAX_VIDEO_IMAGES - images.length;
    if (room <= 0) {
      setImageError(`You can attach up to ${MAX_VIDEO_IMAGES} images.`);
      return;
    }

    const chosen = Array.from(files).slice(0, room);
    if (chosen.length < files.length) {
      setImageError(
        `Only the first ${chosen.length} were added — ${MAX_VIDEO_IMAGES} is the limit.`,
      );
    }

    setAttaching(true);
    try {
      for (const file of chosen) {
        // Refused here rather than by the server. The size limit is enforced by
        // the framework before any of our code runs, so a too-big file comes
        // back as a bare 413 with no body — after the whole thing has uploaded.
        const refusal = whyVideoImageRefused(file);
        if (refusal) {
          setImageError(refusal);
          continue;
        }

        try {
          const stored = await api.uploadVideoImage(file);
          setImages((current) => [
            ...current,
            {
              ...stored,
              preview: URL.createObjectURL(file),
              label: file.name,
              // Centre until they move it — the crop every picture got before
              // this control existed.
              focalX: DEFAULT_VIDEO_IMAGE_FOCUS,
              focalY: DEFAULT_VIDEO_IMAGE_FOCUS,
              zoom: VIDEO_IMAGE_ZOOM_DEFAULT,
            },
          ]);
        } catch (err) {
          // Per file, so one rejected picture does not discard the others that
          // uploaded cleanly.
          setImageError(
            err instanceof Error ? err.message : `${file.name} could not be attached.`,
          );
        }
      }
    } finally {
      setAttaching(false);
      // Cleared so choosing the same file again still fires a change event.
      if (imageInput.current) imageInput.current.value = "";
    }
  }

  function setFraming(blobKey: string, focalX: number, focalY: number, zoom: number) {
    setImages((current) =>
      current.map((i) => (i.blobKey === blobKey ? { ...i, focalX, focalY, zoom } : i)),
    );
  }

  function removeImage(blobKey: string) {
    setImages((current) => {
      const going = current.find((i) => i.blobKey === blobKey);
      if (going) URL.revokeObjectURL(going.preview);
      return current.filter((i) => i.blobKey !== blobKey);
    });
    setImageError(null);
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    if (!topic.trim()) {
      setError("Give the agent a topic to work with.");
      return;
    }
    if (tab === "custom" && !customScript.trim()) {
      setError("Paste your script or switch modes.");
      return;
    }
    if (tab === "guided" && !description.trim()) {
      setError("Add creative guidance or switch to AI mode.");
      return;
    }

    setPending(true);
    try {
      const scriptSource: VideoScriptSource =
        tab === "ai" ? "Ai" : tab === "guided" ? "Guided" : "Custom";
      await api.createVideo({
        topic: topic.trim(),
        name: name.trim() || undefined,
        scriptSource,
        visualStyle: useAiImagery ? "AiImagery" : "Realistic",
        description: tab === "guided" ? description.trim() : undefined,
        customScript: tab === "custom" ? customScript.trim() : undefined,
        styleTone: styleTone.trim() || undefined,
        voiceId: voiceId || undefined,
        images: images.length
          ? // Every framing field, explicitly. This list once destructured the
            // focal point and then left it out of the object, so the framing
            // chosen here never reached the render — only refine sent it.
            toImagePayload(images)
          : undefined,
      });
      toast.success("Queued", "The agent is working on your video.");
      await onQueued();
      onOpenChange(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to queue");
    } finally {
      setPending(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Create video</DialogTitle>
          <DialogDescription>
            9:16 portrait, 15 seconds.
            {Number.isFinite(remaining) && ` ${remaining} left this month.`}
          </DialogDescription>
        </DialogHeader>

        <form onSubmit={submit} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="topic">Topic</Label>
            <Input
              id="topic"
              value={topic}
              onChange={(e) => setTopic(e.target.value)}
              maxLength={400}
              placeholder="e.g. Why book your flu jab this autumn"
            />
            <p className="text-xs text-muted-foreground">
              A short phrase — the researcher and scriptwriter build the rest.
            </p>
          </div>

          <Tabs value={tab} onValueChange={(v) => setTab(v as ScriptTab)}>
            <TabsList className="grid grid-cols-3">
              <TabsTrigger value="ai">AI script</TabsTrigger>
              <TabsTrigger value="guided">Guided</TabsTrigger>
              <TabsTrigger value="custom">My script</TabsTrigger>
            </TabsList>

            <TabsContent value="ai" className="mt-3 space-y-2">
              <p className="text-xs text-muted-foreground">
                The agent researches the topic and writes the whole script.
                Fastest path — leave everything else blank.
              </p>
            </TabsContent>

            <TabsContent value="guided" className="mt-3 space-y-2">
              <Label htmlFor="description">Creative guidance</Label>
              <Textarea
                id="description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                rows={3}
                maxLength={800}
                placeholder="Angle, audience, must-hit points, tone…"
              />
              <p className="text-xs text-muted-foreground">
                The writer treats this as instructions it must follow.
              </p>
            </TabsContent>

            <TabsContent value="custom" className="mt-3 space-y-2">
              <Label htmlFor="customScript">Your script</Label>
              <Textarea
                id="customScript"
                value={customScript}
                onChange={(e) => setCustomScript(e.target.value)}
                rows={6}
                maxLength={4000}
                placeholder="Paste the exact narration you want. We'll voice and edit it as-is."
              />
              <p className="text-xs text-muted-foreground">
                {customScript.length}/4000 — we won't rewrite this, only extract metadata.
              </p>
            </TabsContent>
          </Tabs>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="space-y-2">
              <Label htmlFor="styleTone">Tone</Label>
              <Input
                id="styleTone"
                value={styleTone}
                onChange={(e) => setStyleTone(e.target.value)}
                placeholder="friendly, informative"
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="voice">Voice</Label>
              {narration ? (
                <select
                  id="voice"
                  value={voiceId}
                  onChange={(e) => setVoiceId(e.target.value)}
                  className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
                >
                  {/* One list, not two. Every option here is one this
                      deployment will actually use — offering a voice that gets
                      swapped is the product ignoring a choice it invited. */}
                  {narration.voices.map((v) => (
                    <option key={v.id} value={v.id}>{v.label}</option>
                  ))}
                </select>
              ) : (
                <p className="flex h-10 items-center text-xs text-muted-foreground">
                  Using this pharmacy&apos;s default narration voice.
                </p>
              )}
            </div>
          </div>

          {/* The pharmacy's own pictures. Free, and deliberately placed above
              the AI-imagery toggle: "use your own photos" is the cheaper and
              usually better answer, and a pharmacist should read it first. */}
          <div className="space-y-2 rounded-md border border-border bg-muted/20 p-3">
            <div className="flex items-center justify-between">
              <Label className="flex items-center gap-1.5 text-sm font-medium">
                <ImageIcon className="h-3.5 w-3.5" />
                Your photos
                <span className="text-xs font-normal text-muted-foreground">
                  (optional, up to {MAX_VIDEO_IMAGES})
                </span>
              </Label>
              <Button
                type="button"
                variant="outline"
                size="sm"
                disabled={attaching || images.length >= MAX_VIDEO_IMAGES}
                onClick={() => imageInput.current?.click()}
              >
                {attaching ? (
                  <Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />
                ) : (
                  <Upload className="mr-1.5 h-3.5 w-3.5" />
                )}
                {attaching ? "Adding…" : "Add photos"}
              </Button>
            </div>

            <input
              ref={imageInput}
              type="file"
              accept="image/jpeg,image/png,image/webp"
              multiple
              className="hidden"
              onChange={(e) => void attachFiles(e.target.files)}
            />

            {images.length > 0 && (
              // Shown in the shape they will appear in, not as square
              // thumbnails. A square tile of a landscape photo hides the very
              // thing being decided here — that 58% of its width is about to be
              // cut off — so the pharmacist would approve a picture and get a
              // different one.
              <div className="space-y-1.5">
                <FramingHint />
                <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
                  {images.map((image) => (
                    <div key={image.blobKey} className="group relative">
                      <PhotoFramer
                        src={image.preview}
                        alt={image.label}
                        focalX={image.focalX}
                        focalY={image.focalY}
                        zoom={image.zoom}
                        onChange={(focalX, focalY, zoom) =>
                          setFraming(image.blobKey, focalX, focalY, zoom)
                        }
                      />
                      <button
                        type="button"
                        onClick={() => removeImage(image.blobKey)}
                        aria-label={`Remove ${image.label}`}
                        className="absolute right-0.5 top-0.5 rounded-full bg-background/90 p-0.5 opacity-0 shadow transition-opacity group-hover:opacity-100 focus:opacity-100"
                      >
                        <X className="h-3 w-3" />
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            )}

            {imageError && (
              <p className="text-xs text-destructive">{imageError}</p>
            )}

            <p className="text-xs text-muted-foreground">
              {images.length > 0
                ? "Your photos + stock. No extra AI-image charge."
                : "Add your shopfront, team or a poster and we'll build the video around them. Your photos + stock. No extra AI-image charge."}
            </p>
          </div>

          {/* AI imagery toggle — Pro only. Realistic (Pexels stock) is the
              default and costs nothing on our side. */}
          <div className="flex items-start gap-3 rounded-md border border-border bg-muted/30 p-3">
            <input
              id="aiImagery"
              type="checkbox"
              checked={useAiImagery}
              onChange={(e) => setUseAiImagery(e.target.checked)}
              disabled={!aiImageryEnabled}
              className="mt-1 h-4 w-4 rounded border-input"
            />
            <div className="flex-1 space-y-0.5">
              <Label
                htmlFor="aiImagery"
                className={cn(
                  "flex items-center gap-1 text-sm font-medium",
                  !aiImageryEnabled && "opacity-60",
                )}
              >
                <Sparkles className="h-3.5 w-3.5" />
                AI-generated cartoon visuals
                {!aiImageryEnabled && (
                  <span className="text-[10px] font-normal text-muted-foreground">
                    · Pro
                  </span>
                )}
              </Label>
              <p className="text-xs text-muted-foreground">
                Replace stock footage with one AI-illustrated image per script beat.
                Uses ~8 images, capped at $0.05 per video.
              </p>
            </div>
          </div>

          {/* Cost estimate — surface any paid backends before submission.

              Shown only when we know which narrator will run. Without that, the
              sum is missing its largest term and would read ~$0.005 for a
              render that costs ~$0.055 — the same too-cheap number this whole
              change exists to stop, just arrived at a different way. An honest
              dash beats a confident wrong figure. */}
          <div className="flex items-center justify-between rounded-md border border-border bg-muted/20 px-3 py-2 text-xs">
            <span className="text-muted-foreground">Estimated cost per render</span>
            {narration ? (
              <span className={cn("font-mono font-semibold",
                estimate > 0.03 ? "text-amber-600 dark:text-amber-400" : "text-emerald-600 dark:text-emerald-400"
              )}>
                ~${estimate.toFixed(3)} USD
              </span>
            ) : (
              <span className="font-mono text-muted-foreground" title="Narration settings unavailable">
                —
              </span>
            )}
          </div>

          <div className="space-y-2">
            <Label htmlFor="name">
              Name <span className="text-xs text-muted-foreground">(optional)</span>
            </Label>
            <Input
              id="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Flu jab reel — October"
            />
          </div>

          {error && (
            <p className="text-sm text-destructive" role="alert">{error}</p>
          )}

          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button
              type="submit"
              disabled={pending}
              className="bg-brand-gradient text-primary-foreground hover:opacity-90"
            >
              {pending ? "Queuing…" : "Generate"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
