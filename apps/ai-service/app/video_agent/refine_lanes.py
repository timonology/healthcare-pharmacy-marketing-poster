"""Two queues over one collection, so a three-second job is not stuck behind a minute.

The worker renders one job at a time per instance. That was fine when every job
was a full render, and stops being fine the moment some of them take seconds: a
pharmacist who nudges a caption and waits ninety seconds behind somebody else's
first render has been given a feature that feels broken.

So there are two claim loops with their own concurrency, and a job belongs to
exactly one of them.

**The filters must be disjoint, and that is the whole of the correctness
argument here.** Two lanes whose filters overlap would both try to claim the
same row. The claim itself is a `find_one_and_update`, so only one can win and
nothing is corrupted — but the loser wastes a poll and, worse, a job could land
in the lane not sized for it, which is the exact problem this file exists to
solve. The disjointness rests on one MongoDB behaviour, stated here because it
is easy to get wrong:

    `{"refine.confirmed": {"$ne": True}}` matches documents where `refine` is
    absent, where it is null, and where `confirmed` is false — because `$ne`
    matches missing fields.

So "has a confirmed refine" and "does not" partition the collection with no gap
and no overlap, and every row is claimable by exactly one lane.
"""
from __future__ import annotations

from typing import Any

from .stages import FREE_STAGES as REFINE_FREE_STAGES
from .stages import STAGES as REFINE_STAGES

#: Lane names. Short because they appear in every log line the worker writes.
LANE_REFINE = "refine"
LANE_RENDER = "render"

LANES = (LANE_REFINE, LANE_RENDER)


def lane_filter(lane: str) -> dict[str, Any]:
    """The clause that decides whether a row belongs to `lane`.

    Combined with the claimability filter (Pending, or Rendering with a lapsed
    lease) by the caller — this part only answers "whose job is it?".
    """
    if lane == LANE_REFINE:
        # Every confirmed refine, whatever stage it names — including stages
        # that are not open. The refine lane refuses those with a reason, which
        # is better than the alternative: excluded here, they would match
        # neither lane and sit Pending for ever with nobody looking.
        return {"refine.confirmed": True}

    if lane == LANE_RENDER:
        return {"refine.confirmed": {"$ne": True}}

    raise ValueError(f"{lane!r} is not a lane. Expected one of {', '.join(LANES)}.")
