# syntax=docker/dockerfile:1.7
# The SDK stage runs on the machine doing the build ($BUILDPLATFORM), not the
# target. On an Apple Silicon Mac shipping to amd64, running the SDK under
# emulation made restore and publish take the best part of an hour, and
# sometimes hang outright. Nothing here needs to: the publish below is
# framework-dependent and portable (UseAppHost=false, no runtime identifier),
# so it is plain IL plus every platform's native libraries under runtimes/.
# Only the runtime stage is the target platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Acme.sln ./
COPY src/Acme.Domain/*.csproj         src/Acme.Domain/
COPY src/Acme.Application/*.csproj    src/Acme.Application/
COPY src/Acme.Infrastructure/*.csproj src/Acme.Infrastructure/
COPY src/Acme.WebApi/*.csproj         src/Acme.WebApi/
COPY tests/Acme.Tests/*.csproj        tests/Acme.Tests/
RUN dotnet restore Acme.sln

COPY . .
RUN dotnet publish src/Acme.WebApi/Acme.WebApi.csproj -c Release -o /app /p:UseAppHost=false \
 # Published content files (appsettings*.json, the house template JSON and
 # PNGs) keep the checkout's modes. Owner-only files would arrive root-owned
 # and unreadable to $APP_UID — the same crash the ai-service hit. Read for
 # everyone, write for root only.
 && chmod -R u=rwX,go=rX /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install fonts so QuestPDF / SkiaSharp can actually render text in exported
# PDFs. Without these, TTF glyph lookup silently returns nothing and posters
# ship as "shapes and colors with no text". fonts-liberation covers the
# Helvetica/Arial family; fonts-dejavu-core is a broad fallback.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      fontconfig \
      fonts-liberation \
      fonts-dejavu-core \
 && fc-cache -f \
 && rm -rf /var/lib/apt/lists/*
# Note: ffmpeg lives in the ai-service Dockerfile now — the .NET API no
# longer renders video (Python worker owns the pipeline).

EXPOSE 8080
COPY --from=build /app .
# Which build this is, served from /health. scripts/deploy.sh passes the same
# SHA tag the image is pushed under, so "is production running what I just
# shipped?" — and "are the API and web app on the same build?" — can be
# answered from outside. Last, so changing it never invalidates a cached layer.
ARG BUILD_ID=""
ENV BUILD_ID=$BUILD_ID
# Run as the image's built-in unprivileged user (security review SEC-14): a
# flaw in the API, or in a library it parses untrusted files with, then lands
# as a user who cannot change the image or read other containers' mounts.
# The API writes nothing to disk, and 8080 needs no privilege.
USER $APP_UID
# Bind to $PORT when set (Railway), 8080 otherwise (local docker-compose).
ENTRYPOINT ["sh", "-c", "exec dotnet Acme.WebApi.dll --urls=http://+:${PORT:-8080}"]
