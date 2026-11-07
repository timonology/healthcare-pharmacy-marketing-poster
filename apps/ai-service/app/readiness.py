"""Can this service reach everything a design or a video needs, right now?

The API's ``/api/health/ready`` checks what the API depends on. The paid
providers live here — Anthropic writes scripts and designs, Pexels supplies
footage, ElevenLabs narrates, Replicate draws AI imagery, Tavily researches —
and each can fail on its own: an expired key, an exhausted plan, a revoked
token. Until now the first sign was a customer's video failing.

Every check is a free read: Anthropic's model list, a one-result Pexels search,
ElevenLabs' account (which also says how many characters are left), Replicate's
account, Tavily's usage (credits left). Nothing is generated, searched for a
fee, or charged.

Never returns a key or a value — verdicts and setting names only. Protected by
``READINESS_TOKEN`` (same value as the API's ``Readiness__Token``), 404 without it.
"""

from __future__ import annotations

import asyncio
import hmac
import time
from typing import Awaitable, Callable

import httpx

from app.config import Settings

PER_CHECK_SECONDS = 10.0

Check = Callable[[Settings, httpx.AsyncClient], Awaitable[tuple[str, str]]]


def token_matches(settings: Settings, presented: str | None) -> bool:
    expected = (settings.readiness_token or "").strip()
    if len(expected) < 24:
        return False
    return hmac.compare_digest(expected.encode(), (presented or "").encode())


async def _anthropic(s: Settings, client: httpx.AsyncClient) -> tuple[str, str]:
    if not s.anthropic_api_key:
        return "fail", "ANTHROPIC_API_KEY is not set: no scripts, no AI designs"
    r = await client.get(
        "https://api.anthropic.com/v1/models",
        headers={"x-api-key": s.anthropic_api_key, "anthropic-version": "2023-06-01"},
    )
    if r.status_code in (401, 403):
        return "fail", "Anthropic refused ANTHROPIC_API_KEY"
    if r.status_code >= 400:
        return "warn", f"Anthropic answered {r.status_code}"
    return "ok", "key accepted"


async def _pexels(s: Settings, client: httpx.AsyncClient) -> tuple[str, str]:
    if not s.pexels_api_key:
        return "fail", "PEXELS_API_KEY is not set: videos have no footage"
    r = await client.get(
        "https://api.pexels.com/videos/search",
        params={"query": "pharmacy", "per_page": 1},
        headers={"Authorization": s.pexels_api_key},
    )
    if r.status_code in (401, 403):
        return "fail", "Pexels refused PEXELS_API_KEY"
    if r.status_code == 429:
        return "fail", "Pexels rate limit reached: footage searches will fail until it resets"
    if r.status_code >= 400:
        return "warn", f"Pexels answered {r.status_code}"
    remaining = r.headers.get("X-Ratelimit-Remaining")
    return "ok", f"search works{f', {remaining} requests left this period' if remaining else ''}"


async def _elevenlabs(s: Settings, client: httpx.AsyncClient) -> tuple[str, str]:
    if not s.elevenlabs_api_key:
        # Edge-TTS is the fallback, and Microsoft refuses it from datacentres.
        return "fail", "ELEVENLABS_API_KEY is not set: hosted narration falls back to Edge-TTS, which is refused"
    headers = {"xi-api-key": s.elevenlabs_api_key}
    r = await client.get("https://api.elevenlabs.io/v1/user", headers=headers)
    if r.status_code in (401, 403) and "permission" in r.text.lower():
        # A scoped key (text-to-speech only) may not read the account. That
        # is a good key, not a refused one: ask for the voice list instead,
        # which narration needs anyway.
        v = await client.get("https://api.elevenlabs.io/v1/voices", headers=headers)
        if v.status_code < 400:
            return "ok", "key accepted (it cannot read the account, so characters left are unknown)"
        return "fail", "ElevenLabs refused ELEVENLABS_API_KEY for the voice list"
    if r.status_code in (401, 403):
        return "fail", "ElevenLabs refused ELEVENLABS_API_KEY"
    if r.status_code >= 400:
        return "warn", f"ElevenLabs answered {r.status_code}"

    sub = (r.json() or {}).get("subscription") or {}
    used, limit = sub.get("character_count"), sub.get("character_limit")
    if isinstance(used, int) and isinstance(limit, int) and limit > 0:
        left = limit - used
        # A 15-second script is ~250 characters; below ~40 videos' worth, say so.
        if left <= 0:
            return "fail", f"character allowance used up ({used}/{limit}): narration will fail"
        if left < 10_000:
            return "warn", f"{left} characters left this period (about {left // 250} videos)"
        return "ok", f"key accepted, {left} characters left this period"
    return "ok", "key accepted"


async def _replicate(s: Settings, client: httpx.AsyncClient) -> tuple[str, str]:
    if not s.replicate_api_token:
        return "warn", "REPLICATE_API_TOKEN is not set: the AI-imagery video style uses stock instead"
    r = await client.get(
        "https://api.replicate.com/v1/account",
        headers={"Authorization": f"Bearer {s.replicate_api_token}"},
    )
    if r.status_code in (401, 403):
        return "fail", "Replicate refused REPLICATE_API_TOKEN"
    if r.status_code >= 400:
        return "warn", f"Replicate answered {r.status_code}"
    return "ok", "token accepted"


async def _tavily(s: Settings, client: httpx.AsyncClient) -> tuple[str, str]:
    """Tavily's usage endpoint: free, and says how many credits are left.

    Never a search — that would spend a credit on every smoke run. If the usage
    endpoint is not there (an older API), the key is reported as set but
    unverified rather than failed: research is optional, a script is written
    without it.
    """
    if not s.tavily_api_key:
        return "warn", "TAVILY_API_KEY is not set: video scripts are written without research"
    r = await client.get("https://api.tavily.com/usage",
                         headers={"Authorization": f"Bearer {s.tavily_api_key}"})
    if r.status_code in (401, 403):
        return "fail", "Tavily refused TAVILY_API_KEY: video scripts are written without research"
    if r.status_code == 404:
        return "ok", "configured (Tavily's usage check is not available, so the key was not verified)"
    if r.status_code >= 400:
        return "warn", f"Tavily answered {r.status_code}"

    try:
        body = r.json()
    except ValueError:
        body = None
    key = body.get("key") if isinstance(body, dict) else None
    used = key.get("usage") if isinstance(key, dict) else None
    limit = key.get("limit") if isinstance(key, dict) else None
    if isinstance(used, (int, float)) and isinstance(limit, (int, float)) and limit > 0:
        left = int(limit - used)
        if left <= 0:
            return "fail", f"research credits used up ({int(used)}/{int(limit)}): scripts go out without research"
        if left < 100:
            return "warn", f"{left} research credits left (each video uses about one)"
        return "ok", f"key accepted, {left} research credits left"
    return "ok", "key accepted"


async def _database(s: Settings, _: httpx.AsyncClient) -> tuple[str, str]:
    if not s.mongo_connection_string:
        return "fail", "Mongo__ConnectionString is not set: the video worker cannot see any job"
    from motor.motor_asyncio import AsyncIOMotorClient

    client = AsyncIOMotorClient(s.mongo_connection_string, serverSelectionTimeoutMS=5000)
    try:
        await client[s.mongo_database].command("ping")
        return "ok", f"reachable ('{s.mongo_database}')"
    finally:
        client.close()


async def _storage(s: Settings, _: httpx.AsyncClient) -> tuple[str, str]:
    if not s.azure_blob_connection_string:
        return "fail", "AzureBlob__ConnectionString is not set: finished videos cannot be saved"
    from app.video_agent import storage

    def probe() -> None:
        storage._client().get_container_client(s.azure_blob_container).get_container_properties()

    await asyncio.to_thread(probe)
    return "ok", f"container '{s.azure_blob_container}' reachable"


CHECKS: dict[str, Check] = {
    "anthropic": _anthropic,
    "pexels": _pexels,
    "elevenlabs": _elevenlabs,
    "replicate": _replicate,
    "tavily": _tavily,
    "database": _database,
    "storage": _storage,
}


async def run(settings: Settings) -> dict:
    async with httpx.AsyncClient(timeout=PER_CHECK_SECONDS) as client:

        async def one(name: str, check: Check) -> dict:
            started = time.monotonic()
            try:
                status, detail = await asyncio.wait_for(check(settings, client), PER_CHECK_SECONDS)
            except asyncio.TimeoutError:
                status, detail = "fail", f"no answer within {PER_CHECK_SECONDS:.0f}s"
            except Exception as exc:  # noqa: BLE001 — a check must never take the report down
                status, detail = "fail", f"{type(exc).__name__}: {str(exc)[:200]}"
            return {"name": name, "status": status, "detail": detail,
                    "ms": int((time.monotonic() - started) * 1000)}

        checks = await asyncio.gather(*(one(n, c) for n, c in CHECKS.items()))

    overall = "fail" if any(c["status"] == "fail" for c in checks) else (
        "warn" if any(c["status"] == "warn" for c in checks) else "ok")
    return {"status": overall, "checks": checks}
