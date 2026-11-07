"""Two messages per failure: one for the pharmacist, one for whoever fixes it.

A render that falls over has to say two different things to two different
people, and this project has now got each of them wrong in turn:

* the pharmacist was once shown ``TypeError: expected string or bytes-like
  object, got 'list'`` — our Python, presented as their problem;
* then the technical reason was removed from the record entirely and left only
  in the log, so a production failure could be *reported* by `pnpm smoke` but
  not *diagnosed* without access to the host. That cost a full round trip on the
  Edge-TTS outage: "video failed with a human message", and the actual reason
  was three clicks away in Railway.

So there are two fields. These tests pin what belongs in each, and in
particular that the stored diagnostic follows the exception *chain* — because
the useful reason is almost always the ``__cause__``. ``NonRetriablePipelineError``
is a wrapper; the provider error underneath it is the answer.
"""

from __future__ import annotations

import re

from app.video_agent.worker import _friendly_failure, _technical_reason


class ElevenLabsError(RuntimeError):
    """Stands in for the real one, which would drag in httpx."""


class NonRetriablePipelineError(RuntimeError):
    """Stands in for the node wrapper."""


def _provider_failure() -> BaseException:
    """The exact shape a failed ElevenLabs render raises."""
    try:
        try:
            raise ElevenLabsError("ElevenLabs returned 401: Invalid API key")
        except ElevenLabsError as inner:
            raise NonRetriablePipelineError(
                "ElevenLabs could not narrate this: ElevenLabs returned 401: Invalid API key"
            ) from inner
    except NonRetriablePipelineError as outer:
        return outer


def _edge_blocked() -> BaseException:
    """Edge-TTS refused from a datacentre, with a path in the message."""
    try:
        try:
            raise Exception(
                '403 Forbidden from wss://speech.platform.bing.com/ '
                'File "/app/.venv/lib/python3.11/site-packages/edge_tts/communicate.py"'
            )
        except Exception as inner:
            raise NonRetriablePipelineError("Edge-TTS failed across 4 voices") from inner
    except NonRetriablePipelineError as outer:
        return outer


def test_the_pharmacist_never_sees_the_exception() -> None:
    note = _friendly_failure(_provider_failure())

    assert "401" not in note
    assert "ElevenLabs" not in note
    assert "Error" not in note
    # It says what to do, and that it cost them nothing.
    assert "allowance" in note


def test_the_diagnostic_carries_the_provider_reason() -> None:
    """The cause, not just the wrapper.

    "ElevenLabs could not narrate this" is not actionable. "401: Invalid API
    key" is — it names the thing to go and change.
    """
    reason = _technical_reason(_provider_failure())

    assert "401" in reason
    assert "Invalid API key" in reason


def test_the_diagnostic_names_the_real_cause_for_a_blocked_provider() -> None:
    reason = _technical_reason(_edge_blocked())

    assert "403" in reason, "the count of failed voices is not a reason"


def test_file_paths_are_stripped() -> None:
    """This field is readable by the video's owner through the API.

    Exception type and message only. Server paths say something about the host
    and nothing useful about the failure.
    """
    reason = _technical_reason(_edge_blocked())

    assert "site-packages" not in reason
    assert not re.search(r"(/[\w.\-]+){2,}", reason)


def test_the_diagnostic_is_bounded() -> None:
    """It is stored on every failed row and read on every poll."""
    huge = ValueError("x" * 5000)

    assert len(_technical_reason(huge)) <= 600


def test_a_cycle_in_the_cause_chain_terminates() -> None:
    """Chains can loop. A render failure must not hang the worker's failure path."""
    a = RuntimeError("a")
    b = RuntimeError("b")
    a.__cause__ = b
    b.__cause__ = a

    assert _technical_reason(a)  # returns, rather than spinning
