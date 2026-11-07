"""Rebuild the graph state a refine resumes from.

A refine re-enters the pipeline part-way through, which only works if the stages
it is *skipping* can have their output handed to it. The previous render wrote
those artifacts down for exactly this purpose:

  ============  =========================  ==============================
  Stage         What it produced           Where it went
  ============  =========================  ==============================
  scriptwriter  script, title, metadata    on the video document
  voice         the narration mp3          blob: ``…/narration.mp3``
  visuals       stock footage              references only, re-fetched here
  visuals       AI stills                  blob: ``…/stills/NN.webp``
  ============  =========================  ==============================

Two deliberate asymmetries in what was kept, both recorded in
`DECISION-video-brand-and-refine.md`:

*The narration is kept as bytes* because regenerating it costs ElevenLabs money
on a premium voice — the one artifact that is both small and dear.

*Stock footage is kept only as links* because twelve HD clips is roughly 120 MB
per video, and it is public and free to fetch again. Storing it would mean
paying a large upload on every render for something most videos never use.

**Nothing here is fatal.** Every artifact that cannot be rebuilt comes back
absent, and the caller decides whether the stage it wanted can still run. A
refine that cannot be assembled must fail *before* anything is charged or
overwritten, not half-way through an ffmpeg encode.
"""
from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

from . import beats, storage

log = logging.getLogger(__name__)

#: Minimum plausible size for a downloaded clip. Pexels sometimes answers a
#: dead link with a short HTML error page and a 200, and a 2 KB "video" fails
#: inside ffmpeg several stages later, pointing at the wrong thing.
MIN_CLIP_BYTES = 10_000


def narration(doc: dict, out_dir: Path) -> str | None:
    """The saved narration as a local file, or None to re-synthesise it."""
    key = (doc.get("audioBlobKey") or "").strip()
    if not key:
        # Normal for any video rendered before the narration was kept, and for
        # one whose upload failed — the key is only written after a successful
        # upload, precisely so this stays trustworthy.
        log.info("No saved narration for %s.", doc.get("_id"))
        return None

    target = out_dir / "narration.mp3"
    if not storage.download_file(key, target):
        return None
    return str(target)


def stills(doc: dict, out_dir: Path) -> list[str]:
    """The AI-generated stills, in order. Empty for a stock-footage video.

    Kept and reused rather than regenerated because Flux is stochastic:
    regenerating returns *different* pictures, so somebody who asked to fix a
    caption would get a video that no longer looks like the one they approved.
    """
    keys = [k for k in (doc.get("stillBlobKeys") or []) if isinstance(k, str) and k]
    if not keys:
        return []

    saved: list[str] = []
    still_dir = out_dir / "stills"
    for index, key in enumerate(keys):
        target = still_dir / f"{index:02d}{Path(key).suffix or '.webp'}"
        if storage.download_file(key, target):
            saved.append(str(target))

    if len(saved) != len(keys):
        # Partial is still useful — the visuals node cycles a short pool — but
        # it means the video will not look quite as it did, so say so.
        log.warning(
            "Video %s: %d of %d stills recovered. The refine will reuse what "
            "came back and the missing beats will repeat an image.",
            doc.get("_id"), len(saved), len(keys),
        )
    return saved


def _frame_size(shorts: bool) -> tuple[int, int]:
    """The frame size the render itself would use.

    Imported from `nodes` rather than restated. They are not the literals they
    look like: `SHORTS_RES` and `LONGFORM_RES` are read from `SHORTS_WIDTH`,
    `SHORTS_HEIGHT` and friends at import, so an environment that sets those
    would have had a hardcoded copy here rebuilding clips at a size the original
    render never used — footage that does not match the video it is being cut
    back into, from a constant that looked safe to duplicate.

    Imported inside the function because `nodes` pulls in moviepy, Pillow and
    ImageMagick configuration at module load, and nothing else in this module
    needs them.
    """
    from .nodes import LONGFORM_RES, SHORTS_RES

    return SHORTS_RES if shorts else LONGFORM_RES


def clips_from_stills(
    still_paths: list[str], metadata: dict, out_dir: Path, shorts: bool = True
) -> list[str]:
    """Rebuild the on-screen clips of an AI-imagery video from its saved stills.

    **Why this is here and not in the visuals node.** The obvious reading of
    "reuse the saved stills" is that the visuals stage should skip generation
    when it already has pictures. That is wrong, and getting it wrong would cost
    money in one direction and quality in the other:

    * A **visuals** refine is somebody asking for *different footage*. Reusing
      the old pictures there would hand back the images they just asked to
      change, and report success.
    * An **editor** refine is somebody asking to resize a caption. Regenerating
      there would bill Replicate and — because Flux is stochastic — return a
      video that no longer looks like the one they approved.

    So the reuse belongs to the editor path, and the editor path is this: the
    stills are downloaded, and the clips are rebuilt from them with ffmpeg.
    `image_to_video_clip` shells out to ffmpeg and touches no provider, so this
    costs nothing and keeps the editor lane genuinely free.

    Returns [] on any failure, which the caller reports as a refine that cannot
    be run rather than one that silently drops the visuals.
    """
    if not still_paths:
        return []

    try:
        from . import ai_imagery
    except Exception as exc:  # noqa: BLE001
        log.warning("Cannot rebuild stills into clips (%s).", exc)
        return []

    target = _frame_size(shorts)
    segments = (metadata or {}).get("segments") or []

    clips: list[str] = []
    for index, still in enumerate(still_paths):
        # The same duration the first render gave this beat, so the recut keeps
        # its timing. Falls back to the visuals node's own default when the
        # segment list is shorter than the stills — which happens when a beat
        # fell back to stock footage and left a gap.
        seconds = 3.5
        if index < len(segments):
            seg = segments[index]
            try:
                span = float(seg.get("end", 0)) - float(seg.get("start", 0))
                seconds = max(1.5, min(8.0, span) or 3.5)
            except (TypeError, ValueError):
                pass

        try:
            built = ai_imagery.image_to_video_clip(
                Path(still), out_dir, duration_s=seconds, target_size=target
            )
        except Exception as exc:  # noqa: BLE001
            log.warning("Still %s would not rebuild into a clip: %s", still, exc)
            built = None

        if built is not None:
            clips.append(str(built))

    if len(clips) != len(still_paths):
        log.warning(
            "Rebuilt %d of %d stills into clips.", len(clips), len(still_paths)
        )
    return clips


def _segments(state_or_doc: dict) -> list[dict]:
    meta = state_or_doc.get("metadata")
    if meta is None:
        meta = script_state(state_or_doc).get("metadata") or {}
    return [s for s in (meta.get("segments") or []) if isinstance(s, dict)]


def pinned_beats(doc: dict, segment_count: int) -> dict[int, int]:
    """Which moments show the pharmacy's own photographs: ``{moment: image}``.

    The one answer to that question, used by the refine, the shot editor and
    the render alike.

    * After a render that recorded it: exactly the moments that showed a photo
      (`beats.recorded_photos`). A photo the pharmacist replaced with footage
      stays replaced; a picture on the list that was never shown stays unshown.
    * When pictures are being added, or on records too old to say: planned
      from the list, as the first render did.
    """
    from . import user_media

    images = [i for i in (doc.get("images") or []) if isinstance(i, dict)]
    if not images or segment_count <= 0:
        return {}
    plan = user_media.plan_pins(segment_count, len(images))

    refine = doc.get("refine") or {}
    recorded = beats.recorded_photos(doc.get("clipSources"), segment_count)
    if recorded is None:
        return plan

    shown: dict[int, int] = {}
    for moment, image in recorded.items():
        index = image if image is not None else plan.get(moment)
        if index is not None and 0 <= index < len(images):
            shown[moment] = index

    if not refine.get("pinsOwnPictures"):
        return shown

    added = refine.get("addedPictures")
    if not isinstance(added, int) or isinstance(added, bool) or added <= 0:
        # Queued by an API that does not record the count yet (an older build,
        # or a row already waiting). Placing only the photos already shown
        # would drop the new ones in silence; planning over the whole list is
        # what every add-pictures change did before, so it is the safe answer.
        return plan

    # Adding pictures: plan again, but only over the photos the video shows
    # now plus the new ones. Planning over the whole list would bring back a
    # photo the pharmacist replaced with footage — and, on a list holding a
    # picture that never rendered, bring that in too.
    keep = sorted(set(shown.values()) | set(range(max(0, len(images) - added), len(images))))
    placed = user_media.plan_pins(segment_count, len(keep))
    return {moment: keep[slot] for moment, slot in placed.items() if slot < len(keep)}


def footage(
    doc: dict,
    out_dir: Path,
    segment_count: int,
) -> tuple[list[str], list[dict | None], list[str]]:
    """Fetch the stock clips again, laid out one per moment.

    Returns ``(clip per moment, record per moment, fill clips)``. A moment with
    nothing to fetch — the pharmacy's own photograph, or a clip that will not
    download — is ``""`` / ``None``; the caller decides what goes there.

    **By moment, never by position.** See `beats`: the records used to be
    paired with moments by their order in a list, which stopped matching at the
    first photograph or failed search, so a re-cut slid every later shot one
    moment early and dropped the photograph entirely.

    A clip borrowed by several moments is fetched once.
    """
    records = [s for s in (doc.get("clipSources") or []) if isinstance(s, dict)]
    if not records:
        return [""] * segment_count, [None] * segment_count, []

    pinned = pinned_beats(doc, segment_count)
    per_beat, extra_records = beats.by_beat(records, segment_count, set(pinned))

    clip_dir = out_dir / "clips"
    fetched: dict[str, str] = {}

    def fetch(record: dict) -> str:
        link = (record.get("link") or "").strip()
        if not link:
            # The id is the documented fallback for a moved CDN, but looking it
            # up needs a Pexels call, and a gap is survivable. Visible in the
            # count below.
            return ""
        if link not in fetched:
            target = clip_dir / f"{len(fetched):03d}.mp4"
            fetched[link] = str(target) if _download(link, target) else ""
        return fetched[link]

    paths: list[str] = []
    sources: list[dict | None] = []
    for record in per_beat:
        path = fetch(record) if record else ""
        paths.append(path)
        # The original record with the local path corrected to where it has
        # just been downloaded. `link` and `pexels_id` are what make it
        # recoverable again next time.
        # Kept even when the download failed (path ""): the record still says
        # what the moment showed, which the shot editor describes to the model.
        # The render keeps a shot only when its file exists, so an empty path
        # means "search this moment again", never "keep nothing".
        sources.append({**record, "path": path} if record else None)

    extras = [p for p in (fetch(r) for r in extra_records) if p]

    wanted = sum(1 for r in per_beat if r) + len(extra_records)
    got = sum(1 for p in paths if p) + len(extras)
    if got < wanted:
        log.warning(
            "Video %s: recovered %d of %d stock clips.", doc.get("_id"), got, wanted,
        )
    return paths, sources, extras


def user_clips(doc: dict, segments: list[dict], out_dir: Path) -> dict[int, str]:
    """The pharmacy's photographs rebuilt as clips, keyed by moment.

    A re-cut skips the visuals stage, which is what normally makes these — so
    without this a caption change quietly removed every photograph from the
    video. Rebuilt from the stored pictures with their framing; free, since it
    is ffmpeg on stored files.
    """
    pins = pinned_beats(doc, len(segments))
    if not pins:
        return {}

    from . import ai_imagery, user_media

    def duration_for(index: int) -> float:
        seg = segments[index] if index < len(segments) else {}
        try:
            span = float(seg.get("end", 0)) - float(seg.get("start", 0))
        except (TypeError, ValueError):
            span = 0.0
        return max(1.5, min(span or 3.5, 8.0))

    images = [i for i in (doc.get("images") or []) if isinstance(i, dict)]
    clips, skipped = user_media.prepare_user_clips(
        images, pins, out_dir / "user", _frame_size(True), duration_for,
        download=storage.download_file,
        to_clip=ai_imagery.image_to_video_clip,
    )
    for reason in skipped:
        log.warning("Video %s: %s", doc.get("_id"), reason)
    return clips


def _download(url: str, target: Path) -> bool:
    """Fetch one clip. False on anything that is not plainly a usable file."""
    import requests

    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        with requests.get(url, stream=True, timeout=60) as response:
            if response.status_code != 200:
                log.info("Clip %s answered %s.", url, response.status_code)
                return False
            with open(target, "wb") as handle:
                for chunk in response.iter_content(chunk_size=1 << 16):
                    handle.write(chunk)
    except Exception as exc:  # noqa: BLE001
        log.info("Clip %s could not be fetched (%s).", url, exc)
        target.unlink(missing_ok=True)
        return False

    if not target.exists() or target.stat().st_size < MIN_CLIP_BYTES:
        # A dead Pexels link can answer 200 with a short error page. Left in
        # place it becomes an ffmpeg failure several stages downstream, blamed
        # on the editor.
        log.info("Clip %s came back too small to be a video.", url)
        target.unlink(missing_ok=True)
        return False

    return True


#: What each entry stage needs handed to it, because the stage that would have
#: produced it is being skipped.
#:
#: `researcher` is absent deliberately: entering there is a full run and needs
#: nothing seeded.
REQUIREMENTS: dict[str, tuple[str, ...]] = {
    "scriptwriter": ("facts",),
    "voice": ("script",),
    "visuals": ("script", "audio_path"),
    "editor": ("script", "audio_path", "footage"),
}


def initial_state(doc: dict, entry: str, out_dir: Path) -> tuple[dict[str, Any], list[str]]:
    """Assemble the state a refine resumes from, and what could not be rebuilt.

    Returns ``(state, missing)``. A non-empty `missing` means the refine must
    **not** run: the caller reports it against the video the pharmacist already
    has, which is left untouched.

    Failing here is the point. The alternative is discovering a missing
    narration inside moviepy, after the lease is taken and the row has been
    flipped to Rendering — at which point the pharmacist's finished video is
    showing "Rendering" and the reason is an ffmpeg stack trace.

    The research facts a `scriptwriter` entry needs are persisted by the render
    that produced them, so a rewrite keeps the sources the first script was
    built on. A video rendered before that — or one whose research was never
    recorded — is refused rather than rewritten from the topic alone, because
    the second is a different feature wearing the name of the first.
    """
    out_dir.mkdir(parents=True, exist_ok=True)
    state: dict[str, Any] = {
        "job_id": str(doc.get("_id") or ""),
        "topic": (doc.get("topic") or doc.get("name") or "").strip(),
    }

    # Carried through whether or not the stage that produced them runs.
    #
    # **This is load-bearing and easy to miss.** When a render finishes, the
    # worker writes these fields back from the graph's *final state*:
    #
    #     "clip_sources": final.get("clip_sources") or [],
    #     "ai_image_cost_usd": final.get("ai_image_cost_usd") or 0.0,
    #
    # A refine that enters at the editor never runs the visuals node, so none of
    # them would be in the state, and each `or` would write an empty value over
    # a good one. Resizing a caption would therefore erase the clip references —
    # leaving the video unrefinable ever again, because rehydration has nothing
    # to re-download — and zero the record of what the render cost, which is the
    # very number the charge policy is waiting on.
    #
    # It is the same fault that made renaming a video destroy its script: one
    # writer, a partial picture, and a blind overwrite. Seeding them here means
    # the stages that do not run hand back what was already true.
    state["clip_sources"] = [
        s for s in (doc.get("clipSources") or []) if isinstance(s, dict)
    ]
    # Same reasoning as clip_sources above: the worker writes `facts` back from
    # the final state, so an entry that skips the researcher would overwrite the
    # sources the script was built on with an empty list — and then the *next*
    # refine would have nothing to rewrite from.
    state["facts"] = [f for f in (doc.get("facts") or []) if isinstance(f, dict)]
    state["ai_image_cost_usd"] = float(doc.get("aiImageCostUsd") or 0.0)
    state["ai_images_generated"] = int(doc.get("aiImagesGenerated") or 0)

    # `user_image_warnings` is deliberately NOT seeded here, and — like
    # `ai_still_paths` below — the reason is worth writing down because it looks
    # like the same omission the three fields above exist to prevent.
    #
    # An editor refine does skip the visuals node, so the Ready write does clear
    # the "Attached images not used: …" diagnostic, even though the beats in
    # that cut really are still stock. Seeding cannot fix that: `diagnostic` is
    # persisted as one formatted sentence rather than a list, and `claim`
    # `$unset`s it before the graph starts — correctly, so a stale failure
    # reason never outlives the attempt that caused it. Carrying it would mean a
    # second persisted field, owned and mapped on the .NET side, for text no
    # pharmacist ever sees.
    #
    # So the accepted behaviour is: the note survives the render that produced
    # it, and a later refine drops it. That is a support answer lost, not a
    # video — unlike `clip_sources`, where the same seam cost the ability to
    # refine at all.

    # `ai_still_paths` is deliberately NOT seeded here, and the reason is worth
    # writing down because it looks like an omission.
    #
    # Seeding it would change nothing: the worker rebuilds `stillBlobKeys` by
    # uploading whatever local files that key points at, so an empty list and an
    # absent key both end as an empty list. What protects a stock-footage video
    # is that it has no stills to lose; what protects an AI video is that editor
    # refines of one are refused outright.
    #
    # **That second protection expires** when the visuals node learns to reuse
    # saved stills. At that point a refine whose stills only partly downloaded
    # would re-upload the recovered ones and quietly shorten `stillBlobKeys`.
    # The fix then belongs in the worker — do not rewrite that field when the
    # stage that owns it did not run — not here.

    needs = REQUIREMENTS.get(entry, ())
    missing: list[str] = []

    if "facts" in needs and not state["facts"]:
        # A rewrite with no sources is not the same feature. The scriptwriter
        # would write from the topic alone, inventing whatever it liked in place
        # of the researched claims the first script was built on — and this is
        # patient-facing health copy. Refused rather than silently downgraded.
        missing.append(
            "the research behind this video is not on record, so rewriting it "
            "would mean starting the subject again from scratch"
        )

    if "script" in needs:
        state.update(script_state(doc))
        if not state.get("script"):
            missing.append(
                "the script from the first render is not on record, so there is "
                "nothing to re-cut"
            )

    if "audio_path" in needs:
        audio = narration(doc, out_dir)
        if audio:
            state["audio_path"] = audio
        else:
            missing.append(
                "the saved narration could not be retrieved, so the video would "
                "have to be spoken again"
            )

    # <b>A visuals refine keeps the shots it was not asked to change.</b>
    #
    # Opportunistic, and deliberately *not* in REQUIREMENTS: a video with no
    # stored clip references must still be refinable, so a missing pool degrades
    # to re-searching everything rather than refusing.
    #
    # Without this, adding one photograph or re-filling one moment re-searched
    # every beat — and `_pexels_search` picks a random page and shuffles within
    # quality tiers, deliberately, so "every beat" means "different footage
    # throughout". A pharmacist asking to change the last shot got a video whose
    # every shot had changed, which is not the change they agreed to.
    if entry == "visuals" and "footage" not in needs:
        try:
            count = len(_segments(state))
            previous, recovered_sources, _extras = footage(doc, out_dir, count)
            kept = sum(1 for p in previous if p)
            if kept:
                # One entry per moment, "" where there is nothing to keep, so
                # the visuals node's `previous_aligned[i]` is moment i.
                state["previous_aligned"] = previous
                state["previous_sources"] = recovered_sources
                log.info(
                    "Video %s: %d of %d shot(s) recovered to keep across this refine",
                    doc.get("_id"), kept, count,
                )
        except Exception:  # noqa: BLE001
            # Keeping the old shots is an improvement on re-searching, never a
            # precondition for it.
            log.warning(
                "Video %s: could not recover the previous shots; the refine will "
                "search for all of them again.", doc.get("_id"), exc_info=True,
            )

    if "footage" in needs:
        segments = _segments(state)
        aligned, _sources, extras = footage(doc, out_dir, len(segments))
        if not segments:
            # No moments to lay clips against (unreadable metadata): the editor
            # then treats everything as a pool, as it always did.
            extras = [p for p in aligned if p] + extras
            aligned = []
        else:
            # Stock only, taken before the photographs go in: a moment whose
            # clip would not download borrows one as the first render did, and
            # a photograph must not turn up twice.
            stock = [p for p in aligned if p]
            for i, path in user_clips(doc, segments, out_dir).items():
                if i < len(aligned):
                    aligned[i] = path
            if not stock and not extras:
                stock = [p for p in aligned if p]
            # Spares first, then least-used and not next door — the same rule
            # as the first render (`beats.fill_gaps`), so a re-cut does not
            # turn a missing clip into a duplicate shot.
            aligned = beats.fill_gaps(aligned, stock, extras)
            if not any(aligned):
                aligned = []
        still_paths = stills(doc, out_dir)

        # AI-imagery videos keep the *stills*, not the Ken Burns clips built
        # from them — the clips are local ffmpeg output and regenerating them is
        # free, where regenerating the pictures is both billable and stochastic.
        # So the visuals node rebuilds the motion from the same images.
        if still_paths:
            state["ai_still_paths"] = still_paths

        if aligned or extras:
            state["aligned_clips"] = aligned
            state["extra_clips"] = extras
            state["video_clips"] = aligned + extras
        elif still_paths:
            # An AI-imagery video. What is saved is the *stills*; what the
            # editor plays is the clips built from them, which are local ffmpeg
            # output and not worth storing. So they are rebuilt here — no
            # provider is called, the pictures are the ones the pharmacist
            # already approved, and the editor lane stays free.
            rebuilt = clips_from_stills(
                still_paths,
                state.get("metadata") or {},
                out_dir / "stills",
                # Always shorts, deliberately — **not** derived from the
                # document's `aspect`.
                #
                # `_render_sync` builds its context with `video_mode="shorts"`
                # hardcoded, and the visuals node picks the frame size from
                # `video_mode`, never from `aspect`. So every render is vertical
                # today whatever the document says. Reading `aspect` here looked
                # more correct and was: a video stored as 16:9 would have had
                # its clips rebuilt at 1920x1080 and cut back into a 1080x1920
                # video.
                #
                # If `video_mode` ever stops being hardcoded, this moves with
                # it — they are one decision in two places.
                shorts=True,
            )
            if rebuilt:
                # All aligned: each still was generated for one script beat, so
                # the nth clip belongs with the nth segment. There is no fill
                # pool for an AI video.
                state["aligned_clips"] = rebuilt
                state["extra_clips"] = []
                state["video_clips"] = rebuilt
            else:
                missing.append(
                    "the pictures from the first version could not be turned "
                    "back into video, so there is nothing to re-cut"
                )
        else:
            missing.append(
                "none of the original footage could be fetched again, so the "
                "video would have to be rebuilt from scratch"
            )

    if missing:
        log.warning(
            "Video %s cannot be refined at the %s stage: %s",
            doc.get("_id"), entry, "; ".join(missing),
        )

    return state, missing


def script_state(doc: dict) -> dict[str, Any]:
    """The scriptwriter's output, read back off the document."""
    import json

    metadata: dict[str, Any] = {}
    raw = doc.get("metadataJson")
    if isinstance(raw, str) and raw.strip():
        try:
            parsed = json.loads(raw)
            if isinstance(parsed, dict):
                metadata = parsed
        except (ValueError, TypeError):
            # The segment timings live in here, and the editor places every
            # caption from them. Losing it is not fatal — the video renders
            # without captions — but it is not a quiet thing either.
            log.warning(
                "Video %s: stored metadata is not readable JSON. The refine "
                "will have no caption timings.", doc.get("_id"),
            )

    return {
        "script": doc.get("script") or "",
        "title": doc.get("title") or "",
        "description": doc.get("generatedDescription") or "",
        "tags": list(doc.get("tags") or []),
        "metadata": metadata,
    }
