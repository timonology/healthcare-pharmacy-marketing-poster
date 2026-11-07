#!/bin/sh
# Starts the ai-service as the unprivileged `app` user (security review SEC-14).
#
# Running as root here is only to hand the media volume to `app`: a volume
# made by an image from before this change is root-owned. The check keeps a
# large, already-correct volume from being walked on every start.
set -eu

if [ "$(id -u)" = "0" ]; then
  if [ -d /app/media ] && [ "$(stat -c %U /app/media)" != "app" ]; then
    chown -R app:app /app/media
  fi
  # setpriv changes the user, not the environment: point HOME at theirs, so
  # caches (matplotlib, imageio) land somewhere writable.
  export HOME=/home/app
  # Never fall back to running as root: that would quietly undo SEC-14.
  if ! command -v setpriv >/dev/null 2>&1; then
    echo "ai-service: setpriv is missing from this image, so it cannot drop root. Rebuild the image." >&2
    exit 1
  fi
  exec setpriv --reuid=app --regid=app --init-groups "$@"
fi

exec "$@"
