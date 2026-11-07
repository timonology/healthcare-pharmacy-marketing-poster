"""The render pipeline's stages. One definition, imported everywhere.

There are two sides to this list — `RefineStage` in C#, and this — because the
API decides what a refine may do and the worker carries it out. Two is the
minimum, and it is already one more than anyone would like: `RefineStageContractTests`
reads this file and fails if the two drift.

**There were briefly four.** The same five names were written out in this
module's callers — the graph's entry points, the classifier's prompt vocabulary,
the lane filter — each correct, each independently editable, and only one of
them covered by the contract test. That is the fault shape behind most of what
has gone wrong in this codebase: a rule written out by hand in several places,
drifting. So the list lives here and the callers import it.

Order is meaningful. Each stage depends on the output of the one before, so
"earlier" means "redoes more" and a refine re-enters at the earliest stage its
change actually touches.
"""
from __future__ import annotations

#: Pipeline order. Index matches the .NET RefineStage enum's value.
STAGES: tuple[str, ...] = (
    "researcher",
    "scriptwriter",
    "voice",
    "visuals",
    "editor",
)

#: Stages that call nobody and cost nothing. The editor is ffmpeg on our own
#: CPU; the rest reach Tavily, Anthropic, ElevenLabs or Replicate.
#:
#: Advisory on this side. `RefineStages` in the domain is the authority on what
#: a stage costs and whether a pharmacist may reach it — this copy exists so the
#: worker can refuse an obviously wrong instruction without a round trip.
FREE_STAGES: frozenset[str] = frozenset({"editor"})

#: The stage a refine restarts at when nothing earlier is needed.
CHEAPEST = "editor"

#: Longest change request we will try to route.
#:
#: Above this an instruction is almost always several requests at once, and
#: routing the lot to one stage satisfies part of it and silently drops the
#: rest. Truncating would do the same thing less visibly.
#:
#: Must match `RefinePlan.MaxInstructionLength`, or one side accepts what the
#: other will not act on and the pharmacist gets a shrug instead of a sentence.
MAX_INSTRUCTION_CHARS = 1000


def is_free(stage: str) -> bool:
    return stage in FREE_STAGES
