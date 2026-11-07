import datetime
import logging
import os
import sys
from contextlib import asynccontextmanager

# ─── Boot marker ──────────────────────────────────────────────────
# The very first thing this module does, before ANY imports that
# could fail silently, is print a marker to stdout. If you don't see
# this line in App Service Log Stream, the container running is not
# this image — Docker Hub / App Service is serving a cached copy.
#
# The tag comes from the environment, not from a literal here.
#
# It used to be a hand-typed constant with "bump this when you push" next
# to it, which is a marker that answers the wrong question: it tells you
# which string somebody last edited, not which build is running. A
# five-day-old container went unnoticed for exactly that reason — a
# refine endpoint returned 404 while this line claimed a build that
# looked current, because it always claims the same thing.
#
# `BUILD_ID` is stamped at image build time. Unset — a plain
# `docker compose build` — falls back to the code itself.
#
# <b>That fallback used to read this file's mtime, and it was wrong in the
# exact way this marker exists to prevent.</b> `main.py` changes rarely.
# A rebuild that added `video_agent/user_media.py` and rewrote
# `video_agent/worker.py` left it untouched, so a freshly built image
# printed a marker five days old — and the marker was believed over the
# rebuild, twice, by people reasoning carefully from it. A marker that
# cannot tell a new image from an old one is worse than none, because it
# is trusted.
#
# So: the newest mtime anywhere under `app/`, and the number of files.
# Any edit moves the first; adding or removing a module moves the second.
# Costs one directory walk of ~50 files at boot, once.
def _source_stamp() -> str:
    newest, count = 0.0, 0
    for root, _dirs, files in os.walk(os.path.dirname(__file__)):
        if "__pycache__" in root:
            continue
        for name in files:
            if not name.endswith(".py"):
                continue
            count += 1
            try:
                newest = max(newest, os.path.getmtime(os.path.join(root, name)))
            except OSError:
                # A file that vanished mid-walk is not worth failing boot over.
                continue
    when = datetime.datetime.fromtimestamp(newest or 0).strftime("%Y%m%d-%H%M%S")
    return f"local-{when}-{count}f"


BUILD_ID = os.getenv("BUILD_ID") or _source_stamp()
print("=" * 60, flush=True)
print(f"SONAR AI-SERVICE BOOT MARKER  build={BUILD_ID}", flush=True)
print("=" * 60, flush=True)

from fastapi import FastAPI, Header
from fastapi.middleware.cors import CORSMiddleware

from app.routers import canvas, video
from app.video_agent.worker import worker


# ─── Logging setup ─────────────────────────────────────────────────
# Uvicorn only configures its own logger. Without this call, every
# `log.info(...)` in our worker code gets silently dropped because the
# root logger stays at WARNING. That produces a very confusing symptom
# in App Service: FastAPI boots fine, health probes return 200, but the
# worker appears to do nothing because its log lines never reach stdout.
# Configure once, at process start, before any of our modules log.
_log_level = os.getenv("LOG_LEVEL", "INFO").upper()
logging.basicConfig(
    level=_log_level,
    format="%(asctime)s %(levelname)-5s %(name)s: %(message)s",
    stream=sys.stdout,
    force=True,  # override any handler Uvicorn already installed
)
# Tone down chatty libraries so real signal isn't buried.
logging.getLogger("httpx").setLevel(logging.WARNING)
logging.getLogger("httpcore").setLevel(logging.WARNING)
logging.getLogger("pymongo").setLevel(logging.WARNING)
# The Azure SDK logs every request and response at INFO, including the
# expected 409 ContainerAlreadyExists on each boot — which reads as a failure in
# Railway's log view. Our own storage code logs any real container error.
logging.getLogger("azure").setLevel(logging.WARNING)


@asynccontextmanager
async def lifespan(app: FastAPI):
    # Start the video worker on boot. The worker is a no-op if
    # VIDEO_WORKER_ENABLED=false or Mongo isn't configured, so this is
    # safe in local envs that only exercise the canvas endpoints.
    boot_log = logging.getLogger(__name__)

    # Print the JWT config fingerprint at boot.
    #
    # ai-service verifies tokens the .NET API signs. If the secrets differ,
    # every authenticated call fails with a 401 that looks like a login problem
    # and gives no hint about the real cause. The API logs the same fingerprint
    # on startup — if these two lines disagree, that is the bug.
    try:
        from app.config import get_settings
        from app.security import secret_fingerprint

        settings = get_settings()
        boot_log.info(
            "JWT config: secret_fingerprint=%s issuer=%r audience=%r",
            secret_fingerprint(settings.jwt_secret),
            settings.jwt_issuer,
            settings.jwt_audience,
        )
    except Exception:
        # A diagnostic must never be the reason the service won't start. If
        # settings are broken the worker below reports it properly; this line
        # failing first would just misattribute the cause.
        boot_log.warning("Could not read JWT config for the boot fingerprint", exc_info=True)

    # Every route this build actually serves.
    #
    # A missing endpoint answers 404, and a 404 from a service that is
    # otherwise healthy is indistinguishable from a typo in the caller's URL,
    # a proxy rewrite, or an old image. That ambiguity cost real time: the .NET
    # side reported "we could not work out what to change just now" while
    # ai-service answered 404 to /api/video/classify-refine, and the only way
    # to tell a stale container from a wrong path was to read the source and
    # guess. If the path you are calling is not on this line, the running image
    # does not have it — whatever the boot marker says.
    #
    # Read from the OpenAPI schema, not from app.routes.
    #
    # Two wrong versions preceded this. The first filtered app.routes for
    # "POST" and printed "(none)" on a build that plainly serves POST routes.
    # The second printed the routes it found and revealed why: this FastAPI
    # version wraps included routers in `_IncludedRouter` objects, so the real
    # endpoints are nested inside them and a top-level scan sees containers
    # rather than routes. It logged two pages of repr and still did not answer
    # the question.
    #
    # `app.openapi()` is the documented, version-stable view of what this app
    # serves, keyed by the full path with the prefixes already applied — which
    # is exactly the thing in doubt when a caller gets a 404.
    #
    # Wrapped, because a diagnostic must never be the reason a service fails to
    # start. Schema generation touches every route model and can raise on one
    # bad annotation; that is worth a warning, not an outage.
    try:
        paths = app.openapi().get("paths", {})
        listed = sorted(
            f"{','.join(sorted(m.upper() for m in ops)) or '-'} {path}"
            for path, ops in paths.items()
        )
        boot_log.info(
            "Routes served by this build (%d): %s",
            len(listed), " | ".join(listed) or "(none)",
        )
    except Exception:
        boot_log.warning("Could not list routes for the boot diagnostic", exc_info=True)

    boot_log.info("Lifespan startup: initialising video worker")
    worker.start()
    try:
        yield
    finally:
        await worker.stop()


# Interactive /docs and /redoc only when APP_ENV=development (security review
# SEC-08): they map every endpoint for a stranger, and this service is
# reachable from the internet. Unset means production, so a forgotten setting
# fails closed. /openapi.json stays — the smoke and e2e checks read it.
#
# Read straight from the environment rather than get_settings(): that is
# cached, and priming it at import would freeze settings before tests set them.
_docs = os.environ.get("APP_ENV", "production").strip().lower() in {"development", "dev", "local"}

app = FastAPI(
    title="Marketing25 AI Service",
    version="0.1.0",
    lifespan=lifespan,
    docs_url="/docs" if _docs else None,
    redoc_url="/redoc" if _docs else None,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:3000"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(canvas.router, prefix="/api")
# Served at /api/video/… — the router's own prefix is only half the path, which
# has caught us out before. See AiTemplateGenerator's note on CanvasRoot.
app.include_router(video.router, prefix="/api")


@app.get("/health", tags=["meta"])
def health() -> dict[str, str]:
    """Liveness, plus which build is answering.

    `build` is here because "is this container running the code I just
    pushed?" was, until now, a question you answered by scrolling back
    through the log for a boot marker — and the marker was a hand-typed
    constant, so it could not answer it either. A container ran five days
    stale while a new endpoint 404'd; nothing on this service would have
    told you, and the caller's error message said the model had not
    understood the request.

    One `curl` now settles it:

        curl -s localhost:8000/health

    Deployed builds report the git short SHA, which is also the immutable
    image tag. Locally it is `local-<mtime>`, which at least moves when the
    code does.
    """
    return {"status": "ok", "build": BUILD_ID}


@app.get("/health/ready", tags=["meta"], include_in_schema=False)
async def health_ready(x_readiness_token: str | None = Header(default=None)) -> dict:
    """Every provider this service depends on, checked with a free read.

    For `pnpm smoke`. Needs ``X-Readiness-Token`` matching ``READINESS_TOKEN``;
    404 otherwise, and 404 when no token is configured. See app/readiness.py.
    """
    from fastapi import HTTPException

    from app import readiness
    from app.config import get_settings

    settings = get_settings()
    if not readiness.token_matches(settings, x_readiness_token):
        raise HTTPException(status_code=404, detail="Not Found")
    return {"build": BUILD_ID, **(await readiness.run(settings))}


@app.get("/diagnostics/jwt", tags=["meta"], include_in_schema=False)
def jwt_diagnostics() -> dict[str, str]:
    """
    Fingerprint of the JWT config, for comparing against the .NET API's
    /api/diagnostics/jwt.

    Anonymous and non-reversible: a truncated SHA-256 of the secret. The whole
    point is that a smoke test can prove the two services agree without a human
    reading two sets of startup logs — a mismatch makes every cross-service
    call fail with a 401 that looks like a login problem.
    """
    from fastapi import HTTPException

    from app.config import get_settings
    from app.security import secret_fingerprint

    # Development only, like the API's own /api/diagnostics/jwt (security
    # review R2-11): anywhere else it is an oracle for confirming a guessed or
    # leaked secret. Both services log the same fingerprint at boot, which is
    # where to compare them in production.
    if not _docs:
        raise HTTPException(status_code=404, detail="Not Found")

    s = get_settings()
    return {
        "secretFingerprint": secret_fingerprint(s.jwt_secret),
        "issuer": s.jwt_issuer,
        "audience": s.jwt_audience,
    }
