# syntax=docker/dockerfile:1.7
# Build context is the monorepo root (see docker-compose.yml).
FROM node:20-alpine
WORKDIR /repo

RUN npm install -g pnpm@9.0.0

# Install deps in a layer that only invalidates when manifests change.
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml* ./
COPY apps/web/package.json apps/web/package.json
COPY packages/shared-types/package.json packages/shared-types/package.json
RUN pnpm install --frozen-lockfile || pnpm install

# Now bring in the rest of the source.
COPY . .

RUN pnpm --filter web build

WORKDIR /repo/apps/web
ENV HOSTNAME=0.0.0.0
EXPOSE 3000
# Bind to whatever port the host injects (Railway sets $PORT). Falls back
# to 3000 for local docker-compose where PORT isn't set.
CMD ["sh", "-c", "exec node_modules/.bin/next start -H 0.0.0.0 -p ${PORT:-3000}"]
