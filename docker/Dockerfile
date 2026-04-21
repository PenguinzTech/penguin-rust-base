# syntax=docker/dockerfile:1
# =============================================================================
# Penguin Rust Base — Community Game Server Image
#
# Custom-built from Debian bookworm. Game files (SteamCMD app 258550) and
# Oxide mods are baked in at build time — no first-boot download wait.
#
# CI rebuilds automatically when Oxide or Steam build ID changes (every 4h check).
# Layer ordering is intentional: Steam layer is ~6GB and changes infrequently;
# Oxide layer is small and changes often. Oxide sits above Steam so the
# registry-side layer cache reuses the Steam download on Oxide-only updates.
#
# Build args:
#   OXIDE_VERSION  - Oxide.Rust release tag (e.g. 2.0.7271) — injected by CI
#   STEAM_BUILD_ID - Steam build ID for OCI label tracking — informational only
# =============================================================================

ARG OXIDE_VERSION
ARG STEAM_BUILD_ID

FROM debian:bookworm-slim

ARG OXIDE_VERSION
ARG STEAM_BUILD_ID

# Set pipefail on all RUN pipelines so a failure upstream of a pipe (e.g. curl)
# aborts the build instead of being masked by a successful downstream tar/unzip.
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# ─── Server defaults — override at runtime via -e or Helm env vars ───────────
# Left blank so start.sh auto-configures based on available resources on first boot.
# Set explicitly to skip auto-detection: docker run -e RUST_SERVER_WORLDSIZE=2000
ENV RUST_SERVER_WORLDSIZE=""
ENV RUST_SERVER_MAXPLAYERS=""

# ─── Server identity / browser listing ───────────────────────────────────────
ENV RUST_SERVER_NAME="Rust Server"
ENV RUST_SERVER_DESCRIPTION=""
ENV RUST_SERVER_TAGS=""
ENV RUST_SERVER_URL=""
ENV RUST_SERVER_HEADERIMAGE=""
ENV RUST_SERVER_LOGO=""

# ─── Server behaviour ────────────────────────────────────────────────────────
# RUST_SERVER_LEVEL: map type — "Procedural Map", "Barren", "HapisIsland", etc.
# RUST_SERVER_PVE / RUST_SERVER_RADIATION: set to 0 or 1 to override game default
# RUST_SERVER_TICKRATE / RUST_SERVER_FPS: leave blank to use game defaults
ENV RUST_SERVER_LEVEL="Procedural Map"
ENV RUST_SERVER_PVE=""
ENV RUST_SERVER_RADIATION=""
ENV RUST_SERVER_TICKRATE=""
ENV RUST_SERVER_FPS=""

# ─── Mod framework / PvP toggles ─────────────────────────────────────────────
# OXIDE=1 (default) keeps Oxide + umod plugins loaded.
# OXIDE=0 restores vanilla RustDedicated from the baked Managed.vanilla/ snapshot
#   and removes oxide/ — no plugins, no Oxide hooks, pure Facepunch binary.
#
# PVP_MODE only applies when OXIDE=1. It flips TruePVE's global
# `defaultAllowDamage` — TruePVE stays loaded in both modes so admins can
# configure zones, rulesets, and per-entity exceptions either way.
#   PVP_MODE=1 (default) → defaultAllowDamage=true   (PvP globally; carve out
#                                                     PvE zones via TruePVE)
#   PVP_MODE=0           → defaultAllowDamage=false  (PvE globally; carve out
#                                                     PvP zones via TruePVE)
# When OXIDE=0 the server is pure vanilla + full PvP; PVP_MODE is ignored.
ENV OXIDE="1"
ENV PVP_MODE="1"

# ─── Plugin source & selection ───────────────────────────────────────────────
# PLUGIN_SOURCE controls the resolution strategy for plugins in RUST_PLUGINS:
#   github (default) — full priority chain: baked (hash-checked) → GitHub release
#                      → umod fallback (if PLUGIN_UMOD_FALLBACK=1) → skip
#   baked            — baked plugins only; no network calls (fastest cold start)
#   umod             — always fetch direct from umod.org (bypasses scan pipeline)
#
# PLUGIN_UMOD_FALLBACK: in github mode, whether to try umod.org when a slug is not
#   found in penguin-rust-plugins releases (default 1 = enabled).
#   Set to 0 to disable umod entirely and hard-fail on unknown slugs.
#
# RUST_PLUGINS: comma-separated slug list (e.g. truepve,gather-manager).
#   Empty (default) leaves all plugins disabled. Non-empty activates listed slugs.
ENV PLUGIN_SOURCE="github"
ENV PLUGIN_UMOD_FALLBACK="1"
ENV RUST_PLUGINS=""

# ─── Wipe schedule ───────────────────────────────────────────────────────────
# WIPE_SCHED (blank) = wipe on first Thursday of month (Facepunch forced-wipe day)
# WIPE_SCHED=1w|2w|3w = every N weeks on WIPE_DAY
# WIPE_SCHED=off = no automated wipes
# WIPE_TIME is when the wipe actually happens; RCON warnings start 60 min earlier.
ENV WIPE_SCHED=""
ENV WIPE_DAY="Th"
ENV WIPE_TIME="06:00"
ENV WIPE_BP="false"

# ─── DDoS protection (opt-in) ────────────────────────────────────────────────
# DDOS_PROTECT=1 enables iptables hashlimit per-source-IP UDP rate limiting.
# Requires NET_ADMIN capability on the container. Default off so the image
# runs capability-free. See docs/ddos-protection.md for tuning guidance.
ENV DDOS_PROTECT="0"
ENV DDOS_UDP_RATE="60"
ENV DDOS_UDP_BURST="120"

# SteamCMD requires 32-bit glibc compatibility layer.
# Create non-root service account before installing files so ownership is clean.
# DL3008 ignored: community base rebuilds every 4h; pinning apt versions across
# Debian point releases makes auto-rebuilds brittle without a security benefit.
# hadolint ignore=DL3008
RUN dpkg --add-architecture i386 && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        default-mysql-client \
        iptables \
        jq \
        lib32gcc-s1 \
        numactl \
        procps \
        unzip \
    && rm -rf /var/lib/apt/lists/* && \
    groupadd -r rustserver && \
    useradd -r -g rustserver -u 1000 -m -d /home/rustserver rustserver

# websocat enables WebSocket RCON commands from start.sh (server.maxplayers, config.save).
# Not packaged for Debian — install static musl binary from upstream GitHub release.
# Pinned to v1.13.0 (last stable as of image design); update via PR when a new release lands.
ARG WEBSOCAT_VERSION=1.13.0
RUN curl -fsSL \
    "https://github.com/vi/websocat/releases/download/v${WEBSOCAT_VERSION}/websocat.x86_64-unknown-linux-musl" \
    -o /usr/local/bin/websocat && \
    chmod +x /usr/local/bin/websocat

# supercronic — cron for containers. Runs as non-root, logs to stdout, reads
# standard crontab syntax. Used for plugin update checks, future scheduled tasks.
# Pinned to v0.2.44 (last stable as of 2026-03); update via PR when new release lands.
ARG SUPERCRONIC_VERSION=0.2.44
RUN curl -fsSL \
    "https://github.com/aptible/supercronic/releases/download/v${SUPERCRONIC_VERSION}/supercronic-linux-amd64" \
    -o /usr/local/bin/supercronic && \
    chmod +x /usr/local/bin/supercronic

# Install SteamCMD under /steamcmd — rustserver will own it
RUN mkdir -p /steamcmd && \
    curl -sqL "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz" \
    | tar zxvf - -C /steamcmd && \
    chown -R rustserver:rustserver /steamcmd

# ─── Steam layer (~6GB) ──────────────────────────────────────────────────────
# Bake Rust dedicated server game files at build time.
# Run as rustserver so all game files are already owned correctly.
# PVC is mounted at /steamcmd/rust/server (saves only) — game files stay in image layer.
USER rustserver
RUN /steamcmd/steamcmd.sh \
    +login anonymous \
    +force_install_dir /steamcmd/rust \
    +app_update 258550 validate \
    +quit

# SteamAPI_Init searches ~/.steam/sdk64/steamclient.so (hardcoded Valve path).
# SteamCMD installs the library to /steamcmd/linux64/ — symlink bridges the two.
# Without this, Steam server init fails and the server exits cleanly (exit 0).
RUN mkdir -p /home/rustserver/.steam/sdk64 && \
    ln -s /steamcmd/linux64/steamclient.so /home/rustserver/.steam/sdk64/steamclient.so

# ─── Oxide layer (~50MB) ─────────────────────────────────────────────────────
# Sits above Steam layer so it rebuilds independently on Oxide releases.
# -o overwrites game-managed files (e.g. Assembly-CSharp.dll) without prompting.
# Before unzipping, snapshot Managed/ as Managed.vanilla/ so OXIDE=0 at runtime
# can restore vanilla without a Steam round-trip (~50MB layer cost; acceptable).
RUN cp -a /steamcmd/rust/RustDedicated_Data/Managed \
          /steamcmd/rust/RustDedicated_Data/Managed.vanilla && \
    echo "Downloading Oxide ${OXIDE_VERSION}..." && \
    curl -fsSL \
    "https://github.com/OxideMod/Oxide.Rust/releases/download/${OXIDE_VERSION}/Oxide.Rust-linux.zip" \
    -o /tmp/oxide.zip && \
    unzip -o /tmp/oxide.zip -d /steamcmd/rust && \
    rm /tmp/oxide.zip

# ─── Scanned-plugin layer (penguin-rust-plugins OCI artifacts) ───────────────
# Plugins are fetched and scanned (ClamAV + YARA + Semgrep + gitleaks + trivy)
# in the separate penguin-rust-plugins repo, then published as per-plugin OCI
# artifacts at ghcr.io/penguinztech/penguin-rust-plugins/${slug}:latest.
# :latest only moves after a clean scan, so consuming it here propagates the
# scan gate implicitly. See docker/plugin-refs.txt for the registry and
# .github/workflows/build-image.yml for the `oras pull` staging logic.
#
# staged-plugins/ is populated by the CI workflow before `docker build` runs.
# Each staged-plugins/${slug}/ contains: ${FileName}.cs, ${slug}.hash,
# provenance.json, sbom.cdx.json, ATTRIBUTION.md, reports/.
COPY --chown=rustserver:rustserver staged-plugins/ /etc/penguin-rust-plugins/per-plugin/

# umod-plugins.txt remains on the image — start.sh reads it under PLUGIN_SOURCE=umod
# to know which slugs to fetch directly from umod.org at runtime (scan-bypass mode).
COPY --chown=rustserver:rustserver umod-plugins.txt /etc/penguin-rust-plugins/umod-plugins.txt

# Verify every plugin matches its committed hash at build time.
# Hash mismatch = the OCI artifact was tampered between cosign-sign and our pull —
# fail the build rather than ship a bad plugin.
# Plugin staging (copy to oxide/plugins/) is deferred to start.sh so RUST_PLUGINS
# env var can control which plugins are loaded at runtime.
# DL3003 (use WORKDIR): intentional — this is a single RUN with bounded, local cd
# purely to iterate slugs; WORKDIR would leave us in /etc/penguin-rust-plugins/per-plugin
# for subsequent layers, which is not what we want.
# hadolint ignore=DL3003
RUN set -eu; \
    cd /etc/penguin-rust-plugins/per-plugin; \
    for dir in */; do \
      slug="${dir%/}"; \
      (cd "${slug}" && sha256sum -c "${slug}.hash") \
        || { echo "FATAL: ${slug} hash mismatch — refusing to build" >&2; exit 1; }; \
    done; \
    mkdir -p /steamcmd/rust/oxide/plugins/disabled; \
    find /etc/penguin-rust-plugins/per-plugin -maxdepth 2 -name '*.cs' | \
        while IFS= read -r cs; do \
            gzip -c "${cs}" > "/steamcmd/rust/oxide/plugins/disabled/$(basename "${cs}").gz"; \
        done; \
    find /etc/penguin-rust-plugins/per-plugin -maxdepth 2 -name '*.hash' \
        -exec cp {} /steamcmd/rust/oxide/plugins/disabled/ \;

# ─── Deconflict staged plugins ───────────────────────────────────────────────
# Some slugs define C# classes that conflict with our patched versions.
# Remove conflicting staged plugins from disabled/ so only patched versions load.
# Conflicts:
#   - vehiclelicense slug → VehicleLicense.cs.gz (conflicts with patched VehicleLicence.cs)
#   - treeplanter slug → treeplanter.cs.gz (conflicts with patched TreePlanter.cs)
#   - rustkits slug → RustKits.cs.gz (conflicts with patched Kits.cs)
# Use -f to avoid failure if files don't exist (penguin-rust-plugins may not publish all).
RUN rm -f \
    /steamcmd/rust/oxide/plugins/disabled/VehicleLicense.cs.gz \
    /steamcmd/rust/oxide/plugins/disabled/treeplanter.cs.gz \
    /steamcmd/rust/oxide/plugins/disabled/RustKits.cs.gz

# ─── Built-in plugins ────────────────────────────────────────────────────────
# AutoAdmin and PluginManager are always-on built-ins — copied directly into
# oxide/plugins/ so they load on every server unconditionally.
#   AutoAdmin    — auto-provisions admin group permissions from RUST_ADMIN_STEAMIDS
#   ExtensionManager — runtime /extension and extension.{add,remove,update,list} commands
#
# plugins/patched/ — upstream umod plugins with removed Oxide API fixes applied:
#   FindByID→FindAwakeOrSleeping, net.connection→Connection,
#   ConVar.Chat.ChatChannel→Chat.ChatChannel, BuildingPrivlidge→BuildingPrivilege,
#   CommunityEntity.ServerInstance→CuiHelper foreach loop.
# These are excluded from the penguin-rust-plugins scan pipeline and baked here
# instead so servers get working patched versions. Oxide only scans oxide/plugins/
# root — subdirectories are ignored, so patched plugins are copied to root.
COPY --chown=rustserver:rustserver plugins/AutoAdmin.cs /steamcmd/rust/oxide/plugins/
COPY --chown=rustserver:rustserver plugins/ExtensionManager.cs /steamcmd/rust/oxide/plugins/
COPY --chown=rustserver:rustserver plugins/patched/ /steamcmd/rust/oxide/plugins/
# First-party optional plugins — bake into disabled/ with hash sidecars so
# activate_plugin can find them via RUST_PLUGINS without falling through to
# the GitHub/umod fetch path (which would fail for plugins not in any registry).
# Hash slug is lowercase to match the activate_plugin lookup key.
COPY --chown=rustserver:rustserver plugins/MorningFog.cs plugins/SafeSpace.cs /tmp/firstparty/
RUN cd /tmp/firstparty && \
    sha256sum MorningFog.cs > /steamcmd/rust/oxide/plugins/disabled/morningfog.hash && \
    gzip -c MorningFog.cs  > /steamcmd/rust/oxide/plugins/disabled/MorningFog.cs.gz && \
    sha256sum SafeSpace.cs  > /steamcmd/rust/oxide/plugins/disabled/safespace.hash && \
    gzip -c SafeSpace.cs   > /steamcmd/rust/oxide/plugins/disabled/SafeSpace.cs.gz && \
    rm -rf /tmp/firstparty
RUN cd /steamcmd/rust/oxide/plugins && \
    sha256sum Pets2.cs > disabled/pets2.hash && \
    gzip -c Pets2.cs  > disabled/Pets2.cs.gz && \
    rm Pets2.cs

# ─── Shared library ─────────────────────────────────────────────────────────
# Functions sourced by start.sh, check-plugin-updates.sh, and wipe-check.sh.
COPY --chown=rustserver:rustserver lib-functions.sh /usr/local/lib/penguin-rust/lib-functions.sh

# ─── Scheduled tasks (supercronic) ──────────────────────────────────────────
# Base crontab has static entries (plugin update checker). start.sh appends
# dynamic entries (wipe schedule) computed from env vars at boot.
COPY --chown=rustserver:rustserver crontab /etc/penguin-rust/crontab
COPY --chown=rustserver:rustserver check-plugin-updates.sh /usr/local/bin/check-plugin-updates.sh
COPY --chown=rustserver:rustserver wipe-check.sh /usr/local/bin/wipe-check.sh
COPY --chown=rustserver:rustserver manage-plugin.sh /usr/local/bin/manage-plugin.sh
COPY --chown=rustserver:rustserver user-sync.sh /usr/local/bin/user-sync.sh
RUN chmod +x /usr/local/bin/check-plugin-updates.sh /usr/local/bin/wipe-check.sh \
             /usr/local/bin/manage-plugin.sh /usr/local/bin/user-sync.sh

# ─── Startup script ──────────────────────────────────────────────────────────
COPY --chown=rustserver:rustserver start.sh /start.sh
RUN chmod +x /start.sh

# ─── Build metadata ──────────────────────────────────────────────────────────
# Bake build args into a file so start.sh can source them at runtime.
# ARGs are build-time only and don't survive into the container environment.
# Must run as root — rustserver (UID 1000) cannot write to /etc.
USER root
RUN echo "OXIDE_VERSION=${OXIDE_VERSION}" > /etc/rust-build-info && \
    echo "STEAM_BUILD_ID=${STEAM_BUILD_ID}" >> /etc/rust-build-info
USER rustserver

# ─── OCI labels ──────────────────────────────────────────────────────────────
# Consumed by CI workflow (skopeo inspect) to detect whether a rebuild is needed.
LABEL org.opencontainers.image.source="https://github.com/PenguinzTech/penguin-rust-base" \
      org.opencontainers.image.description="Rust dedicated server with Oxide + scanned plugins — community base image" \
      dev.penguinztech.oxide-version="${OXIDE_VERSION}" \
      dev.penguinztech.steam-build-id="${STEAM_BUILD_ID}"

WORKDIR /steamcmd/rust

# Health check: verify RustDedicated is running (no curl/wget per project rules).
# start-period gives ~10 min for world generation on first boot before health
# reports kick in — prevents K8s/Docker from killing the pod during startup.
HEALTHCHECK --interval=60s --timeout=5s --start-period=600s --retries=3 \
    CMD pgrep -f RustDedicated > /dev/null || exit 1

ENTRYPOINT ["/start.sh"]
