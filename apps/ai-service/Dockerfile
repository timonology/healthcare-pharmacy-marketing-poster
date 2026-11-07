# syntax=docker/dockerfile:1.7
FROM python:3.11-slim AS base
WORKDIR /app
ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1

# System deps for the video agent:
#   ffmpeg       — the entire video pipeline hinges on it (Ken Burns +
#                   concat + AAC encode + thumbnail extraction).
#   imagemagick  — moviepy TextClip renders overlays through it.
#   fonts-*      — bold DejaVu + Liberation give us the caption fallback
#                   chain that mirrors what QuestPdfExporter uses on the
#                   .NET side, so on-video text renders identically to
#                   the poster PDFs.
RUN apt-get update && apt-get install -y --no-install-recommends \
      ffmpeg \
      imagemagick \
      fonts-dejavu \
      fonts-liberation \
      libmagickwand-dev \
    # docker-entrypoint.sh drops root to `app` with setpriv. If a future base
    # image stops shipping it, fail the build here rather than crash-loop the
    # container with nothing on port 8000.
    && command -v setpriv >/dev/null \
    && rm -rf /var/lib/apt/lists/* \
    # ImageMagick ships a default policy that blocks MoviePy's TextClip
    # (it reads captions via @/tmp/... paths). Neutralise the @-path
    # restriction in whichever policy file exists — Debian 12 ships
    # ImageMagick 6 (/etc/ImageMagick-6/policy.xml), Debian 13 ships
    # ImageMagick 7 (/etc/ImageMagick-7/policy.xml). Tolerating either
    # keeps the build stable across base-image updates.
    # Delete ANY line containing `pattern="@*"` — that's the restriction
    # that makes TextClip fail with "operation not allowed by the security
    # policy `@/tmp/…`". Also loosen `path` domain rights from "none" to
    # "read|write" in case the pattern is on a different line. Both edits
    # are idempotent and no-ops if the file doesn't exist.
    && for f in /etc/ImageMagick-6/policy.xml /etc/ImageMagick-7/policy.xml; do \
         if [ -f "$f" ]; then \
           sed -i '/pattern="@\*"/d' "$f"; \
           sed -i 's|domain="path" rights="none"|domain="path" rights="read\|write"|g' "$f"; \
           # Security review R2-14: what the loosening above must not open up.
           # TextClip only needs label/caption and PNG; these coders fetch URLs,
           # run drawing scripts or read arbitrary files, so they are off.
           sed -i 's|</policymap>|  <policy domain="coder" rights="none" pattern="URL" />\n  <policy domain="coder" rights="none" pattern="HTTP" />\n  <policy domain="coder" rights="none" pattern="HTTPS" />\n  <policy domain="coder" rights="none" pattern="FTP" />\n  <policy domain="coder" rights="none" pattern="MVG" />\n  <policy domain="coder" rights="none" pattern="MSL" />\n  <policy domain="coder" rights="none" pattern="EPHEMERAL" />\n</policymap>|' "$f"; \
           echo "Patched $f"; \
         fi; \
       done

COPY pyproject.toml ./
RUN pip install --upgrade pip && pip install -e .

COPY app ./app
# COPY keeps the checkout's file modes. A file that is owner-only on the
# developer's machine (0600) arrives owned by root and unreadable to `app`,
# and uvicorn dies with "Permission denied: /app/app/main.py" before it can
# bind a port. Normalise: root owns the code, everyone may read it, nobody but
# root may change it — the server needs no write access to its own source.
RUN chmod -R u=rwX,go=rX /app/app

# Which build this is.
#
# Stamped from deploy.sh's git short SHA — the same value used as the
# immutable image tag, so the line in the log and the tag in the registry
# name the same commit. Unset (a plain `docker compose build`) leaves it
# empty and app/main.py falls back to its own file mtime.
#
# This exists because the boot marker used to be a hand-typed constant with
# "bump this when you push" beside it. A five-day-old container ran a refine
# endpoint that did not exist in it, answering 404, while printing a build
# tag that looked perfectly current — because it always printed the same one.
#
# Last, and after COPY app, on purpose: it changes on every build, so any
# earlier position would invalidate the pip install layer every time.
ARG BUILD_ID=""
ENV BUILD_ID=$BUILD_ID

# `media` holds intermediate + final renders. Mount a volume in
# docker-compose so long renders survive container restarts.
#
# The server runs as an unprivileged `app` user (security review SEC-14): this
# service parses untrusted images and drives ffmpeg and ImageMagick, which is
# exactly where a parser bug would hand over the process. The entrypoint
# starts as root only to fix ownership of the media volume — a volume created
# by an older image is root-owned, and the worker could not write to it — and
# then drops to `app` before anything else runs.
RUN groupadd --system app && useradd --system --gid app --create-home --home-dir /home/app app \
 && mkdir -p /app/media/jobs && chown -R app:app /app/media
COPY --chmod=0755 docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
# The kernel runs this file directly, so its first bytes must be exactly
# "#!". An editor that saves a UTF-8 byte-order mark in front of them, or
# Windows line endings after them, turns every start into "exec format error"
# (BOM) or "not found" (CRLF) and a crash loop. Strip both here so the image
# never depends on how a checkout was saved, then fail the build — not the
# container — if the result is still not a shell script.
RUN sed -i '1s/^\xEF\xBB\xBF//; s/\r$//' /usr/local/bin/docker-entrypoint.sh \
 && head -c 2 /usr/local/bin/docker-entrypoint.sh | grep -q '#!' \
 && /bin/sh -n /usr/local/bin/docker-entrypoint.sh

EXPOSE 8000
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
