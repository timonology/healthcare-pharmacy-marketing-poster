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
FROM node:20-alpine AS deps
WORKDIR /repo
RUN npm install -g pnpm@9.0.0

COPY package.json pnpm-workspace.yaml pnpm-lock.yaml* ./
COPY apps/web/package.json apps/web/package.json
COPY packages/shared-types/package.json packages/shared-types/package.json
RUN pnpm install --frozen-lockfile || pnpm install


# ---------- 2. build ----------
# Builds the Next.js app in standalone mode. The output lives at
# /repo/apps/web/.next/standalone and contains its own minimal node_modules.
FROM node:20-alpine AS build
WORKDIR /repo
RUN npm install -g pnpm@9.0.0
COPY --from=deps /repo /repo
COPY . .
RUN pnpm --filter web build


# ---------- 3. runtime ----------
# Final image: just the standalone server + static assets. No pnpm, no
# devDependencies, no source — typically ~120MB vs ~400MB before.
FROM node:20-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production
ENV HOSTNAME=0.0.0.0

# Standalone bundle (server.js + minimal node_modules tracing).
COPY --from=build /repo/apps/web/.next/standalone ./
# Static assets and public files aren't bundled into standalone — copy them.
COPY --from=build /repo/apps/web/.next/static ./apps/web/.next/static
COPY --from=build /repo/apps/web/public      ./apps/web/public

EXPOSE 3000
# server.js lives at the standalone root and uses HOSTNAME + PORT env vars.
CMD ["sh", "-c", "exec node apps/web/server.js"]
