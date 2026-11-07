"""Provider keys pasted into a hosting dashboard are cleaned before use.

From the 23 October production run: video and AI design worked locally and
failed on Railway with ``401 invalid x-api-key``. A key copied from a .env line
keeps its quotes in a dashboard, and one copied from a terminal keeps its
newline; the .env parser strips both locally, the platform does not.
"""
import pytest

from app.config import Settings
from app.video_agent.worker import _key_fingerprint

KEY = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz-WXYZ"


@pytest.mark.parametrize(
    "raw",
    [KEY, f"{KEY}\n", f"  {KEY}  ", f'"{KEY}"', f"'{KEY}'", f'"{KEY}"\n', f'" {KEY} "'],
)
def test_pasted_key_is_cleaned(monkeypatch, raw):
    monkeypatch.setenv("JWT_SECRET", "x" * 32)
    monkeypatch.setenv("ANTHROPIC_API_KEY", raw)
    assert Settings(_env_file=None).anthropic_api_key == KEY


@pytest.mark.parametrize("raw", ["", "   ", '""', "''", "\n"])
def test_blank_key_counts_as_not_set(monkeypatch, raw):
    monkeypatch.setenv("JWT_SECRET", "x" * 32)
    monkeypatch.setenv("ANTHROPIC_API_KEY", raw)
    assert Settings(_env_file=None).anthropic_api_key is None


def test_a_quote_inside_the_key_is_left_alone(monkeypatch):
    monkeypatch.setenv("JWT_SECRET", "x" * 32)
    monkeypatch.setenv("ANTHROPIC_API_KEY", f'{KEY}"')
    assert Settings(_env_file=None).anthropic_api_key == f'{KEY}"'


def test_fingerprint_names_the_key_without_revealing_it():
    fp = _key_fingerprint(KEY)
    assert fp == f"set(sk-ant-api…WXYZ, {len(KEY)} chars)"
    assert KEY[:-4] not in fp
    assert _key_fingerprint(None) == "MISSING"
    assert "UNEXPECTED-PREFIX" in _key_fingerprint("abc123")
