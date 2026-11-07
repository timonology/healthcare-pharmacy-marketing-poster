"""JWT validation. Validates tokens issued by the .NET API."""

import base64
import hashlib
import hmac
import logging
import time
from typing import Annotated

from fastapi import Depends, HTTPException, Request, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from jose import ExpiredSignatureError, JWTError, jwt

# JWTClaimsError is defined in jose.exceptions but NOT re-exported from the
# package root (jose/__init__.py exports only JOSEError, JWSError, JWTError and
# ExpiredSignatureError). Importing it from `jose` raises ImportError at module
# load, which takes the whole service down at startup.
from jose.exceptions import JWTClaimsError
from pydantic import BaseModel

from app.config import Settings, get_settings

log = logging.getLogger(__name__)

bearer = HTTPBearer(auto_error=True)


def secret_fingerprint(secret: str | None) -> str:
    """
    A short, non-reversible fingerprint of the signing secret.

    Exists so a mismatch between this service and the .NET API is a two-second
    comparison instead of an opaque 401. Both sides log this at boot; if the
    fingerprints differ, every forwarded token will be rejected no matter what
    else is configured. Never log the secret itself.
    """
    if not secret:
        return "(unset)"
    return hashlib.sha256(secret.encode()).hexdigest()[:12]


class CurrentUser(BaseModel):
    id: str
    email: str
    name: str
    role: str = "PharmacyUser"

    @property
    def is_staff(self) -> bool:
        return self.role in STAFF_ROLES


STAFF_ROLES = {"Admin", "SuperAdmin"}

# ASP.NET writes the role claim under the full SOAP claim-type URI unless it is
# explicitly remapped, so accept both spellings rather than silently reading no
# role (which would fail open to the default below if this were ever used for
# anything other than denial).
_ROLE_CLAIMS = (
    "role",
    "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
)


def get_current_user(
    creds: Annotated[HTTPAuthorizationCredentials, Depends(bearer)],
    settings: Annotated[Settings, Depends(get_settings)],
) -> CurrentUser:
    try:
        payload = jwt.decode(
            creds.credentials,
            settings.jwt_secret,
            algorithms=["HS256"],
            audience=settings.jwt_audience,
            issuer=settings.jwt_issuer,
        )
    # These three failures need different fixes, so they get different
    # messages. "Invalid or expired token" conflated a rotated secret, a stale
    # login and a misconfigured issuer into one dead end.
    except ExpiredSignatureError as exc:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Token has expired. Sign in again.",
        ) from exc
    except JWTClaimsError as exc:
        log.warning(
            "Token claims rejected (expected issuer=%r audience=%r): %s",
            settings.jwt_issuer, settings.jwt_audience, exc,
        )
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail=(
                "Token issuer or audience does not match. Check JWT_ISSUER and "
                "JWT_AUDIENCE against the API's Jwt:Issuer and Jwt:Audience."
            ),
        ) from exc
    except JWTError as exc:
        # Almost always a signing-secret mismatch, which is invisible from the
        # outside — so name the likely cause and give something comparable.
        log.warning(
            "Token signature rejected. ai-service JWT_SECRET fingerprint=%s. "
            "If this differs from the API's, that is the cause. (%s)",
            secret_fingerprint(settings.jwt_secret), exc,
        )
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail=(
                "Token signature is not valid. ai-service's JWT_SECRET most "
                "likely differs from the one the API signs with."
            ),
        ) from exc

    role = ""
    for claim in _ROLE_CLAIMS:
        value = payload.get(claim)
        if isinstance(value, list):
            value = value[0] if value else None
        if value:
            role = str(value)
            break

    return CurrentUser(
        id=str(payload.get("sub", "")),
        email=str(payload.get("email", "")),
        name=str(payload.get("name", "")),
        role=role or "PharmacyUser",
    )


def require_staff(
    user: Annotated[CurrentUser, Depends(get_current_user)],
) -> CurrentUser:
    """Dependency for endpoints only administrators may call.

    Defence in depth: the .NET API already gates the admin surface, but
    ai-service is independently reachable inside the cluster, so anything
    expensive or content-creating re-checks the role here.
    """
    if not user.is_staff:
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Administrator access required.",
        )
    return user


# ─── Quota assertions ──────────────────────────────────────────────
#
# Why this exists, in one paragraph: the expensive endpoints here are gated on
# `require_staff` because this service is independently reachable. It is not
# merely reachable inside the cluster — it is its own public App Service, and
# docker-compose publishes it on port 8000. Poster design had to open to
# pharmacy users, and the monthly allowance that makes it affordable lives in
# the .NET API, because that is where subscriptions live. Opening the role gate
# alone would therefore let any pharmacy account call this endpoint directly, in
# a loop, and never meet its allowance. A cost control the customer can step
# around is not a cost control.
#
# So the API signs a short assertion — the caller's own user id, the purpose,
# and the minute — with the JWT secret the two services already share, and sends
# it alongside the caller's token. The token says who. The assertion says the
# allowance was checked. A pharmacist cannot forge one: an HMAC key is not
# recoverable from the tokens it has signed.

QUOTA_ASSERTION_HEADER = "X-Quota-Assertion"

#: Must match AiQuotaAssertion.PosterDesignPurpose on the .NET side.
PURPOSE_POSTER_DESIGN = "poster-design"


def _sign_quota(secret: str, user_id: str, purpose: str, minute: int) -> str:
    mac = hmac.new(
        secret.encode("utf-8"),
        f"{user_id}|{purpose}|{minute}".encode("utf-8"),
        hashlib.sha256,
    ).digest()
    return base64.urlsafe_b64encode(mac).decode("ascii").rstrip("=")


def quota_assertion_valid(
    secret: str | None, presented: str | None, user_id: str, purpose: str
) -> bool:
    """True when `presented` is the API's word that this user's quota was checked.

    Accepts the current or previous minute. A captured assertion is then useful
    for at most two minutes rather than indefinitely, and a request that
    straddles a minute boundary — or a service whose clock is a little behind —
    still works. Widening this further would buy nothing and cost exactly the
    property the window exists for.
    """
    if not secret or not presented or not user_id:
        return False

    minute = int(time.time()) // 60
    return any(
        hmac.compare_digest(presented, _sign_quota(secret, user_id, purpose, m))
        for m in (minute, minute - 1)
    )


def require_poster_designer(
    request: Request,
    user: Annotated[CurrentUser, Depends(get_current_user)],
    settings: Annotated[Settings, Depends(get_settings)],
) -> CurrentUser:
    """Staff, or a pharmacy user the API has vouched for.

    Staff pass on the role alone, exactly as before — the admin console's
    template generation is unchanged by any of this.
    """
    if user.is_staff:
        return user

    if quota_assertion_valid(
        settings.jwt_secret,
        request.headers.get(QUOTA_ASSERTION_HEADER),
        user.id,
        PURPOSE_POSTER_DESIGN,
    ):
        return user

    # 403 and not 401: the token is fine, the authorisation is not. A 401 would
    # send the browser through a sign-in it does not need.
    log.warning(
        "Refused poster design for %s: no valid quota assertion. Either the "
        "request did not come through the API's allowance check, or the two "
        "services disagree about the JWT secret — compare the boot "
        "fingerprints before assuming abuse.",
        user.id,
    )
    raise HTTPException(
        status_code=status.HTTP_403_FORBIDDEN,
        detail="Poster design must be requested through the API.",
    )


#: A pharmacy searching stock photography for its own poster. Signed by the API
#: after its per-user rate limit, because the Pexels key is shared by every
#: pharmacy and its hourly allowance is the thing a loop would exhaust.
PURPOSE_STOCK_SEARCH = "stock-search"


def require_stock_searcher(
    request: Request,
    user: Annotated[CurrentUser, Depends(get_current_user)],
    settings: Annotated[Settings, Depends(get_settings)],
) -> CurrentUser:
    """Staff, or a pharmacy user whose search the API rate-limited and vouched for."""
    if user.is_staff:
        return user
    if quota_assertion_valid(
        settings.jwt_secret,
        request.headers.get(QUOTA_ASSERTION_HEADER),
        user.id,
        PURPOSE_STOCK_SEARCH,
    ):
        return user
    raise HTTPException(
        status_code=status.HTTP_403_FORBIDDEN,
        detail="Photo search must be requested through the API.",
    )


# ── per-user rate limit ──────────────────────────────────────────────────────


class _Window:
    """Sliding-window counts per key, in memory (per process)."""

    def __init__(self) -> None:
        self._hits: dict[str, list[float]] = {}

    def allow(self, key: str, limit: int, window_s: float, now: float | None = None) -> bool:
        now = time.monotonic() if now is None else now
        hits = [t for t in self._hits.get(key, []) if now - t < window_s]
        if len(hits) >= limit:
            self._hits[key] = hits
            return False
        hits.append(now)
        self._hits[key] = hits
        # Keep the table from growing without bound under many users.
        if len(self._hits) > 10_000:
            for k in [k for k, v in self._hits.items() if not v or now - v[-1] >= window_s]:
                self._hits.pop(k, None)
        return True


_limiter = _Window()


def per_user_limit(name: str, limit: int, window_s: float = 60.0):
    """A dependency that refuses a non-staff user past ``limit`` calls per window.

    ai-service is reachable from the internet and these calls each cost a
    model request. The API's own limits do not apply to a pharmacy that calls
    this service directly with its token (security review SEC-08).
    """

    def dependency(user: Annotated[CurrentUser, Depends(get_current_user)]) -> CurrentUser:
        if user.is_staff:
            return user
        if not _limiter.allow(f"{name}:{user.id}", limit, window_s):
            raise HTTPException(
                status_code=status.HTTP_429_TOO_MANY_REQUESTS,
                detail="Too many requests. Wait a moment and try again.",
            )
        return user

    return dependency
