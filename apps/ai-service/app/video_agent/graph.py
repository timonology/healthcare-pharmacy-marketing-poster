"""Build the LangGraph for a specific UserContext.

Marketing25 skips the uploader node — the .NET API surfaces the video
via signed blob URL and the pharmacy user downloads/shares it themselves.
"""
from __future__ import annotations

from typing import Any

from langgraph.graph import END, StateGraph

from .context import UserContext
from .nodes import (
    make_editor,
    make_researcher,
    make_scriptwriter,
    make_visuals,
    make_voice,
    research_quality_score,
)
from .stages import STAGES
from .state import AgentState

RESEARCH_MIN_FACTS = 5
RESEARCH_MIN_SCORE = 0.55
MAX_RESEARCH_RETRIES = 2


def _gate(state: dict) -> str:
    facts = state.get("facts", [])
    retries = state.get("research_retries", 0)
    score = research_quality_score(state)
    if len(facts) >= RESEARCH_MIN_FACTS and score >= RESEARCH_MIN_SCORE:
        return "accept"
    if retries >= MAX_RESEARCH_RETRIES:
        return "accept"
    return "retry"


#: Stages a refine may re-enter at, in pipeline order.
#:
#: Imported rather than restated. This was a fourth hand-written copy of the
#: same five names — see stages.py.
ENTRY_STAGES = STAGES


def build_graph(ctx: UserContext, progress=None, entry: str | None = None) -> Any:
    """Compile the pipeline, optionally starting part-way along it.

    `entry` is set only by a refine. It is the whole of what makes refinement
    affordable: re-entering at the editor re-cuts the saved narration and
    footage in seconds and calls nobody, where a full run pays Tavily,
    Anthropic, ElevenLabs and Replicate for a change ffmpeg could have made.

    The caller is responsible for seeding the state the skipped stages would
    have produced — see `rehydrate`. A graph entered at the editor with no
    `audio_path` fails inside moviepy, which is a confusing place to learn that
    a narration blob was missing.
    """
    if entry is not None and entry not in ENTRY_STAGES:
        # Refused rather than defaulted. Falling back to a full run would turn a
        # typo into a bill; falling back to the editor would re-cut identical
        # inputs and report success.
        raise ValueError(
            f"{entry!r} is not a pipeline stage. Expected one of "
            f"{', '.join(ENTRY_STAGES)}."
        )

    # Where the run actually begins. A custom script is its own kind of
    # re-entry — the narration is supplied, so there is nothing to research —
    # and it resolves to the same thing a refine asks for explicitly.
    start = entry
    if start is None:
        start = (
            "scriptwriter"
            if (ctx.script_source or "ai").lower() == "custom" and ctx.custom_script
            else "researcher"
        )

    # Only the stages from `start` onward are added.
    #
    # The alternative — add all five and point the entry at one of them — leaves
    # the earlier nodes with no outgoing edges. LangGraph is documented to
    # reject a node it considers a dead end, and building only what runs means
    # never having to know whether it does. It also means the compiled graph is
    # exactly the work that will happen, which is the thing being reasoned about
    # when the question is "what will this refine cost?".
    builders = {
        "researcher": make_researcher,
        "scriptwriter": make_scriptwriter,
        "voice": make_voice,
        "visuals": make_visuals,
        "editor": make_editor,
    }
    running = ENTRY_STAGES[ENTRY_STAGES.index(start):]

    g = StateGraph(AgentState)
    for stage in running:
        g.add_node(stage, builders[stage](ctx, progress))

    g.set_entry_point(start)

    if start == "researcher":
        # The gate retries thin research before letting the writer near it.
        # Only meaningful when research is actually running.
        g.add_conditional_edges(
            "researcher", _gate,
            {"accept": "scriptwriter", "retry": "researcher"},
        )

    for earlier, later in zip(running, running[1:]):
        if earlier != "researcher":
            g.add_edge(earlier, later)

    g.add_edge("editor", END)
    return g.compile()
