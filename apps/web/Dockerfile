# syntax=docker/dockerfile:1.7
#
# IMPORTANT: build this from the MONOREPO ROOT, not from apps/web/.
# The Dockerfile needs to reach packages/shared-types/ above apps/web/.
#
#   From the repo root:
#     docker build -f apps/web/Dockerfile -t <tag> .
#
#   Or use the npm script:
#     pnpm docker:web
#

# ---------- 1. deps ----------
# Installs the workspace's deps in an isolated layer that only invalidates
# when manifests change.
# deps and build run on the machine doing the build ($BUILDPLATFORM). Building
# Next.js under amd64 emulation on an Apple Silicon Mac was the slow half of a
# ship. The output is platform-independent: the standalone bundle is
# JavaScript, and this app has no native runtime modules (no sharp, no
# canvas), so only the runtime stage needs to be the target platform.
FROM --platform=$BUILDPLATFORM node:22-alpine AS deps
WORKDIR /repo
RUN npm install -g pnpm@9.0.0

COPY package.json pnpm-workspace.yaml pnpm-lock.yaml* ./
COPY apps/web/package.json apps/web/package.json
COPY packages/shared-types/package.json packages/shared-types/package.json
RUN pnpm install --frozen-lockfile || pnpm install


# ---------- 2. build ----------
# Builds the Next.js app in standalone mode. The output lives at
# /repo/apps/web/.next/standalone and contains its own minimal node_modules.
FROM --platform=$BUILDPLATFORM node:22-alpine AS build
WORKDIR /repo
RUN npm install -g pnpm@9.0.0
COPY --from=deps /repo /repo
COPY . .
RUN pnpm --filter web build


# ---------- 3. runtime ----------
# Final image: just the standalone server + static assets. No pnpm, no
# devDependencies, no source — typically ~120MB vs ~400MB before.
# Node 22 LTS (security review R2-07: Node 20 left support in April 2026).
FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV HOSTNAME=0.0.0.0

# Standalone bundle (server.js + minimal node_modules tracing).
#
# Owned by the image's unprivileged `node` user, which the server runs as
# (security review SEC-14). Owned rather than just readable because Next
# writes its image-optimisation cache under .next/cache at runtime.
COPY --from=build --chown=node:node /repo/apps/web/.next/standalone ./
# Static assets and public files aren't bundled into standalone — copy them.
COPY --from=build --chown=node:node /repo/apps/web/.next/static ./apps/web/.next/static
COPY --from=build --chown=node:node /repo/apps/web/public      ./apps/web/public
# Served from /api/health, as the API and ai-service serve theirs. See
# scripts/deploy.sh. Runtime only, so a new id never rebuilds the app.
ARG BUILD_ID=""
ENV BUILD_ID=$BUILD_ID
USER node

EXPOSE 3000
# server.js lives at the standalone root and uses HOSTNAME + PORT env vars.
CMD ["sh", "-c", "exec node apps/web/server.js"]
