"""
Video render worker. Polls the same MongoDB `videos` collection the .NET
API writes to, picks up documents in the `Pending` state, runs the
LangGraph pipeline, uploads the MP4 + JPG to Azure Blob using the same
key scheme (`videos/{ownerId}/{videoId}.mp4`) the .NET API expects, and
flips the row to `Ready`.

The .NET API stays authoritative for the Video entity's business rules
(tier gating, count-per-month, delete cleanup). This worker only mutates
the render-pipeline fields:
    status, note, blobKey, thumbnailBlobKey, updatedAtUtc,
    title, script, description, tags, metadata (json), aiImageCostUsd,
    aiImagesGenerated
"""
from __future__ import annotations

import asyncio
import logging
import re
import subprocess
import traceback
import os
import socket
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from motor.motor_asyncio import AsyncIOMotorClient

from ..config import get_settings
import tempfile

from . import editor_options, rehydrate, shot_edits, usage as usage_mod
from .context import BrandContext, UserContext
from .refine_lanes import (
    REFINE_FREE_STAGES,
    REFINE_STAGES,
    LANE_REFINE,
    LANE_RENDER,
    lane_filter,
)
from .graph import build_graph
from .nodes import NonRetriablePipelineError
from . import storage

log = logging.getLogger(__name__)

# VideoStatus enum values — must match the .NET Acme.Domain.Videos.VideoStatus.
STATUS_PENDING = 0
STATUS_RENDERING = 1
STATUS_READY = 2
STATUS_FAILED = 3

COLLECTION = "videos"

# Most research facts kept on a video. The researcher is asked for seven; this
# is headroom, not a target, and exists so a malformed reply cannot grow the
# document without bound.
MAX_PERSISTED_FACTS = 20

# How long a claim survives without being renewed.
#
# Generous, because the stages are genuinely slow: research, a language model
# writing a script, speech synthesis, stock or generated imagery downloads, then
# an ffmpeg encode. A lease shorter than the slowest legitimate stage is worse
# than none — a second worker reclaims a job that was fine, and the pharmacy
# gets two renders of the same video and pays for the imagery twice.
#
# The renewal below fires on every progress report, so the only way this lapses
# is the process actually stopping.
LEASE_SECONDS = 600

# Grace for a Rendering row carrying no lease at all — a render claimed by a
# worker built before leases existed.
#
# Longer than LEASE_SECONDS on purpose. A missing lease cannot distinguish
# "old worker, going fine" from "abandoned", so the only safe reading is
# silence: if nothing has touched the row for longer than a lease would have
# lasted, nobody is working on it. Anything shorter reclaims healthy renders.
UNLEASED_GRACE_SECONDS = 900

# Identifies this process in the lease. Host and pid, so a job stuck in
# production can be traced to the instance that was holding it — a uuid would be
# unique and tell nobody anything.
WORKER_ID = f"{socket.gethostname()}/{os.getpid()}"


class VideoWorker:
    """Long-running poller. Owned by FastAPI's lifespan — starts on app
    boot, stops on shutdown."""

    def __init__(self) -> None:
        # One task per lane. They poll the same collection through disjoint
        # filters, so a refine that takes three seconds is never stuck behind a
        # first render that takes ninety — which is the whole reason a pharmacist
        # would believe "adjust this" is a different kind of action from "make
        # me a video".
        self._tasks: list[asyncio.Task[None]] = []
        self._stop_event = asyncio.Event()

    def start(self) -> None:
        s = get_settings()
        # Loud startup log so it's obvious in `docker compose logs` whether
        # the worker actually spun up. Anything missing shows up as a
        # WARN and the worker refuses to start (rather than silently
        # picking up nothing forever).
        log.info(
            "Video worker config: enabled=%s poll=%.1fs mongo=%s db=%s blob=%s container=%s "
            "anthropic=%s tavily=%s pexels=%s replicate=%s eleven=%s",
            s.video_worker_enabled,
            s.video_worker_poll_seconds,
            "set" if s.mongo_connection_string else "MISSING",
            s.mongo_database or "MISSING",
            "set" if s.azure_blob_connection_string else "MISSING",
            s.azure_blob_container or "MISSING",
            _key_fingerprint(s.anthropic_api_key),
            "set" if s.tavily_api_key else "MISSING",
            "set" if s.pexels_api_key else "MISSING",
            "set" if s.replicate_api_token else "off",
            "set" if s.elevenlabs_api_key else "off",
        )
        if not s.video_worker_enabled:
            log.warning("Video worker DISABLED (VIDEO_WORKER_ENABLED=false)")
            return
        if not s.mongo_connection_string:
            log.error(
                "Video worker WILL NOT START — Mongo__ConnectionString is empty. "
                "Check the ai-service env in docker-compose.yml."
            )
            return
        # Warn (but still start) on missing API keys — the pipeline can't
        # succeed without them but users may want to test connectivity first.
        for name, val in (
            ("ANTHROPIC_API_KEY", s.anthropic_api_key),
            ("TAVILY_API_KEY", s.tavily_api_key),
            ("PEXELS_API_KEY", s.pexels_api_key),
        ):
            if not val:
                log.warning(
                    "Video worker: %s is empty — pipeline will fail at that stage",
                    name,
                )
        if self._tasks:
            return
        for lane in (LANE_REFINE, LANE_RENDER):
            self._tasks.append(
                asyncio.create_task(self._loop(lane), name=f"video-worker-{lane}")
            )
        log.info(
            "✅ Video worker started — %d lanes (%s), poll=%.1fs",
            len(self._tasks), ", ".join((LANE_REFINE, LANE_RENDER)),
            s.video_worker_poll_seconds,
        )

    async def stop(self) -> None:
        if not self._tasks:
            return
        self._stop_event.set()
        # Waited on together, not one after the other: a lane mid-render can
        # take the full ten seconds, and serialising would give the second lane
        # whatever was left of nothing.
        done, pending = await asyncio.wait(self._tasks, timeout=10)
        for task in pending:
            task.cancel()
        for task in done:
            # `.exception()` raises on a cancelled task rather than returning,
            # so shutdown would itself throw — from the very code meant to
            # report a lane that failed.
            if task.cancelled():
                continue
            # Surfaced rather than swallowed. A lane that died early would
            # otherwise look exactly like a lane that shut down tidily, and the
            # symptom — refines never running — appears much later.
            if (exc := task.exception()) is not None:
                log.error("Video worker lane ended with an error: %s", exc)
        self._tasks = []
        log.info("Video worker stopped")

    async def _loop(self, lane: str) -> None:
        s = get_settings()
        client = AsyncIOMotorClient(s.mongo_connection_string)
        db = client[s.mongo_database]
        col = db[COLLECTION]

        # Verify Mongo is reachable — otherwise the loop happily polls
        # a dead connection forever with no indication anything's wrong.
        try:
            await client.admin.command("ping")
            log.info("Mongo reachable: %s (db=%s)", s.mongo_connection_string.split("@")[-1], s.mongo_database)
        except Exception as exc:  # noqa: BLE001
            log.error("Mongo ping FAILED: %s. Worker will keep retrying but nothing will get processed.", exc)

        # Best-effort — the .NET side already ensures this on startup.
        try:
            storage.ensure_container()
        except Exception as exc:  # noqa: BLE001
            log.warning("ensure_container failed: %s", exc)

        # Heartbeat: log every N idle cycles so users can see the worker
        # is alive even when nothing's queued. At 4s poll + 15 cycles we
        # log once per minute of idle time.
        idle_cycles = 0
        heartbeat_every = int(60 / max(s.video_worker_poll_seconds, 1))

        while not self._stop_event.is_set():
            try:
                picked = await self._process_one(col, lane)
                if picked:
                    idle_cycles = 0
                else:
                    idle_cycles += 1
                    if idle_cycles % heartbeat_every == 0:
                        # Counted through this lane's own filter. A shared count
                        # would have each lane report the other's backlog, so a
                        # refine lane sitting idle behind a queue of renders
                        # would look busy.
                        waiting = await col.count_documents(
                            {"$and": [{"status": STATUS_PENDING}, lane_filter(lane)]}
                        )
                        log.info(
                            "Video worker [%s] idle (0 claimed, %d waiting in this lane)",
                            lane, waiting,
                        )
                    await asyncio.wait_for(
                        self._stop_event.wait(),
                        timeout=s.video_worker_poll_seconds,
                    )
            except asyncio.TimeoutError:
                pass  # normal — no pending work
            except Exception as exc:  # noqa: BLE001
                log.exception("Worker loop error: %s", exc)
                await asyncio.sleep(s.video_worker_poll_seconds)

    async def _prepare_refine(self, col, job: dict, held: dict) -> bool:
        """Turn a claimed refine into something the graph can resume from.

        Returns False when the refine cannot be run, having already put the
        video back to Ready with a reason. Returns True for an ordinary render,
        which it leaves untouched.

        **Refused before anything is charged or overwritten, and restored to
        Ready rather than Failed.** The video the pharmacist asked to change
        still exists and is still good — marking it Failed would take a working
        video away from them because we could not fetch a stock clip. They see
        their video and a sentence about why the change did not happen.
        """
        refine = job.get("refine")
        if not isinstance(refine, dict) or not refine.get("confirmed"):
            return True

        # Started here, not in _render_sync, because interpreting the
        # instruction is itself a model call — and on a refine that is then
        # refused, it is the *only* thing that happened. A ledger created later
        # would miss it entirely and let "editor refines are free" stay an
        # assertion rather than a measurement.
        ledger = usage_mod.UsageLedger()
        job["_ledger"] = ledger

        stage = str(refine.get("stage") or "").strip().lower()
        video_id = job["_id"]

        async def refuse(reason: str, level=log.warning) -> bool:
            level("Refine refused for video %s: %s", video_id, reason)

            # Back to Ready only if there is actually a video to go back to.
            #
            # The usual case is a finished video whose change could not be
            # made — Failed would take a working video away from a pharmacist
            # because we could not re-fetch a stock clip. But a *failed* video
            # carries its confirmed refine through Retry, and if that one is
            # refused there is no rendered file behind it. Ready would then show
            # a play button attached to nothing.
            wrote = await col.update_one(held, _merge_update(
                {"$set": {
                    **_refusal_outcome(job, reason),
                    "leaseOwner": None,
                    "leaseExpiresAtUtc": None,
                    "updatedAtUtc": datetime.now(timezone.utc),
                }},
                # Refused, but not free: interpreting the instruction cost a
                # model call. Recording it is the difference between knowing
                # what refines cost and assuming.
                _usage_write(ledger.totals(), ledger.increments(), "refused"),
            ))
            if wrote.matched_count == 0:
                log.warning(
                    "Video %s: could not record the refusal — the lease moved on.",
                    video_id,
                )
            return False

        if stage not in REFINE_STAGES:
            return await refuse(
                "We could not work out what to change. Please ask again.",
                log.error,
            )

        # Pinning the pharmacy's own pictures re-enters at `visuals`, which is
        # not a free stage — for a *sentence*. It is free for a photograph, and
        # the difference is real rather than a loophole: no model is asked
        # anything, the script and narration are reused from storage, the stock
        # footage comes back from Pexels at no cost, and the pictures belong to
        # the pharmacy already. `RefineStages.PicturesMayBePinned` on the .NET
        # side is the same rule, and RefineStageContractTests keeps the two
        # honest.
        #
        # Narrow on purpose. It admits exactly one stage, only when the .NET
        # side set this flag, and never on an AI-imagery video — re-entering
        # visuals on one of those regenerates every beat through Replicate,
        # which is precisely the bill the check below exists to prevent.
        refine_doc = job.get("refine") or {}
        pins_pictures = bool(refine_doc.get("pinsOwnPictures"))
        # A shot edit re-enters at the same stage for the same reason: it
        # re-searches free stock and reuses the stored script and narration. The
        # one model call it makes is the interpretation, which is what an editor
        # refine already costs and what the free lane already covers.
        edits_shots = bool(refine_doc.get("editsShots"))
        # `_coerce_visual_style` rather than an int comparison, because the
        # field arrives as an ordinal from .NET and as a name from anything
        # hand-written, and it already knows both. A second reading of the same
        # field is how the two drift.
        free_for_pictures = (
            (pins_pictures or edits_shots)
            and stage == "visuals"
            and _coerce_visual_style(job.get("visualStyle")) != "ai_imagery"
        )

        # The .NET side refuses closed stages before queueing, so reaching here
        # means the two disagree. Checked anyway: this is the guard between a
        # routing mistake and an unbudgeted bill, and it is nearly free.
        if stage not in REFINE_FREE_STAGES and not free_for_pictures:
            return await refuse(
                "That change needs a part of the service we have not opened "
                "up yet. Creating a new video is the way to do it for now.",
                log.error,
            )

        work_dir = Path(tempfile.gettempdir()) / "marketing25" / str(video_id) / "refine"

        # Blob downloads and HTTP fetches, so off the event loop — this lane is
        # supposed to stay responsive for the next short job behind it.
        state, missing = await asyncio.to_thread(
            rehydrate.initial_state, job, stage, work_dir
        )
        if missing:
            return await refuse(missing[0].capitalize() + ".")

        if stage == "editor":
            # Off the event loop. `interpret` makes a blocking model call with a
            # thirty-second ceiling, and awaiting it inline would freeze *both*
            # lanes for its duration — including the render lane's progress
            # writes, which are also the lease heartbeat. A refine lane that
            # stalls every other lane is worse than no lane at all.
            options = await asyncio.to_thread(
                editor_options.interpret, refine.get("instruction") or "",
                None, ledger,
            )
            if options.remove_repeated_captions and not _has_repeated_caption(state):
                # The one request a re-cut answers without a setting — so check
                # the video actually repeats a caption before promising to
                # remove one. Otherwise it would re-cut, succeed and change
                # nothing, which is exactly the complaint this branch exists for.
                options.remove_repeated_captions = None
                if options.is_empty():
                    return await refuse(
                        "We checked, and no caption in this video's script "
                        "appears twice, so re-cutting it would not change "
                        "anything. If it is the same footage you can see "
                        "twice, ask us to replace the repeated shot. If it is "
                        "the same words, tell us which words and roughly when."
                        + _cannot(options.unsupported)
                    )
            if options.is_empty():
                # Nothing the editor can act on. Running anyway would re-cut
                # identical inputs, report success, and hand back a video that
                # is unchanged in every way the pharmacist cared about.
                detail = _cannot(options.unsupported)
                return await refuse(
                    "We could not find anything in that to change about the "
                    "captions, timing or closing card." + detail
                )
            job["_options"] = options

        if edits_shots:
            # The shots come from the pharmacist, never from a model: the tiles
            # they tapped in the refine dialog (`shotEdits`), or a typed
            # "first / last / shot N". See `shot_edits` for why the model that
            # used to choose was removed. No model call happens here at all.
            moments = list((state.get("metadata") or {}).get("segments") or [])
            if not moments:
                # No shot list to point at. `script_state` logs when the stored
                # metadata will not parse; this is that cause, said as what it
                # is rather than blamed on the pharmacist's choice.
                return await refuse(
                    "We do not have a record of this video's shots, so we cannot "
                    "change one. Creating a new video is the way to do it."
                )

            topic = str(state.get("topic") or job.get("topic") or "")
            picked = refine_doc.get("shotEdits")
            if picked:
                edits = shot_edits.from_picker(picked, len(moments), topic)
                if edits.is_empty():
                    # The API checks the indexes against the same list, so this
                    # means the video changed between opening the dialog and
                    # the worker picking the change up.
                    return await refuse(
                        "The shots you chose are no longer in this video. Open "
                        "the change again and pick from the current list."
                    )
            else:
                edits = shot_edits.from_typed(
                    refine.get("instruction") or "", len(moments), topic)
                if edits is None or edits.is_empty():
                    return await refuse(shot_edits.TAP_THE_SHOT)

            job["_shot_edits"] = edits.by_index()
            log.info("Video %s: re-filling moments %s", video_id, job["_shot_edits"])

        # Which moments show the pharmacy's photographs in this render: what
        # the last render actually showed, less any photo moment a shot edit
        # is re-filling. Decided once here so the refine, the shot list and the
        # render cannot disagree.
        segments = list((state.get("metadata") or {}).get("segments") or [])
        pins = rehydrate.pinned_beats(job, len(segments))
        for moment in (job.get("_shot_edits") or {}):
            pins.pop(moment, None)
        job["_user_pins"] = pins

        job["_entry"] = stage
        job["_state"] = state
        log.info(
            "Video %s resuming at the %s stage: %r",
            video_id, stage, (refine.get("instruction") or "")[:80],
        )
        return True

    async def _process_one(self, col, lane: str) -> bool:
        """Atomically claim one renderable row → Rendering, then process it
        off the DB thread. Returns True if a job was picked up.

        Claimable means Pending, or Rendering with a lease that has lapsed.

        The second case is recovery, and it is why the lease exists. This
        used to claim `{"status": STATUS_PENDING}` and set Rendering with
        nothing to say who held it or for how long — and nothing anywhere
        read Rendering back. A worker killed mid-render (a deploy, a
        container restart, ffmpeg taking an OOM kill) left the row in
        Rendering permanently: this filter would never match it again, and
        the .NET Retry refused anything that was not Failed, which a row
        abandoned mid-flight never becomes.

        That was not merely a stuck job. The pharmacy's monthly allowance
        or a purchased credit is spent when the video is created, so the
        render they paid for could neither finish nor be retried.

        `leaseExpiresAtUtc` missing is treated as lapsed on purpose: every
        row claimed before leases existed looks like that, and those are
        precisely the ones that have been stranded.
        """
        now = datetime.now(timezone.utc)
        lease_until = now + timedelta(seconds=LEASE_SECONDS)

        # How far back a lease-less Rendering row must have gone quiet before we
        # presume its worker is gone. Mirrors Video.UnleasedRenderGrace on the
        # .NET side; the two need not be equal, but neither may be shorter than
        # LEASE_SECONDS or a live render gets reclaimed.
        unleased_cutoff = now - timedelta(seconds=UNLEASED_GRACE_SECONDS)

        claimable = {
            "$or": [
                {"status": STATUS_PENDING},
                {
                    "status": STATUS_RENDERING,
                    "$or": [
                        # Lease present and expired: the holder is gone.
                        {"leaseExpiresAtUtc": {"$lt": now}},
                        # No lease at all — claimed by a worker built before
                        # leases existed. Only reclaimed once the row has been
                        # *silent* for longer than a lease would have lasted.
                        #
                        # Taking it on sight, which is what this used to do,
                        # means that during a rolling deploy a new worker
                        # snatches a healthy render from an old one and the
                        # pharmacy pays for the imagery twice — the exact
                        # outcome the lease was introduced to prevent. The same
                        # over-eager rule made the API report live renders as
                        # stalled; see Video.UnleasedRenderGrace.
                        {
                            "leaseExpiresAtUtc": None,
                            "updatedAtUtc": {"$lt": unleased_cutoff},
                        },
                        {
                            "leaseExpiresAtUtc": {"$exists": False},
                            "updatedAtUtc": {"$lt": unleased_cutoff},
                        },
                    ],
                },
            ]
        }

        # Whose lane this row is in. The two lane filters partition the
        # collection, so a row is claimable by exactly one of them — see
        # refine_lanes for why that holds.
        claimable = {"$and": [claimable, lane_filter(lane)]}

        # find_one_and_update is one round trip and one document-level atomic
        # operation, so two workers racing cannot both win the same job.
        claim = await col.find_one_and_update(
            claimable,
            {
                "$set": {
                    "status": STATUS_RENDERING,
                    "note": "Starting…",
                    "leaseOwner": WORKER_ID,
                    "leaseExpiresAtUtc": lease_until,
                    "updatedAtUtc": now,
                },
                # Last attempt's technical reason, cleared as this one begins.
                # A retry that succeeds must not leave the previous failure's
                # explanation sitting on a working video.
                "$unset": {"diagnostic": ""},
            },
            sort=[("createdAtUtc", 1)],
        )
        if not claim:
            return False

        if claim.get("status") == STATUS_RENDERING and claim.get("leaseOwner") not in (None, WORKER_ID):
            log.warning(
                "Reclaimed video %s from %s — its lease had lapsed, so that worker "
                "is assumed gone. If this repeats for the same job, the render is "
                "outliving the %ds lease rather than crashing.",
                claim["_id"], claim.get("leaseOwner"), LEASE_SECONDS,
            )

        video_id = claim["_id"]
        owner_id = claim["ownerId"]
        log.info("Claimed video %s (owner=%s, topic=%r)",
                 video_id, owner_id, claim.get("topic") or claim.get("prompt"))

        # Every write below carries this, and none of them may drop it.
        #
        # A lease answers "is anyone rendering this", but it does not on its own
        # stop a worker that lost one from finishing anyway. The case is a worker
        # that HANGS rather than dies — a provider call blocking for a quarter of
        # an hour, a host that starts swapping. Its lease lapses, another worker
        # correctly reclaims the job and starts rendering, and then the first one
        # comes back to life and writes Ready over the top of a render that is
        # still running. Two renders, one of them thrown away, and the pharmacy
        # charged once for whichever arrived last.
        #
        # So the writes are conditional on still holding the claim. A worker that
        # lost it finds its updates match nothing and discards its result, which
        # is the right outcome: the row belongs to whoever holds the lease now.
        held = {"_id": video_id, "leaseOwner": WORKER_ID}

        # Resolve the model here, in the async path, because _render_sync runs
        # on a thread and cannot await Mongo. The result is stamped onto the
        # in-memory copy of the job so the render reads it like any other field.
        job = dict(claim)
        job["model"] = await _resolve_video_model(col.database, job)
        log.info("Video %s using model %s", video_id, job["model"])

        prepared = await self._prepare_refine(col, job, held)
        if not prepared:
            # Refused, and already restored to Ready with a reason. Returning
            # True so the loop treats the cycle as productive rather than
            # sleeping — there may be more work behind this one.
            return True

        # Run the CPU-bound render on a worker thread so we don't block
        # the event loop from serving progress polls to other jobs.
        try:
            result = await asyncio.to_thread(_render_sync, job)
        except Exception as exc:  # noqa: BLE001
            log.error("Render failed for %s: %s\n%s", video_id, exc, traceback.format_exc())
            # A change to a finished video that fell over leaves that video
            # exactly as it was — the refusal path already worked this way, and
            # a crash is no reason to take a working video away and show it as
            # Failed. The file is untouched until a new render uploads.
            failed_update: dict[str, Any] = {
                # Status, the sentence shown, and any pictures to take back off
                # — see `_failure_outcome`. The raw exception is in the log line
                # immediately above.
                **_failure_outcome(job, exc),
                # The same failure, for whoever has to fix it. Never rendered on
                # the video card; read by `pnpm smoke` and anyone looking at the
                # row. Without this, diagnosing a production render means having
                # access to the host's log stream.
                "diagnostic": _technical_reason(exc),
                # Released. A Failed row still holding a lease would read as
                # a live render to Retry, which refuses those — so the one
                # state the user can act on would refuse to let them.
                "leaseOwner": None,
                "leaseExpiresAtUtc": None,
                "updatedAtUtc": datetime.now(timezone.utc),
            }

            # What it had spent before it fell over. Recorded only when the
            # render got far enough to produce a ledger — writing an empty one
            # would overwrite the record of the attempt before it, which on a
            # Retry is the attempt that actually cost something.
            # Tested for *content*, not truthiness. `totals()` always returns a
            # populated dict — empty lists and a zero — so `if spent:` passes
            # for a ledger that recorded nothing, and on a Retry that would
            # overwrite the record of the attempt that actually spent money
            # with a blank one. Which is the precise failure this guard was
            # written to prevent.
            spent = job.get("_usage") or {}
            usage_write = _usage_write(spent, job.get("_usage_inc") or {}, "failed")
            if usage_write:
                log.info(
                    "Video %s failed having spent %s (unpriced: %s)",
                    video_id, spent.get("knownUsd"), spent.get("unpricedUnits"),
                )

            wrote = await col.update_one(
                held, _merge_update({"$set": failed_update}, usage_write)
            )
            if wrote.matched_count == 0:
                log.warning(
                    "Video %s: not recording this failure — the lease moved to another "
                    "worker while this render was running, so the row is no longer ours.",
                    video_id,
                )
            return True

        # Upload MP4 + thumb.
        video_blob_key = f"videos/{owner_id}/{video_id}.mp4"
        thumb_blob_key = f"videos/{owner_id}/{video_id}.jpg"
        # None until something is actually stored there.
        #
        # It was assigned up front, which meant a render with no narration file
        # — or one whose upload failed on a path that did not reset it — still
        # recorded a key. A key in this field is a promise that a blob exists,
        # and a refine that believes it gets a 404 instead of an honest "there
        # is no saved narration, regenerate it". The stills below already follow
        # this rule; the narration did not.
        audio_blob_key: str | None = None
        still_blob_keys: list[str] = []
        try:
            await asyncio.to_thread(
                storage.upload_file,
                Path(result["mp4_path"]), video_blob_key, "video/mp4",
            )
            if result.get("thumb_path"):
                await asyncio.to_thread(
                    storage.upload_file,
                    Path(result["thumb_path"]), thumb_blob_key, "image/jpeg",
                )

            # One still per moment, for the refine dialog's shot picker. A
            # still that fails to upload leaves that tile numbered, not the
            # video unfinished.
            stills = result.get("shot_thumbs") or {}
            for shot in result.get("shot_timeline") or []:
                moment = shot.get("segment")
                if moment not in stills:
                    continue
                key = shot_thumb_key(owner_id, str(video_id), moment)
                try:
                    await asyncio.to_thread(
                        storage.upload_file, Path(stills[moment]), key, "image/jpeg")
                    shot["thumbKey"] = key
                except Exception as exc:  # noqa: BLE001
                    log.warning("Video %s: shot %s still not kept (%s)", video_id, moment, exc)

            # The narration, kept so a refine does not have to buy it again.
            #
            # Roughly 200 KB for fifteen seconds, and on a premium voice it cost
            # real ElevenLabs money — the one artifact that is both small and
            # dear. Everything else a refine needs is either already in this
            # document (the script, the segment timings) or free to fetch again
            # (the stock footage, via the references recorded below).
            #
            # Under `videos/{ownerId}/{videoId}/`, so deleting the video takes it
            # too: the .NET side removes that whole prefix rather than the two
            # keys it happens to know the names of.
            narration = result.get("audio_path")
            if narration and Path(narration).exists():
                key = f"videos/{owner_id}/{video_id}/narration.mp3"
                try:
                    await asyncio.to_thread(
                        storage.upload_file, Path(narration), key, "audio/mpeg",
                    )
                    audio_blob_key = key
                except Exception as exc:  # noqa: BLE001
                    # Not fatal. The video is finished and good; losing the
                    # narration only means a later refine has to regenerate it.
                    # Failing the render over it would throw away the expensive
                    # part to protect the cheap one.
                    log.warning(
                        "Video %s: could not keep the narration (%s). A refine "
                        "will regenerate it.", video_id, exc,
                    )
            else:
                log.warning(
                    "Video %s: finished with no narration file to keep. A refine "
                    "will have to regenerate the voice.", video_id,
                )

            # AI-imagery jobs only, and empty for everything else. Same reason
            # as the narration: cheap to keep, and regenerating would change
            # what the video looks like rather than reproduce it.
            for i, still in enumerate(result.get("ai_still_paths") or []):
                sp = Path(still)
                if not sp.exists():
                    continue
                key = f"videos/{owner_id}/{video_id}/stills/{i:02d}{sp.suffix}"
                try:
                    await asyncio.to_thread(storage.upload_file, sp, key, "image/webp")
                    still_blob_keys.append(key)
                except Exception as exc:  # noqa: BLE001
                    log.warning(
                        "Video %s: could not keep still %d (%s). A refine will "
                        "regenerate the imagery.", video_id, i, exc,
                    )
        except Exception as exc:  # noqa: BLE001
            log.error("Upload failed for %s: %s", video_id, exc)
            wrote = await col.update_one(
                held,
                {"$set": {
                    "status": STATUS_FAILED,
                    # Was `f"Upload failed: {exc}"` — the raw exception, on the
                    # pharmacist's video card. Exactly the fault that put
                    # "expected string or bytes-like object, got 'list'" on
                    # somebody's screen; it survived that fix because only the
                    # render path was changed and this one looks like a
                    # different kind of failure. It is not: both are our
                    # internals, and both are read by somebody who runs a
                    # pharmacy. An Azure Blob error is no more actionable to
                    # them than a Python type error.
                    "note": _friendly_failure(exc),
                    "diagnostic": _technical_reason(exc),
                    "leaseOwner": None,
                    "leaseExpiresAtUtc": None,
                    "updatedAtUtc": datetime.now(timezone.utc),
                }},
            )
            if wrote.matched_count == 0:
                log.warning(
                    "Video %s: upload failed but the lease had already moved on; "
                    "leaving the row to whoever holds it.", video_id,
                )
            return True

        # Flip to Ready with all the fresh metadata.
        # Pictures the pharmacist attached that could not be used.
        #
        # The render succeeded — those beats fell back to stock — so `note`
        # stays empty and nothing is shown to the pharmacy. But "where did my
        # shopfront photo go?" is a real question with a technical answer, and
        # `diagnostic` is where technical answers live.
        image_warnings = [w for w in (result.get("user_image_warnings") or []) if w]

        update: dict[str, Any] = {
            "status": STATUS_READY,
            "note": None,
            # Cleared on success unless an attached picture was skipped, in
            # which case it carries that rather than a stale failure reason.
            "diagnostic": (
                "Attached images not used: " + "; ".join(image_warnings)
                if image_warnings else None
            ),
            "blobKey": video_blob_key,
            "thumbnailBlobKey": thumb_blob_key if result.get("thumb_path") else None,
            "title": result.get("title"),
            "script": result.get("script"),
            "generatedDescription": result.get("description"),
            "tags": result.get("tags") or [],
            "metadataJson": result.get("metadata_json"),
            "aiImageCostUsd": float(result.get("ai_image_cost_usd") or 0.0),
            "aiImagesGenerated": int(result.get("ai_images_generated") or 0),
            # What a refine resumes from. See the upload above: the narration is
            # kept as bytes, the stock footage only as references, because the
            # footage is free to fetch again and twelve HD clips is not small.
            "audioBlobKey": audio_blob_key,
            "clipSources": result.get("clip_sources") or [],
            # Which moment is on screen when, as rendered. What a refine
            # describes to the model, so "the last shot" means the shot the
            # pharmacist saw last — see `beats.fit_timeline`.
            "shotTimeline": result.get("shot_timeline") or [],
            "stillBlobKeys": still_blob_keys,
            "leaseOwner": None,
            "leaseExpiresAtUtc": None,
            "updatedAtUtc": datetime.now(timezone.utc),
        }
        # The same three operators the failure path writes, merged into the
        # Ready update so one guarded write records both the result and what it
        # cost. Two writes would leave a window where a video is Ready with the
        # previous attempt's usage still on it.
        wrote = await col.update_one(
            held,
            _merge_update(
                {"$set": update},
                # Read off the job, not the result — the same place the failure
                # path reads, because `_render_sync` stamps it in a `finally`
                # and that is the one route which works whether the render
                # returned or raised.
                _usage_write(
                    job.get("_usage") or {},
                    job.get("_usage_inc") or {},
                    "ready",
                ),
            ),
        )
        if wrote.matched_count == 0:
            # The expensive case, and the one worth a loud line: this render
            # finished, uploaded, and is being thrown away because another worker
            # owns the job now. Repeatedly seeing this means renders are
            # outliving LEASE_SECONDS rather than crashing, and the lease is too
            # short for the work.
            log.warning(
                "Video %s: render complete but DISCARDED — the lease moved to another "
                "worker mid-render. The blob was overwritten with identical output; the "
                "row stays with the current holder.", video_id,
            )
            return True
        log.info("Video %s → Ready", video_id)
        return True


# ---------------------------------------------------------------------------
# Enum coercion
#
# The .NET side stores these as C# enums, and the Mongo driver persists them as
# INTEGERS — not the string names the JSON API exposes. The worker previously
# passed the raw value straight into UserContext, where the pipeline calls
# .lower() on it.
#
# That failed silently in the worst possible way: value 0 is falsy, so the
# `or "ai"` fallback caught it and the default path appeared to work. Any
# non-default choice — a custom script, guided mode, AI imagery — crashed with
# "'int' object has no attribute 'lower'".
#
# These map ints and strings alike onto the canonical lowercase names the
# pipeline branches on, so the wire format can change without breaking again.
# ---------------------------------------------------------------------------

# Acme.Domain.Videos.VideoScriptSource
_SCRIPT_SOURCE_BY_INT = {0: "ai", 1: "guided", 2: "custom"}

# Acme.Domain.Videos.VideoVisualStyle
_VISUAL_STYLE_BY_INT = {0: "realistic", 1: "ai_imagery"}


def _coerce_script_source(raw: Any) -> str:
    if isinstance(raw, bool):  # bool is an int subclass — reject before the int check
        return "ai"
    if isinstance(raw, int):
        return _SCRIPT_SOURCE_BY_INT.get(raw, "ai")
    if isinstance(raw, str) and raw.strip():
        name = raw.strip().lower()
        return name if name in {"ai", "guided", "custom"} else "ai"
    return "ai"


def _coerce_visual_style(raw: Any) -> str:
    if isinstance(raw, bool):
        return "realistic"
    if isinstance(raw, int):
        return _VISUAL_STYLE_BY_INT.get(raw, "realistic")
    if isinstance(raw, str) and raw.strip():
        # Accepts "AiImagery" (the C# name), "ai-imagery", and "ai_imagery".
        name = raw.strip().lower().replace("-", "_")
        if name in {"aiimagery", "ai_imagery"}:
            return "ai_imagery"
        return "realistic"
    return "realistic"


PLATFORM_SETTINGS_COLLECTION = "platform_settings"
VIDEO_MODEL_SETTING_KEY = "video.anthropicModel"

# Mirrors VideoModels.Allowed in Acme.Domain/Platform/PlatformSetting.cs.
#
# Duplicated deliberately. The .NET side validates on write, so a bad value
# should never reach this collection — but this worker passes the string
# straight to Anthropic, and a value that slipped in another way (a manual
# edit, a restored backup from a newer version) would fail every video with an
# unhelpful error. Checking again here costs nothing and fails safe.
ALLOWED_VIDEO_MODELS = {
    "claude-haiku-4-5-20251001",
    "claude-sonnet-5",
    "claude-opus-5",
}

BUILT_IN_VIDEO_MODEL = "claude-haiku-4-5-20251001"


async def _resolve_video_model(db, doc: dict) -> str:
    """Decide which Anthropic model renders this job.

    Order, most specific first:

      1. ``model`` already on the job document — set explicitly for this video.
      2. The platform setting a SuperAdmin chose in the admin console.
      3. ``ANTHROPIC_MODEL`` from configuration.
      4. A built-in default.

    Never raises. A settings lookup that fails must not take down video
    rendering for everyone — the whole point of the fallback chain is that each
    step is optional.
    """
    explicit = str(doc.get("model") or "").strip()
    if explicit:
        return explicit

    try:
        row = await db[PLATFORM_SETTINGS_COLLECTION].find_one(
            {"_id": VIDEO_MODEL_SETTING_KEY}
        )
        value = str((row or {}).get("value") or "").strip()
        if value:
            if value in ALLOWED_VIDEO_MODELS:
                return value
            log.warning(
                "Platform video model %r is not in the allowed list; ignoring it.",
                value,
            )
    except Exception as exc:  # noqa: BLE001
        log.warning(
            "Could not read the video model setting (%s). Falling back to configuration.",
            exc,
        )

    # settings.anthropic_model, not a literal. This line was previously a
    # hardcoded string, which meant ANTHROPIC_MODEL was silently ignored.
    configured = str(get_settings().anthropic_model or "").strip()
    return configured or BUILT_IN_VIDEO_MODEL


#: Attempts kept in full on a video. Beyond this the oldest are dropped, which
#: is why the running totals in `usageTotals` are incremented separately — they
#: survive the truncation.
MAX_USAGE_ATTEMPTS = 20


def _usage_write(totals: dict, increments: dict, outcome: str) -> dict[str, Any]:
    """The Mongo operators recording one attempt's usage.

    Three things, because they answer three different questions:

    ``$set usage``
        The most recent attempt. What the last run of this video did.
    ``$push usageAttempts``
        Each attempt in full, capped at the most recent twenty. What happened,
        attempt by attempt.
    ``$inc usageTotals``
        Running sums of every unit and of the priced money, never truncated.
        What this video has cost us in total, which is the number the refine
        charge policy is waiting on — and the one a retried video used to
        understate, because `usage` alone reports only the final attempt.

    Returns ``{}`` when the attempt measured nothing, so a render that died
    before spending anything cannot overwrite the record of one that did.
    """
    if not totals or not (totals.get("lines") or totals.get("stageSeconds")):
        return {}

    attempt = dict(totals)
    attempt["at"] = datetime.now(timezone.utc)
    # "ready" or "failed". A cost is read very differently depending on whether
    # anything came of it, and an attempts array that does not say is a list of
    # numbers nobody can interpret.
    attempt["outcome"] = outcome

    write: dict[str, Any] = {
        "$set": {"usage": totals},
        "$push": {
            "usageAttempts": {"$each": [attempt], "$slice": -MAX_USAGE_ATTEMPTS}
        },
    }

    if increments:
        write["$inc"] = increments
    return write


def _has_repeated_caption(state: dict) -> bool:
    """True when this video's script would show some caption twice."""
    from .nodes import repeated_captions

    segments = (state.get("metadata") or {}).get("segments") or []
    return bool(repeated_captions(segments))


def _refusal_outcome(job: dict, reason: str) -> dict[str, Any]:
    """Status, note and pictures for a refine that was refused before it ran.

    Back to Ready when there is a finished video to go back to — Failed would
    take a working video away because we could not re-fetch a stock clip. A
    *failed* video carries its confirmed refine through Retry and has no file
    behind it, so it stays Failed rather than showing a play button attached to
    nothing. Pictures this change appended come off again: it did not happen,
    so they were never shown.
    """
    finished = bool(job.get("blobKey"))
    return {
        "status": STATUS_READY if finished else STATUS_FAILED,
        "note": reason,
        **(_undo_added_pictures(job) if finished else {}),
    }


def _failure_outcome(job: dict, exc: BaseException) -> dict[str, Any]:
    """Status, note and pictures for a render that fell over.

    A change to a finished video that crashed leaves the video as it was:
    Ready, its file untouched, a sentence saying the change was not made, and
    any pictures the change added taken back off. Only a first render, or a
    retry of one, is Failed — that is the case with nothing to show.
    """
    refine_of_finished = bool(
        (job.get("refine") or {}).get("confirmed") and job.get("blobKey"))
    if refine_of_finished:
        return {"status": STATUS_READY, "note": _refine_failure(exc),
                **_undo_added_pictures(job)}
    return {"status": STATUS_FAILED, "note": _friendly_failure(exc)}


def _refine_failure(exc: BaseException) -> str:
    """What to show when a change to a finished video fell over.

    Not `_friendly_failure`: that one says "try again — it won't use another
    video from your allowance", which points at a Retry button a Ready video
    does not show and answers a question a refine never raises. The card
    already begins "Last change not made:".
    """
    timeout_ish = isinstance(exc, TimeoutError) or "timeout" in str(exc).lower()
    cause = "It took longer than expected" if timeout_ish else "Something went wrong"
    return f"{cause} while making it. Your video is as it was — ask for the change again."


def _undo_added_pictures(job: dict) -> dict[str, Any]:
    """`{"images": …}` without the pictures this change appended, or nothing.

    See `RefinePlan.AddedPictures` on the .NET side: pictures are appended when
    an add-pictures change is queued, so a change that then does not happen
    must take them off again, or they are planned onto the video by every later
    refine as though it showed them.
    """
    refine = job.get("refine") or {}
    count = refine.get("addedPictures")
    images = job.get("images")
    if (not refine.get("pinsOwnPictures") or isinstance(count, bool)
            or not isinstance(count, int) or count <= 0
            or not isinstance(images, list) or count > len(images)):
        return {}
    return {"images": images[:-count]}


def _cannot(parts: list[str]) -> str:
    """The parts of a request that cannot be done here, as a sentence.

    The model describes them as noun phrases ("removing duplicate captions"),
    and pasting those after "We cannot" produced "We cannot removing duplicate
    captions from here." — which is what a pharmacist actually read.
    """
    parts = [p.strip().rstrip(".") for p in parts if str(p).strip()]
    if not parts:
        return ""
    return " Not possible here: " + "; ".join(parts) + "."


def _merge_update(base: dict[str, Any], extra: dict[str, Any]) -> dict[str, Any]:
    """Combine two Mongo update documents without losing either's operators.

    `{"$set": {...}}` updated with `{"$set": {...}, "$push": {...}}` by a plain
    `dict.update` replaces the first `$set` wholesale — dropping the status, the
    note and the lease release, which is every field that matters. Merged per
    operator instead.
    """
    merged: dict[str, Any] = {}
    for source in (base, extra):
        for operator, fields in source.items():
            merged.setdefault(operator, {}).update(fields)
    return merged


def _persistable_facts(raw: Any) -> list[dict]:
    """The research facts, in a shape that is safe to store.

    `facts` comes from a model reply parsed as JSON, so its type is whatever the
    model produced. This runs in the write that flips a finished video to Ready
    — after the research, the script, the narration, the footage and the encode
    — and `(raw or [])[:20]` raises `TypeError` on a dict. **A malformed reply
    would therefore have destroyed a completed render at the last step**, and
    the pharmacist would have seen a failure with nothing to show for it.

    Filtered to dicts to match how `rehydrate` reads them back, so anything
    stored is something a later refine can actually use.
    """
    if not isinstance(raw, list):
        if raw:
            log.warning(
                "Research facts came back as %s rather than a list; storing none.",
                type(raw).__name__,
            )
        return []
    return [f for f in raw if isinstance(f, dict)][:MAX_PERSISTED_FACTS]


def _brand_from(doc: dict) -> BrandContext | None:
    """The pharmacy identity stamped on this job, or None to render unbranded.

    A field read, deliberately. The precedence between the pharmacy profile and
    the brand kit is decided once in C# (``PharmacyBrand.Resolve``) and written
    onto the document; re-deciding it here would put the same rule in two
    languages, and the two would drift the first time either changed.

    Returns None in three cases, all of which render exactly as before branding
    existed:

      * ``brand`` absent — every job queued before this shipped.
      * ``usable`` false — the pharmacy has filled in neither record, so the
        only name available is the literal word "Pharmacy". Stamping that across
        somebody's video is worse than leaving it clean.
      * ``brand`` present but not a document — defensive, and cheap.
    """
    raw = doc.get("brand")
    if not isinstance(raw, dict):
        return None
    if not raw.get("usable"):
        return None

    def text(key: str) -> str:
        value = raw.get(key)
        return value.strip() if isinstance(value, str) else ""

    def optional(key: str) -> str | None:
        return text(key) or None

    name = text("name")
    if not name:
        # usable said a name was found and there isn't one. Trust what is
        # actually here over what the flag claims, and say so — this means the
        # two sides disagree, which is worth someone looking at.
        log.warning(
            "Video %s: brand is marked usable but carries no name. Rendering "
            "unbranded.", doc.get("_id"),
        )
        return None

    return BrandContext(
        name=name,
        address=text("address"),
        phone=text("phone"),
        booking_url=optional("bookingUrl"),
        logo_blob_key=optional("logoBlobKey"),
        primary_color=optional("primaryColor"),
        secondary_color=optional("secondaryColor"),
        accent_color=optional("accentColor"),
        license_number=text("licenseNumber"),
        regulatory_footer=text("regulatoryFooter"),
        usable=True,
    )



def _friendly_failure(exc: BaseException) -> str:
    """What to show a pharmacist when a render falls over.

    ``note`` is rendered verbatim on the video card, so whatever goes in here is
    read by somebody who runs a pharmacy. It was ``str(exc)[:400]``, which put
    "expected string or bytes-like object, got 'list'" on their screen — a
    Python type error, about our code, presented as if it were their problem.

    The technical detail is not lost: the full exception and traceback are
    logged one line above this, which is where somebody debugging should be
    looking anyway. What changes is that the customer gets a sentence they can
    act on instead of one they have to ignore.

    Deliberately does not apologise for something the pharmacist may not even
    have noticed, and does not promise a cause we have not established. It says
    what happened, what it cost them (nothing), and what to do.
    """
    timeout_ish = isinstance(exc, TimeoutError) or "timeout" in str(exc).lower()
    if timeout_ish:
        return (
            "This took longer than expected and stopped part-way. "
            "Try again — it won't use another video from your allowance."
        )
    return (
        "Something went wrong while making this video. "
        "Try again — it won't use another video from your allowance."
    )


#: Anything that looks like a filesystem path in an exception message.
_PATH_RE = re.compile(r"(/[\w.\-]+){2,}")


def _technical_reason(exc: BaseException) -> str:
    """The operator's version of the same failure, short enough to store.

    <b>Why this is persisted and not merely logged.</b> ``note`` is deliberately
    a sentence for a pharmacist, which means that when a render fails in
    production the only record of *why* lives in a log stream — so diagnosing it
    requires access to the host, and `pnpm smoke` can report that something
    broke but never what. That cost a full round trip on the Edge-TTS failure:
    the smoke said "video failed with a human message", and the actual reason
    ("Edge-TTS is not served to datacentre networks") was three clicks away in
    Railway.

    <b>Why it is a reason and not a traceback.</b> This field is readable by the
    video's owner through the API, so it carries the exception type and message
    and nothing else — no stack frames, no file paths. The full traceback stays
    in the log where it belongs. The chain is followed because the useful reason
    is usually the ``__cause__``: ``NonRetriablePipelineError`` wraps the
    provider error that actually explains the failure.
    """
    parts: list[str] = []
    seen: set[int] = set()
    current: BaseException | None = exc

    while current is not None and id(current) not in seen and len(parts) < 4:
        seen.add(id(current))
        message = " ".join(str(current).split())
        parts.append(f"{type(current).__name__}: {message}" if message else type(current).__name__)
        current = current.__cause__ or current.__context__

    return _PATH_RE.sub("<path>", " ← ".join(parts))[:600]


def _render_sync(doc: dict) -> dict:
    """Blocking render — runs on a thread pool. Returns the paths and
    metadata the worker needs to upload + persist."""
    import json

    ctx = UserContext(
        user_id=doc["ownerId"],
        job_id=str(doc["_id"]),
        topic=(doc.get("topic") or doc.get("prompt") or doc.get("name") or "pharmacy tips").strip(),
        style_tone=doc.get("styleTone") or "friendly, informative, community-focused",
        # Already resolved by _resolve_video_model before the job reached this
        # thread. The fallback is belt and braces for a direct call in a test.
        model=(doc.get("model") or BUILT_IN_VIDEO_MODEL),
        voice_id=doc.get("voiceId") or "en-US-JennyNeural",
        video_mode="shorts",
        duration_seconds=int(doc.get("duration") or 15),
        watermark=bool(doc.get("watermark") or False),
        script_source=_coerce_script_source(doc.get("scriptSource")),
        description=doc.get("description"),
        custom_script=doc.get("customScript"),
        visual_style=_coerce_visual_style(doc.get("visualStyle")),
        # The pharmacy's own pictures, chosen when the video was queued.
        # `.get()` with a default, so a row written before this field existed is
        # simply a video with none.
        user_images=[i for i in (doc.get("images") or []) if isinstance(i, dict)],
        image_style=(doc.get("imageStyle") or "medical"),
        brand=_brand_from(doc),
        # Set by _prepare_refine on the refine lane, absent on a first render.
        # Without it an editor refine re-cuts identical inputs and hands back an
        # identical video while reporting success.
        editor_options=doc.get("_options"),
        shot_edits=doc.get("_shot_edits") or {},
        user_pins=doc.get("_user_pins"),
        # Continued from _prepare_refine when this is a refine, so the cost of
        # interpreting the instruction is counted against the same video as the
        # render it produced. A fresh ledger here would drop it.
        usage=doc.get("_ledger") or usage_mod.UsageLedger(),
    )

    log.info(
        "Rendering %s: script_source=%s visual_style=%s custom_script=%s brand=%s",
        doc["_id"], ctx.script_source, ctx.visual_style,
        "yes" if ctx.custom_script else "no",
        # Named rather than a boolean: "unbranded" on a pharmacy that expected
        # its name on screen is the report that explains a support ticket.
        ctx.brand.name if ctx.brand else "unbranded",
    )

    # Wire pipeline progress → same Mongo doc, throttled at 1/sec.
    _last: dict[str, Any] = {"pct": -1, "at": 0.0}

    # Set once, when a renewal finds the row is no longer ours.
    _lease: dict[str, bool] = {"lost": False}

    # One client for the whole render, not one per report.
    #
    # This used to open a MongoClient, write, and close it on every progress
    # tick — a TCP connect, a TLS handshake and topology discovery, roughly once
    # a second for the length of the render. Tolerable when it was only moving a
    # percentage along a bar; not tolerable now, because the same write is the
    # lease heartbeat, and making the heartbeat the most expensive thing in the
    # loop is how it starts failing under load and stranding healthy jobs.
    _client: dict[str, Any] = {}

    def _progress_col():
        if "col" not in _client:
            from pymongo import MongoClient
            s = get_settings()
            mc = MongoClient(s.mongo_connection_string)
            _client["mc"] = mc
            _client["col"] = mc[s.mongo_database][COLLECTION]
        return _client["col"]

    def progress(stage: str, pct: int, extra: dict | None = None) -> None:
        import time
        # Called from the render thread — cheap sync Mongo update via pymongo.
        now = time.time()
        stage_changed = stage != _last.get("stage")
        # Log stage transitions to stdout so `docker compose logs` shows
        # pipeline progress even when Mongo writes are throttled.
        if stage_changed:
            log.info("Video %s → stage=%s pct=%d%s",
                     doc["_id"], stage, pct,
                     f" extra={extra}" if extra else "")
        if pct <= _last["pct"] and stage == _last.get("stage"):
            return
        if now - _last["at"] < 1.0 and pct != 100 and not stage_changed:
            return
        _last.update({"pct": pct, "at": now, "stage": stage})

        if _lease.get("lost"):
            # Already found out we no longer hold this job. Keep rendering
            # rather than abandoning work in flight, but stop writing: renewing
            # a lease we do not own would take the job back off the worker that
            # legitimately reclaimed it, and then both would render and both
            # would keep renewing.
            return

        try:
            stamp = datetime.now(timezone.utc)
            renewed = _progress_col().update_one(
                {"_id": doc["_id"], "leaseOwner": WORKER_ID},
                {"$set": {
                    "note": f"{stage.capitalize()}… {pct}%",
                    # The lease is renewed from evidence of progress, which is
                    # what this callback is. Setting it once at claim time and
                    # hoping would mean either a lease too short for a slow
                    # stage — another worker reclaims a healthy job and the
                    # imagery is paid for twice — or one so long that a crashed
                    # render sits unrecoverable for an hour.
                    #
                    # Throttled to roughly 1/sec by the guards above, so this
                    # costs nothing beyond the write already happening.
                    "leaseExpiresAtUtc": stamp + timedelta(seconds=LEASE_SECONDS),
                    "updatedAtUtc": stamp,
                }},
            )
            if renewed.matched_count == 0:
                _lease["lost"] = True
                log.warning(
                    "Video %s: lost the lease mid-render — another worker has claimed "
                    "it. This render will finish and be discarded. If this recurs, the "
                    "render is outliving the %ds lease rather than crashing, and the "
                    "lease is too short for the work.",
                    doc["_id"], LEASE_SECONDS,
                )
        except Exception as exc:  # noqa: BLE001
            # Not fatal on its own: the render carries on and the next report
            # renews. Ten minutes of these in a row means the lease lapses under
            # a job that is still running and a second worker picks it up — so it
            # is a warning worth reading, not noise.
            log.warning("progress mongo write failed: %s", exc)

    try:
        # `_entry` and `_state` are put on the job by the claim path when this
        # is a refine. Absent on a first render, which then starts where it
        # always did.
        entry = doc.get("_entry")
        seeded = doc.get("_state") or {}

        graph = build_graph(ctx, progress=progress, entry=entry)
        # Timed under the name of the stage it started at, so "how long does an
        # editor-only re-cut take" is answerable directly rather than inferred
        # from two timestamps on a document.
        with ctx.usage.stage(entry or "full"):
            final = graph.invoke(
                {"topic": ctx.topic, "research_retries": 0, **seeded}
            )
    finally:
        # Handed back through the job dict, which the caller still holds a
        # reference to, so it survives this function raising.
        #
        # A render that fails part-way has still paid for everything it did
        # before it failed — the research, the script, quite possibly the
        # narration. That is the render whose cost is *most* worth knowing, and
        # it is the reason the ledger lives on the context rather than in graph
        # state, which the exception would have discarded. Recording it only on
        # success would have made that reasoning decorative.
        try:
            doc["_usage"] = ctx.usage.totals()
            doc["_usage_inc"] = ctx.usage.increments()
        except Exception:  # noqa: BLE001
            pass

        # Closed on the failure path too. A render that throws is the common
        # case this worker is built around, and leaking a connection pool on
        # every one of them exhausts the server's connection limit long before
        # anybody connects it back to video rendering.
        mc = _client.pop("mc", None)
        _client.pop("col", None)
        if mc is not None:
            try:
                mc.close()
            except Exception:  # noqa: BLE001
                pass

    mp4_path = final.get("final_video_path")
    if not mp4_path:
        raise RuntimeError("Editor produced no final video path")

    # Extract a thumbnail. Best-effort — the card falls back to a placeholder
    # if this fails.
    thumb_path = Path(mp4_path).parent / "thumb.jpg"
    if not _make_thumbnail(Path(mp4_path), thumb_path):
        thumb_path = None  # type: ignore[assignment]

    shot_thumbs = _shot_thumbnails(Path(mp4_path), final.get("shot_timeline") or [])

    return {
        "mp4_path": mp4_path,
        "thumb_path": str(thumb_path) if thumb_path else None,
        # What a refine needs to pick up without redoing the expensive parts.
        #
        # The narration, because regenerating it costs ElevenLabs money on a
        # premium voice and is the one artifact that is both small and dear.
        # The clip references rather than the clips, because stock footage is
        # public and free to fetch again and the bytes are not small. The script
        # and segment timings are already written to Mongo below.
        "audio_path": final.get("audio_path"),
        # Named explicitly, because this dict is a selection rather than the
        # whole final state — a key the visuals node sets and this list forgets
        # is a key that silently never arrives.
        "user_image_warnings": final.get("user_image_warnings") or [],
        "clip_sources": final.get("clip_sources") or [],
        "shot_timeline": final.get("shot_timeline") or [],
        "shot_thumbs": shot_thumbs,
        "ai_still_paths": final.get("ai_still_paths") or [],
        "title": final.get("title"),
        "script": final.get("script"),
        "description": final.get("description"),
        "tags": final.get("tags") or [],
        "metadata_json": json.dumps(final.get("metadata") or {}, default=str),
        # The sources the script was written from.
        #
        # Kept so a refine that rewrites the wording does not also have to
        # re-research the subject — which would cost a Tavily round and, worse,
        # could come back with different facts, quietly changing what the video
        # claims when somebody only asked for a friendlier tone.
        #
        # Capped: seven is what the researcher is asked for, and a runaway reply
        # must not push a video document toward Mongo's 16 MB limit.
        "facts": _persistable_facts(final.get("facts")),
        "ai_image_cost_usd": final.get("ai_image_cost_usd") or 0.0,

        "ai_images_generated": final.get("ai_images_generated") or 0,
    }


def _shot_thumbnails(video: Path, timeline: list) -> dict[int, str]:
    """A still from the middle of each moment's shot: ``{moment: jpg path}``.

    What the refine dialog shows as tiles, so the pharmacist picks the shot by
    looking at it. Taken from the finished video, not the source clip, so the
    tile is exactly what was on screen — cropped, framed, with the photo's zoom.
    Best-effort per shot: a moment without a still gets a numbered tile.
    """
    stills: dict[int, str] = {}
    folder = video.parent / "shots"
    folder.mkdir(parents=True, exist_ok=True)
    for shot in timeline if isinstance(timeline, list) else []:
        moment = shot.get("segment") if isinstance(shot, dict) else None
        if not isinstance(moment, int) or isinstance(moment, bool):
            continue
        try:
            middle = (float(shot["start"]) + float(shot["end"])) / 2
        except (KeyError, TypeError, ValueError):
            continue
        out = folder / f"{moment:02d}.jpg"
        if _make_thumbnail(video, out, middle, width=240, first_frame_fallback=False):
            stills[moment] = str(out)
    return stills


def shot_thumb_key(owner_id: str, video_id: str, moment: int) -> str:
    """Under the video's own prefix, so deleting the video deletes these too."""
    return f"videos/{owner_id}/{video_id}/shots/{moment:02d}.jpg"


def _make_thumbnail(
    video: Path, out: Path, at_seconds: float = 1.0, *,
    width: int = 640, first_frame_fallback: bool = True,
) -> bool:
    """Grab a single frame as the poster image.

    Runs immediately after the main encode, which is the worst possible
    moment for memory: MoviePy's clip objects are still reachable and the
    just-finished x264 encode has the allocator at high water. ffmpeg
    exiting 245 (-11 / EAGAIN) is what that looks like — it couldn't get
    the memory or thread it wanted, not a problem with the video itself.

    So: collect garbage first, keep ffmpeg single-threaded and tiny, and
    fall back to frame 0 if seeking to `at_seconds` fails. A missing
    thumbnail only costs us the card poster and the email hero image, so
    this stays best-effort — never raise into the caller.
    """
    import gc

    gc.collect()

    base = ["ffmpeg", "-y", "-loglevel", "error", "-threads", "1"]
    tail = [
        "-frames:v", "1",
        "-q:v", "5",
        "-vf", f"scale='min({int(width)},iw)':-2",
        str(out),
    ]
    # Seeking a second in usually gives a more representative frame than
    # frame 0 (which is often mid-fade), but frame 0 always exists.
    attempts = [base + ["-ss", str(at_seconds), "-i", str(video)] + tail]
    if first_frame_fallback:
        # Not for a shot's own still: frame 0 is a different shot, and a tile
        # showing the wrong picture is worse than a numbered placeholder.
        attempts.append(base + ["-i", str(video)] + tail)

    for idx, cmd in enumerate(attempts):
        try:
            subprocess.run(cmd, check=True, capture_output=True, timeout=60)
            if out.exists() and out.stat().st_size > 0:
                if idx:
                    log.info("Thumbnail extracted on fallback attempt (frame 0)")
                return True
            log.warning("Thumbnail attempt %d produced an empty file", idx + 1)
        except subprocess.CalledProcessError as exc:
            # stderr is where ffmpeg says what actually went wrong — without
            # this the previous version just reported the exit code.
            stderr = (exc.stderr or b"").decode("utf-8", "replace").strip()
            log.warning(
                "Thumbnail attempt %d failed (exit %s): %s",
                idx + 1, exc.returncode, stderr[:400] or "(no stderr)",
            )
        except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as exc:
            log.warning("Thumbnail attempt %d failed: %s", idx + 1, exc)

    log.warning("Thumbnail extraction gave up for %s", video.name)
    return False


worker = VideoWorker()


def _key_fingerprint(key: str | None) -> str:
    """Enough to tell two keys apart in a log, not enough to use one.

    "Works locally, 401 on Railway" is almost always a different key, not a
    broken one. Compare this line with the same from `docker compose logs
    ai-service`: the shape and last four characters differ when the keys do.
    """
    if not key:
        return "MISSING"
    shape = "sk-ant-api" if key.startswith("sk-ant-api") else "UNEXPECTED-PREFIX"
    return f"set({shape}…{key[-4:]}, {len(key)} chars)"
