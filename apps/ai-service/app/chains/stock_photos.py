"""
Pexels photo search for poster imagery.

The video pipeline already uses Pexels, but only the *videos* endpoint
(api.pexels.com/videos/search) for B-roll. Photos live at a different path
under the same API key, so this needs no new credential.

Two operations, deliberately split:

  search()  - returns candidates with thumbnail URLs the browser renders
              directly from the Pexels CDN. Cheap, and nothing is stored.

  fetch()   - takes a photo ID and returns the bytes, for the API to store as
              a permanent template asset.

fetch() takes an ID rather than a URL on purpose. An endpoint that downloads
whatever URL a client hands it is a server-side request forgery vector: inside
a container network that URL could name the metadata service or an internal
admin port. Resolving the ID through the Pexels API means the only URL ever
fetched is one Pexels itself returned.
"""

from __future__ import annotations

import asyncio
import logging
from typing import Any
from urllib.parse import urlparse

import httpx

from app.config import get_settings

log = logging.getLogger(__name__)

SEARCH_URL = "https://api.pexels.com/v1/search"
PHOTO_URL = "https://api.pexels.com/v1/photos/{id}"

# Pexels accepts landscape | portrait | square.
ORIENTATION_BY_ASPECT: dict[str, str] = {
    "banner": "landscape",
    "hero": "landscape",
    "portrait": "portrait",
    "full": "portrait",
}

# Appended to short queries. Left off longer ones, which are usually specific
# enough that extra terms narrow the results to nothing.
_CONTEXT_TERMS = "pharmacy healthcare"


class StockPhotoError(RuntimeError):
    """Search or download failed, or Pexels is not configured."""


# Substrings that identify a DNS resolution failure across platforms. These
# arrive wrapped in httpx.ConnectError, so the type alone doesn't distinguish
# "the network is broken" from "the host refused us".
_DNS_MARKERS = (
    "name or service not known",      # glibc / Linux containers
    "temporary failure in name resolution",
    "nodename nor servname",          # macOS
    "getaddrinfo failed",             # Windows
    "no address associated with hostname",
)


def _is_dns_failure(exc: Exception) -> bool:
    return any(m in str(exc).lower() for m in _DNS_MARKERS)


def _host_of(url: str) -> str:
    """Hostname from a URL, for error messages. Falls back to the raw value."""
    try:
        return urlparse(url).hostname or url
    except ValueError:
        return url


def _network_message(exc: Exception, action: str, url: str) -> str:
    """
    Turn a transport failure into something that points at the actual fix.
    """
    host = _host_of(url)
    if _is_dns_failure(exc):
        # Name the host that actually failed rather than a hardcoded one.
        # Pexels serves its API from api.pexels.com and its files from
        # images.pexels.com, and they fail independently — an earlier version
        # of this message always blamed api.pexels.com, which sent people to
        # test a hostname that was resolving perfectly well.
        return (
            f"Could not resolve {host}, so {action} failed. This is a DNS "
            "failure inside the container, not a credentials problem — "
            "internal hostnames like 'mongo' still work, and other Pexels "
            "hosts may too. Restart ai-service; if it recurs, the container "
            "needs explicit DNS servers (see docker-compose)."
        )
    return f"Could not reach {host} while {action}: {exc}"


#: Delays between attempts, in seconds. Three attempts total.
#:
#: A single 1s retry proved too optimistic: a failing resolver can take ~10s to
#: give up, and by the time it does the transient condition often hasn't
#: cleared. Backing off to 4s gives the embedded DNS a realistic chance to
#: recover, while capping the worst case at roughly half a minute so the admin
#: waiting on the request isn't left hanging.
_RETRY_DELAYS = (1.0, 4.0)


async def _get_with_retry(
    client: httpx.AsyncClient, url: str, action: str, **kwargs
) -> httpx.Response:
    """
    GET, retrying transport failures with backoff.

    Only TransportError is retried — DNS, connection and timeout problems, all
    of which are plausibly transient. An HTTP error status is deliberately not
    retried here: a 401 or 404 will say the same thing every time, and the
    caller handles those meaningfully.
    """
    last: Exception | None = None

    for attempt, delay in enumerate((0.0, *_RETRY_DELAYS)):
        if delay:
            await asyncio.sleep(delay)
        try:
            return await client.get(url, **kwargs)
        except httpx.TransportError as exc:
            last = exc
            remaining = len(_RETRY_DELAYS) - attempt
            if remaining > 0:
                log.warning(
                    "Pexels %s failed against %s (%s); %d retr%s left",
                    action, _host_of(url), exc, remaining,
                    "y" if remaining == 1 else "ies",
                )

    raise StockPhotoError(
        _network_message(last or RuntimeError("unknown"), action, url)
    ) from last


def is_configured() -> bool:
    return bool(get_settings().pexels_api_key)


def _headers() -> dict[str, str]:
    key = get_settings().pexels_api_key
    if not key:
        raise StockPhotoError(
            "Stock photo search is not configured. Set PEXELS_API_KEY."
        )
    return {"Authorization": key}


def build_query(raw: str) -> str:
    """Nudge short queries towards healthcare imagery.

    "vaccination" on its own returns a lot of laboratory and syringe macro
    shots; "vaccination pharmacy healthcare" returns people in clinics, which
    is what a pharmacy poster wants.
    """
    q = " ".join(raw.split())
    return f"{q} {_CONTEXT_TERMS}" if len(q.split()) <= 2 else q


def _simplify(photo: dict[str, Any]) -> dict[str, Any]:
    """Reduce a Pexels photo to what the picker and the designer need."""
    src = photo.get("src") or {}
    return {
        "id": str(photo.get("id", "")),
        # `large` is ~940px wide — plenty for a grid thumbnail and it keeps the
        # picker responsive. The full-resolution file is only downloaded for
        # the one photo the user actually picks.
        "thumbnailUrl": src.get("large") or src.get("medium") or src.get("original", ""),
        "width": float(photo.get("width") or 0),
        "height": float(photo.get("height") or 0),
        "photographer": photo.get("photographer") or "",
        "alt": (photo.get("alt") or "").strip(),
    }


async def search(
    query: str,
    orientation: str | None = None,
    per_page: int = 15,
) -> list[dict[str, Any]]:
    params: dict[str, Any] = {
        "query": build_query(query),
        "per_page": max(1, min(per_page, 40)),
    }
    if orientation in ("landscape", "portrait", "square"):
        params["orientation"] = orientation

    try:
        async with httpx.AsyncClient(timeout=20) as http:
            res = await _get_with_retry(
                http, SEARCH_URL, "searching", headers=_headers(), params=params
            )
            res.raise_for_status()
            payload = res.json()
    except httpx.HTTPStatusError as exc:
        # 401 here almost always means the key is missing or wrong, which is
        # worth saying plainly rather than surfacing a bare status code.
        if exc.response.status_code == 401:
            raise StockPhotoError("Pexels rejected the API key.") from exc
        log.warning("Pexels search failed: %s", exc)
        raise StockPhotoError(
            f"Pexels search failed (HTTP {exc.response.status_code}).") from exc
    except StockPhotoError:
        raise
    except httpx.HTTPError as exc:
        raise StockPhotoError(_network_message(exc, "searching", SEARCH_URL)) from exc

    photos = [_simplify(p) for p in payload.get("photos", [])]
    results = [p for p in photos if p["id"] and p["thumbnailUrl"]]
    log.info("Pexels: %r -> %d photo(s)", params["query"], len(results))
    return results


async def fetch(photo_id: str) -> tuple[bytes, str, dict[str, Any]]:
    """
    Download one photo by ID. Returns (bytes, content_type, metadata).

    The download URL comes from the Pexels API response, never from the caller.
    """
    if not photo_id.isdigit():
        raise StockPhotoError("Invalid photo id.")

    try:
        async with httpx.AsyncClient(timeout=60, follow_redirects=True) as http:
            meta_res = await _get_with_retry(
                http, PHOTO_URL.format(id=photo_id), "fetching", headers=_headers()
            )
            if meta_res.status_code == 404:
                raise StockPhotoError("That photo is no longer available.")
            meta_res.raise_for_status()
            photo = meta_res.json()

            src = photo.get("src") or {}
            # `large2x` is around 1880px on the long edge — comfortably above
            # the 1240px canvas width at print density, without pulling a 6000px
            # original that would bloat blob storage for no visible gain.
            download_url = src.get("large2x") or src.get("original") or src.get("large")
            if not download_url:
                raise StockPhotoError("That photo has no downloadable file.")

            file_res = await _get_with_retry(http, download_url, "downloading")
            file_res.raise_for_status()
            data = file_res.content
    except StockPhotoError:
        raise
    except httpx.HTTPError as exc:
        raise StockPhotoError(_network_message(exc, "downloading", download_url)) from exc

    content_type = file_res.headers.get("content-type", "image/jpeg").split(";")[0]
    if content_type not in ("image/jpeg", "image/png", "image/webp"):
        # Pexels serves JPEG; anything else means we mis-parsed the response.
        content_type = "image/jpeg"

    meta = _simplify(photo)
    log.info(
        "Pexels: downloaded photo %s (%d KB) by %s",
        photo_id, len(data) // 1024, meta["photographer"] or "unknown",
    )
    return data, content_type, meta


def resolve_orientation(aspect: str | None) -> str | None:
    """Map a poster image slot to a Pexels orientation filter."""
    if not aspect:
        return None
    return ORIENTATION_BY_ASPECT.get(aspect.lower())


__all__ = [
    "StockPhotoError",
    "build_query",
    "fetch",
    "is_configured",
    "resolve_orientation",
    "search",
]
