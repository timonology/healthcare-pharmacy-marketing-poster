"""The ai-service readiness checks: verdicts, never secrets, never charges.

Driven through httpx's MockTransport so no provider is called.
"""

import asyncio

import httpx

from app import readiness
from app.config import Settings

TOKEN = "r" * 32


def _settings(**overrides) -> Settings:
    base = {
        "JWT_SECRET": "x" * 40,
        "READINESS_TOKEN": TOKEN,
        "ANTHROPIC_API_KEY": "sk-ant-secret-value",
        "PEXELS_API_KEY": "pexels-secret-value",
        "ELEVENLABS_API_KEY": "eleven-secret-value",
        "REPLICATE_API_TOKEN": "r8_secret-value",
    }
    base.update(overrides)
    return Settings(**base)  # type: ignore[arg-type]


def _client(handler) -> httpx.AsyncClient:
    return httpx.AsyncClient(transport=httpx.MockTransport(handler))


def _run(check, settings, handler):
    async def go():
        async with _client(handler) as c:
            return await check(settings, c)
    return asyncio.run(go())


def test_the_token_must_match_and_be_long() -> None:
    assert readiness.token_matches(_settings(), TOKEN)
    assert not readiness.token_matches(_settings(), "wrong")
    assert not readiness.token_matches(_settings(), None)
    # A short configured token is treated as none: the endpoint stays closed.
    assert not readiness.token_matches(_settings(READINESS_TOKEN="short"), "short")


def test_a_refused_key_fails_and_names_the_setting_not_the_value() -> None:
    status, detail = _run(readiness._anthropic, _settings(), lambda r: httpx.Response(401))
    assert status == "fail"
    assert "ANTHROPIC_API_KEY" in detail
    assert "sk-ant-secret-value" not in detail


def test_an_accepted_key_passes() -> None:
    status, _ = _run(readiness._pexels, _settings(),
                     lambda r: httpx.Response(200, json={"videos": []}, headers={"X-Ratelimit-Remaining": "199"}))
    assert status == "ok"


def test_elevenlabs_warns_before_the_characters_run_out() -> None:
    low = {"subscription": {"character_count": 99_000, "character_limit": 100_000}}
    status, detail = _run(readiness._elevenlabs, _settings(), lambda r: httpx.Response(200, json=low))
    assert status == "warn" and "characters left" in detail

    gone = {"subscription": {"character_count": 100_000, "character_limit": 100_000}}
    assert _run(readiness._elevenlabs, _settings(), lambda r: httpx.Response(200, json=gone))[0] == "fail"


def test_a_missing_elevenlabs_key_fails_because_hosted_narration_has_no_fallback() -> None:
    status, _ = _run(readiness._elevenlabs, _settings(ELEVENLABS_API_KEY=None),
                     lambda r: httpx.Response(500))
    assert status == "fail"


def test_tavily_is_asked_about_usage_never_searched() -> None:
    seen = []

    def handler(request):
        seen.append(request.url.path)
        return httpx.Response(200, json={"key": {"usage": 40, "limit": 1000}})

    status, detail = _run(readiness._tavily, _settings(TAVILY_API_KEY="tvly-x"), handler)
    assert status == "ok" and "960" in detail
    assert seen == ["/usage"], "a search would spend a credit on every smoke run"


def test_tavily_warns_before_credits_run_out_and_fails_when_gone() -> None:
    low = lambda r: httpx.Response(200, json={"key": {"usage": 950, "limit": 1000}})  # noqa: E731
    gone = lambda r: httpx.Response(200, json={"key": {"usage": 1000, "limit": 1000}})  # noqa: E731
    assert _run(readiness._tavily, _settings(TAVILY_API_KEY="tvly-x"), low)[0] == "warn"
    assert _run(readiness._tavily, _settings(TAVILY_API_KEY="tvly-x"), gone)[0] == "fail"


def test_a_refused_tavily_key_fails() -> None:
    status, detail = _run(readiness._tavily, _settings(TAVILY_API_KEY="tvly-secret"),
                          lambda r: httpx.Response(401))
    assert status == "fail" and "tvly-secret" not in detail


def test_one_check_throwing_does_not_take_the_report_down(monkeypatch) -> None:
    async def boom(s, c):
        raise RuntimeError("provider exploded")

    async def fine(s, c):
        return "ok", "fine"

    monkeypatch.setattr(readiness, "CHECKS", {"a": boom, "b": fine})
    report = asyncio.run(readiness.run(_settings()))

    assert report["status"] == "fail"
    by_name = {c["name"]: c for c in report["checks"]}
    assert by_name["a"]["status"] == "fail" and "RuntimeError" in by_name["a"]["detail"]
    assert by_name["b"]["status"] == "ok"


def test_a_scoped_elevenlabs_key_is_not_reported_as_refused() -> None:
    """A text-to-speech-only key cannot read the account; it still works."""
    def handler(request):
        if request.url.path == "/v1/user":
            return httpx.Response(401, json={"detail": {"status": "missing_permissions"}})
        return httpx.Response(200, json={"voices": []})

    status, detail = _run(readiness._elevenlabs, _settings(), handler)
    assert status == "ok" and "cannot read the account" in detail
