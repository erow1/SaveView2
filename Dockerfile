# syntax=docker/dockerfile:1.7
# ──────────────────────────────────────────────────────────────────────────────
# SafeView — produkcyjny obraz Blazor Server (.NET 10).
# Build multi-stage; SDK builduje, runtime hostuje. FFmpeg + ICU dla detekcji + lokalizacji.
# ──────────────────────────────────────────────────────────────────────────────

ARG DOTNET_SDK=10.0
ARG DOTNET_RUNTIME=10.0

# ─── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK} AS build
WORKDIR /src

# Cache packages
COPY Directory.Packages.props nuget.config* ./
COPY SafeView.slnx ./
COPY src ./src
COPY tools ./tools

RUN dotnet restore SafeView.slnx
RUN dotnet publish src/SafeView.Web/SafeView.Web.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

# ─── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME} AS runtime
WORKDIR /app

# FFmpeg — dla snapshotów RTSP; tini — porządne zamykanie procesu; ca-certs.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg tini ca-certificates \
 && rm -rf /var/lib/apt/lists/*

# Storage — montowany jako wolumen (frames/clips/models/licenses/reports)
RUN mkdir -p /app/storage/frames /app/storage/clips /app/storage/models \
             /app/storage/uploads /app/storage/reports /app/storage/licenses

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

# Healthcheck korzysta z /health/live (anonimowe).
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget -qO- http://localhost:8080/health/live || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "SafeView.Web.dll"]
