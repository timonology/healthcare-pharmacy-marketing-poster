"""The refine classifier, end to end with a fake model.

There were no tests here at all, and the classifier shipped unable to run: its
import of `anthropic_chat` had landed inside the module docstring, so every call
raised NameError, was caught by the never-raise guard, and reached the
pharmacist as "we could not work out what to change" for every sentence. These
tests drive `classify` through its real code path — only the model is fake — so
anything that stops it reaching the model fails here instead.
"""

import json
from types import SimpleNamespace

import pytest

import app.config as config
import app.llm as llm
from app.chains import refine_router


class _FakeModel:
    def __init__(self, reply: str) -> None:
        self.reply = reply
        self.calls: list = []

    def invoke(self, messages):
        self.calls.append(messages)
        return SimpleNamespace(content=self.reply)


@pytest.fixture
def model(monkeypatch):
    """A model that answers with whatever the test sets on it."""
    fake = _FakeModel(json.dumps({"stage": "editor", "reason": "Captions will change."}))
    monkeypatch.setattr(llm, "anthropic_chat", lambda *a, **k: fake)
    monkeypatch.setattr(
        config, "get_settings",
        lambda: SimpleNamespace(anthropic_model="claude-test", anthropic_api_key="k"))
    return fake


def test_a_caption_request_reaches_the_model_and_comes_back_routed(model) -> None:
    result = refine_router.classify("remove the duplicate caption")

    assert model.calls, "the model was never asked — something before it failed"
    assert result == {"stage": "editor", "reason": "Captions will change.", "free": True}


def test_a_request_that_needs_new_words_routes_to_the_scriptwriter(model) -> None:
    model.reply = json.dumps({"stage": "scriptwriter", "reason": "The words will change."})

    result = refine_router.classify("remove duplicate caption and make it flu related")

    assert result["stage"] == "scriptwriter"
    assert result["free"] is False


def test_a_fenced_reply_is_still_read(model) -> None:
    model.reply = '```json\n{"stage": "voice", "reason": "A different voice."}\n```'

    assert refine_router.classify("use a male voice")["stage"] == "voice"


def test_a_stage_that_does_not_exist_is_unclear(model) -> None:
    model.reply = json.dumps({"stage": "marketing", "reason": "x"})

    assert refine_router.classify("make it pop")["stage"] == refine_router.UNCLEAR


def test_a_provider_failure_is_unclear_rather_than_an_error(model, monkeypatch) -> None:
    def down(*_a, **_k):
        raise RuntimeError("overloaded")

    monkeypatch.setattr(llm, "anthropic_chat", down)

    result = refine_router.classify("make the captions bigger")

    assert result["stage"] == refine_router.UNCLEAR


def test_nothing_to_act_on_never_calls_the_model(model) -> None:
    assert refine_router.classify("  ")["stage"] == refine_router.UNCLEAR
    assert not model.calls
