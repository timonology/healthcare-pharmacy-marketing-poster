"""Graph state schema shared across all nodes."""
from __future__ import annotations

from typing import TypedDict


class AgentState(TypedDict, total=False):
    """Every key a node, the worker or `rehydrate` passes through the graph.

    **A key missing here is silently dropped.** LangGraph keeps a channel per
    declared key only: an undeclared key in the input never reaches a node, and
    one a node returns never reaches the final state. Nothing raises. That is
    how `shot_timeline` (the shot picker's tiles and stills) and
    `previous_aligned` / `previous_sources` (the shots a refine keeps) were
    written, read, unit-tested node by node — and lost in every real render.
    `tests/test_state_schema.py` now fails on any key used but not declared.
    """

    job_id: str
    topic: str

    # Researcher
    facts: list[dict]
    research_retries: int

    # Scriptwriter
    script: str
    title: str
    description: str
    tags: list[str]
    metadata: dict

    # Voice
    audio_path: str

    # Visuals
    video_clips: list[str]
    aligned_clips: list[str]

    # Attached pictures that could not be used, in words an operator can act on.
    # Set by the visuals stage; read by the worker when it marks the video Ready.
    user_image_warnings: list[str]
    #: Words actually handed to the synthesiser. Lets the editor say whether an
    #: over-long clip is a writer over budget or a wrong words-per-minute
    #: constant — the duration alone cannot tell them apart.
    spoken_words: int
    extra_clips: list[str]

    # Where each clip came from: {path, pexels_id, link, query}.
    #
    # Kept so a refine can re-fetch the same footage rather than us storing it.
    # Twelve HD stock clips is roughly 120 MB a video; the references are a
    # couple of kilobytes, and the footage is public and free to download again.
    clip_sources: list[dict]

    # AI imagery bookkeeping
    ai_image_cost_usd: float
    ai_images_generated: int

    # The generated stills, kept so a refine reuses the same pictures.
    # Flux is stochastic: regenerating returns different images, so a caption
    # fix would otherwise come back looking like a different video.
    ai_still_paths: list[str]

    # Refine: the shots to keep, one per moment ("" = search this moment
    # again), and the stock records they came from. Seeded by `rehydrate`,
    # read by the visuals node. Undeclared, every refine re-searched every shot.
    previous_aligned: list[str]
    previous_sources: list[dict | None]

    # Editor
    final_video_path: str

    # What was actually on screen, moment by moment: {segment, start, end}.
    # Written by the editor; the worker takes a still of each shot from it and
    # stores it as the video's `shotTimeline` for the shot picker.
    shot_timeline: list[dict]
