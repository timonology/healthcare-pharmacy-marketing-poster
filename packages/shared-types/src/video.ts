export type VideoStatus = "Pending" | "Rendering" | "Ready" | "Failed";

export type VideoScriptSource = "Ai" | "Guided" | "Custom";
export type VideoVisualStyle = "Realistic" | "AiImagery";

export interface Video {
  id: string;
  ownerId: string;
  name: string;
  topic: string;
  status: VideoStatus;
  scriptSource: VideoScriptSource;
  visualStyle: VideoVisualStyle;
  aspect: string;
  durationSeconds: number;
  downloadUrl: string | null;
  thumbnailUrl: string | null;
  note: string | null;

  /**
   * The technical reason the last render failed — for operators, not for
   * pharmacists.
   *
   * `note` is the sentence the pharmacy reads; this is the exception type and
   * message behind it, and the UI must never render it. It exists so that
   * `pnpm smoke` can report *why* a production render failed without anyone
   * opening a log stream.
   */
  diagnostic: string | null;

  createdAtUtc: string;
  updatedAtUtc: string;

  /**
   * Whether pressing Retry would be accepted right now.
   *
   * Not derivable from `status`, which is why the server sends it. A render
   * that is still running and one whose worker died are both `Rendering` from
   * out here — the difference is a lapsed lease, which is a server fact and not
   * something the browser should be comparing clocks over.
   *
   * The UI used to gate Retry on `status === "Failed"`, and a stranded render
   * never reaches Failed: reaching it takes the worker that died writing it. So
   * the one case that most needed the button was the one case without it.
   */
  canRetry: boolean;

  /**
   * How many of the pharmacy's own pictures this video carries.
   *
   * A count rather than the pictures themselves: the only question the UI asks
   * is how much room is left before {@link MAX_VIDEO_IMAGES}, and answering it
   * with blob keys would put storage paths on the wire for nothing.
   *
   * Optional because every video row written before attachments existed has no
   * such field, and an old video simply has none.
   */
  imageCount?: number;
}

/**
 * The longest change request we will act on, in characters.
 *
 * <b>A third copy, on purpose, and pinned by a test.</b> The other two are
 * `RefinePlan.MaxInstructionLength` in the domain and `MAX_INSTRUCTION_CHARS`
 * in `app/video_agent/stages.py`. The browser needs a literal — a textarea has
 * to stop somewhere, and letting somebody write fifteen hundred characters and
 * then refusing the result is worse than stopping them at the limit.
 *
 * These three were 1000, 1000 and 2000 once, and the effect of the odd one out
 * was that a long instruction came back as an unexplained shrug rather than
 * "one change at a time". `RefineStageContractTests` now reads this file, so
 * the copies cannot drift apart quietly again.
 */
export const MAX_REFINE_INSTRUCTION_CHARS = 1000;

/** A change a pharmacist has asked for, in their own words. */
export interface RefineRequest {
  instruction: string;
}

/**
 * What a requested change would do, shown before anything runs.
 *
 * The preview and the confirmation are two calls on purpose. Routing a change
 * request means interpreting a sentence, and getting that wrong is expensive in
 * one direction and invisible in the other: a caption fix sent to the
 * scriptwriter rewrites the script and bills three providers, while "make it
 * friendlier" sent to the editor re-cuts identical footage and reports success.
 * Showing the plan first is what stops either happening quietly.
 */
export interface RefinePlan {
  /**
   * Whether confirming would actually do anything.
   *
   * False covers three different situations — an instruction we could not
   * interpret, a change that needs a part of the service not yet open, and a
   * video that cannot be changed right now — and `reason` is what tells them
   * apart. Show the reason; never just grey the button.
   */
  canRun: boolean;

  /** The pipeline stage, for support and tests. Not for the pharmacist. */
  stage: string | null;

  /** What will be redone, in plain English. Empty when `canRun` is false. */
  summary: string;

  /** One sentence for the pharmacist. Always populated. */
  reason: string;

  /** True when running this costs nothing. */
  free: boolean;
}

/** Where a pharmacist's own picture came from. */
export type VideoImageSource = "upload" | "logo" | "poster";

/**
 * A picture the pharmacy supplied, to be used as B-roll in its own video.
 *
 * Stored on blob before the video is created, so the dialog can show a
 * thumbnail and the pharmacist can remove one before spending a render.
 *
 * **These are never sent to an image model.** They are the pharmacy's own
 * photographs, pinned to specific beats of the script; the AI-imagery toggle is
 * a different feature with its own cost, and mixing them would charge somebody
 * for pictures they supplied themselves.
 */
export interface VideoImage {
  /** Blob key, not a URL — the same reasoning as a canvas image shape. */
  blobKey: string;
  source: VideoImageSource;
  /** What it shows, used to caption nothing and to help nobody guess. Optional. */
  description?: string;

  /**
   * Which part of the picture survives the crop to 9:16, as a point in 0–1.
   *
   * <b>A point, not a rectangle, and that is the whole of it.</b> The output is
   * always 9:16 and the picture is always scaled to cover it, so the crop
   * window's *size* is fixed by arithmetic — only where it sits is a choice.
   * Storing a rectangle would mean storing three numbers that cannot disagree
   * with the fourth, and inventing a rule for what to do when they did.
   *
   * 0.5/0.5 is the centre, which is what every picture got before this existed
   * and is why it is the default: an old row and a new one mean the same thing.
   *
   * These are exactly CSS `object-position` percentages against
   * `object-fit: cover`, which is not a coincidence — the browser preview and
   * ffmpeg's `crop=(in_w-out_w)*focalX` compute the same offset, so what the
   * pharmacist framed is what the render produces rather than something close
   * to it.
   */
  focalX?: number;
  focalY?: number;

  /**
   * How much of the picture fits the frame, `VIDEO_IMAGE_ZOOM_MIN`–`MAX`.
   *
   * Absent means 1.0: the cover crop above, unchanged. Below 1.0 the photo is
   * scaled down so more of it shows and the gaps are a blurred copy of it;
   * above 1.0 it punches in. `framingGeometry` in `./framing` is the one
   * place that turns these three numbers into a position — the preview uses
   * it and the render does the same sums, pinned by a shared fixture.
   */
  zoom?: number;
}

/** Dead centre — the crop every picture got before it could be chosen. */
export const DEFAULT_VIDEO_IMAGE_FOCUS = 0.5;

/** The most a pharmacist may attach to one video. */
export const MAX_VIDEO_IMAGES = 4;

/**
 * The most one attached picture may weigh, in bytes.
 *
 * <b>A copy of `VideoImagePolicy.MaxBytes`, and it has to be.</b> The server
 * limit is the real one — it is enforced by `[RequestSizeLimit]` and cannot be
 * talked out of. But that enforcement happens in the framework, before any of
 * our code runs, so the response is a bare 413 with no body and nothing to
 * explain itself with. A 20MB photograph therefore uploaded for as long as it
 * took, failed, and said only that it had failed.
 *
 * Knowing the number here lets the browser refuse it instantly and say why.
 * `VideoImageTests` reads this file to keep the two in step.
 */
export const MAX_VIDEO_IMAGE_BYTES = 8 * 1024 * 1024;

/** What ffmpeg will reliably turn into a beat. Mirrors `AllowedContentTypes`. */
export const VIDEO_IMAGE_TYPES = ["image/jpeg", "image/png", "image/webp"] as const;

/**
 * Null when this file may be attached, or a sentence saying why it may not.
 *
 * Deliberately worded like the server's refusals rather than merely similar to
 * them: whichever side refuses first, the pharmacist should read the same kind
 * of sentence. The server still checks — this is a courtesy, not a guard, and
 * anything reaching the API is validated there regardless.
 */
export function whyVideoImageRefused(file: {
  name?: string;
  type?: string;
  size: number;
}): string | null {
  if (file.size <= 0) return "That file is empty.";

  if (file.size > MAX_VIDEO_IMAGE_BYTES) {
    const mb = Math.ceil((file.size / 1024 / 1024) * 10) / 10;
    const limit = MAX_VIDEO_IMAGE_BYTES / 1024 / 1024;
    const named = file.name ? `${file.name} is ${mb}MB` : `That one is ${mb}MB`;
    return `Images must be under ${limit}MB. ${named}.`;
  }

  // Lower-cased before comparing, matching the server's
  // `StringComparer.OrdinalIgnoreCase`. Browsers are not consistent about the
  // case of `File.type`, and a case-sensitive check here would refuse a
  // perfectly good JPEG that the API would have accepted — leaving the
  // pharmacist unable to attach a file with nothing wrong with it.
  const type = (file.type ?? "").trim().toLowerCase();
  if (!type || !(VIDEO_IMAGE_TYPES as readonly string[]).includes(type)) {
    return "Images must be JPEG, PNG or WebP.";
  }

  return null;
}

/**
 * Pictures to add to a video that has already been made.
 *
 * Separate from {@link RefineRequest}, which carries a sentence that has to
 * match a plan the pharmacist was shown and agreed to. Nothing is interpreted
 * here — a photograph says what it wants by being one — so there is no preview
 * step and no instruction to echo back.
 *
 * They are added to whatever the video already has, up to
 * {@link MAX_VIDEO_IMAGES} in total.
 */
export interface AddVideoPicturesRequest {
  images: VideoImage[];
}

export interface CreateVideoRequest {
  topic: string;
  name?: string;
  scriptSource?: VideoScriptSource;
  visualStyle?: VideoVisualStyle;
  description?: string;
  customScript?: string;
  voiceId?: string;
  styleTone?: string;

  /**
   * Up to {@link MAX_VIDEO_IMAGES} of the pharmacy's own pictures, pinned into
   * the visuals stage. Everything else in the video stays stock footage.
   */
  images?: VideoImage[];
}

/** One selectable narration voice. */
export interface VoiceOption {
  id: string;
  label: string;
}

/**
 * Which voices this deployment can narrate with, and what narration costs.
 *
 * **Why the server decides this.** The voice list used to be two hard-coded
 * arrays in the video dialog, and the browser worked out from a `11labs:`
 * prefix whether a render would cost anything. That was correct while the voice
 * id chose the provider. It stopped being correct when the *API key* chose it:
 * on any hosted deployment the dialog offered Edge voices that the render would
 * silently swap for ElevenLabs, and priced those renders at roughly a tenth of
 * their real cost.
 *
 * Only ai-service knows whether `ELEVENLABS_API_KEY` is set, so ai-service
 * answers the whole question and the browser renders the answer. Nothing on
 * this side inspects voice ids.
 */
export interface NarrationOptions {
  provider: "elevenlabs" | "edge";
  voices: VoiceOption[];
  defaultVoiceId: string;

  /** What narration adds to one video, in USD. Zero for Edge-TTS. */
  estimateUsdPerVideo: number;
}

/**
 * One tile in the refine dialog's shot picker — a moment of the finished video.
 *
 * The pharmacist taps the tile to change and says what it should show. The
 * shot is theirs to choose: `shows` is a stock-library description kept for
 * alt text only, and nothing uses it to pick a shot. (It is what a model used
 * to pick with, and it was wrong often enough to refuse real requests.)
 */
export interface VideoShot {
  /** The moment. What a tapped tile sends back. */
  index: number;
  startSeconds: number;
  endSeconds: number;
  /** What is said over this shot. */
  caption: string;
  /** The pharmacy's own photograph rather than stock footage. */
  isPhoto: boolean;
  /** A still exists at `/api/videos/{id}/shots/{index}/thumbnail`; otherwise show a numbered tile. */
  hasThumbnail: boolean;
  shows: string | null;
}

/** A tapped tile, and what it should show instead (empty: the video's own subject). */
export interface ShotChoice {
  index: number;
  show?: string;
}

export interface ChangeShotsRequest {
  shots: ShotChoice[];
}

/** Mirrors `ShotChoice.MaxPerRequest` (.NET) and `MAX_EDITS` (worker). */
export const MAX_SHOTS_PER_CHANGE = 6;
/** Mirrors `ShotChoice.MaxShowLength`. */
export const MAX_SHOT_DESCRIPTION_CHARS = 120;
