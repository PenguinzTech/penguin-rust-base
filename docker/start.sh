#!/bin/bash
# =============================================================================
# Penguin Rust Game Server — startup script
#
# Handles:
#   - File descriptor limits (Rust opens many files during entity init)
#   - Mono GC tuning (cap heap to reduce OOMKill during occlusion grid gen)
#   - Optional CPU affinity (pin server to specific cores for reduced jitter)
#   - Oxide data directory persisted on PVC (whitelist + permissions survive restarts)
#   - Clean SIGTERM handling (flush world state on pod termination)
# =============================================================================
set -euo pipefail

# ─── File descriptor limits ──────────────────────────────────────────────────
# Default system limit causes "too many open files" during entity initialization.
# Rust opens file handles for every entity, navmesh tile, and Oxide plugin.
# Non-root: can raise soft limit up to container hard limit (typically 1M+).
ulimit -n 65535

# ─── Mono GC tuning ──────────────────────────────────────────────────────────
# Controls peak memory during occlusion grid broadphase cell generation.
# MONO_MAX_HEAP defaults to 16g — override in values.yaml for larger worlds.
# Setting too low causes GC thrash; too high causes OOMKill at pod limit.
export MONO_GC_PARAMS="${MONO_GC_PARAMS:-max-heap-size=${MONO_MAX_HEAP:-16g}}"

# ─── CPU affinity (two-phase) ────────────────────────────────────────────────
# Phase 1 — world gen: no CPU restriction. Navmesh and terrain generation are
# multi-threaded and benefit from all available cores; capping here wastes time.
# Phase 2 — game loop: single-threaded. After the server opens UDP 28015
# (same signal as the K8s startup probe), a background watcher applies taskset
# to pin RustDedicated to dedicated cores, eliminating scheduler jitter.
# Set RUST_CPU_CORES=0,1 to enable phase-2 pinning; leave empty to skip.

# ─── Persist Oxide data directory on PVC ────────────────────────────────────
# Oxide stores the permission database (oxide.users.data, oxide.groups.data)
# and plugin data in /steamcmd/rust/oxide/data/ — ephemeral by default and
# wiped on every pod restart. Symlinking to the PVC keeps the whitelist and
# all Oxide permissions intact across restarts and redeployments.
#
# First boot: seeds PVC from image (captures COPY'd plugin configs).
# Subsequent boots: skips copy, just re-creates the symlink.
#
# Trade-off: after first boot, changes to docker/data/ in the image won't
# auto-apply. To reseed: delete /steamcmd/rust/server/oxide-data and restart.
OXIDE_DATA_PVC="/steamcmd/rust/server/oxide-data"
OXIDE_DATA_IMAGE="/steamcmd/rust/oxide/data"

if [ ! -d "${OXIDE_DATA_PVC}" ]; then
    echo "[startup] First boot: seeding Oxide data directory on PVC..."
    cp -rp "${OXIDE_DATA_IMAGE}" "${OXIDE_DATA_PVC}"
fi

rm -rf "${OXIDE_DATA_IMAGE}"
ln -sfT "${OXIDE_DATA_PVC}" "${OXIDE_DATA_IMAGE}"
echo "[startup] Oxide data directory linked to PVC (${OXIDE_DATA_PVC})"

# ─── Runtime plugin toggle ───────────────────────────────────────────────────
# Disable specific plugins at runtime without rebuilding the image.
# OXIDE_DISABLED_PLUGINS=Vanish,BGrade  (comma-separated, with or without .cs)
# Oxide only scans oxide/plugins/ root — subdirectories are ignored.
# Files restore from image on next restart, so toggle is purely env-var-controlled.
if [ -n "${OXIDE_DISABLED_PLUGINS:-}" ]; then
    mkdir -p /steamcmd/rust/oxide/plugins/disabled
    IFS=',' read -ra DISABLED_LIST <<< "${OXIDE_DISABLED_PLUGINS}"
    for PLUGIN in "${DISABLED_LIST[@]}"; do
        PLUGIN="${PLUGIN// /}"
        [ -z "$PLUGIN" ] && continue
        for candidate in \
            "/steamcmd/rust/oxide/plugins/${PLUGIN}" \
            "/steamcmd/rust/oxide/plugins/${PLUGIN}.cs"; do
            if [ -f "${candidate}" ]; then
                mv "${candidate}" /steamcmd/rust/oxide/plugins/disabled/
                echo "[startup] Plugin disabled: $(basename ${candidate})"
                break
            fi
        done
    done
fi

# ─── Admin provisioning ──────────────────────────────────────────────────────
# Writes ownerid entries to users.cfg before server starts (native Rust admin).
# Oxide plugin permissions are handled by AutoAdmin.cs after plugin load.
# Comma-separate multiple IDs: RUST_ADMIN_STEAMIDS=76561198xxx,76561198yyy
if [ -n "${RUST_ADMIN_STEAMIDS:-}" ]; then
    IDENTITY_DIR="/steamcmd/rust/server/${RUST_SERVER_IDENTITY:-rust_server}/cfg"
    mkdir -p "${IDENTITY_DIR}"
    # Rewrite clean on every boot so removed IDs are revoked immediately
    : > "${IDENTITY_DIR}/users.cfg"
    IFS=',' read -ra ADMIN_IDS <<< "${RUST_ADMIN_STEAMIDS}"
    for STEAMID in "${ADMIN_IDS[@]}"; do
        STEAMID="${STEAMID// /}"   # strip whitespace
        [ -z "$STEAMID" ] && continue
        echo "ownerid ${STEAMID} \"admin\" \"auto-provisioned\"" >> "${IDENTITY_DIR}/users.cfg"
        echo "[startup] Admin provisioned: ${STEAMID}"
    done
fi

# ─── RCON password ───────────────────────────────────────────────────────────
# Rust's Bootstrap.Init_Tier0 calls String.Replace(password, "***") for log
# sanitisation. Passing an empty string throws ArgumentException. Skip the
# +rcon.password flag entirely if no password is set — RCON will be disabled.
RCON_PASSWORD_ARG=""
if [ -n "${RUST_RCON_PASSWORD:-}" ]; then
    RCON_PASSWORD_ARG="+rcon.password ${RUST_RCON_PASSWORD}"
fi

# ─── SIGTERM handler ─────────────────────────────────────────────────────────
# K8s sends SIGTERM before SIGKILL. Rust needs time to flush world state.
# terminationGracePeriodSeconds in values.yaml must be >= server.saveinterval.
_term() {
    echo "[startup] SIGTERM received — sending save + quit to server..."
    # If RCON password is set, use it to gracefully save before exit
    if [ -n "${RUST_RCON_PASSWORD:-}" ] && command -v nc &>/dev/null; then
        echo "server.save" | nc -q1 127.0.0.1 "${RUST_RCON_PORT:-28016}" 2>/dev/null || true
    fi
    kill -TERM "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
}
trap _term SIGTERM SIGINT

# ─── Build metadata ──────────────────────────────────────────────────────────
# Sourced from file baked in at image build time (ARGs not available at runtime).
if [ -f /etc/rust-build-info ]; then
    # shellcheck source=/dev/null
    . /etc/rust-build-info
fi

# ─── Startup log ─────────────────────────────────────────────────────────────
echo "[startup] Rust dedicated server starting..."
echo "[startup] Oxide: ${OXIDE_VERSION:-unknown} | Steam build: ${STEAM_BUILD_ID:-unknown}"
echo "[startup] World: ${RUST_SERVER_WORLDSIZE:-3000} | Seed: ${RUST_SERVER_SEED:-12345}"
echo "[startup] MONO_GC_PARAMS: ${MONO_GC_PARAMS}"

# ─── Launch server ───────────────────────────────────────────────────────────
/steamcmd/rust/RustDedicated \
    -batchmode \
    +server.hostname "${RUST_SERVER_NAME:-Rust Server}" \
    +server.port "${RUST_SERVER_PORT:-28015}" \
    +server.maxplayers "${RUST_SERVER_MAXPLAYERS:-50}" \
    +server.worldsize "${RUST_SERVER_WORLDSIZE:-3000}" \
    +server.seed "${RUST_SERVER_SEED:-12345}" \
    +server.saveinterval "${RUST_SERVER_SAVE_INTERVAL:-300}" \
    +server.identity "${RUST_SERVER_IDENTITY:-rust_server}" \
    +rcon.port "${RUST_RCON_PORT:-28016}" \
    ${RCON_PASSWORD_ARG} \
    +rcon.web "${RUST_RCON_WEB:-1}" \
    +oxide.enabled 1 \
    ${RUST_SERVER_EXTRA_ARGS:-} &

SERVER_PID=$!

# ─── Phase 2: pin to game-loop cores after startup ───────────────────────────
# Polls /proc/net/udp until UDP 28015 is bound (same signal as the K8s startup
# probe). Applies taskset at that point so world gen gets full CPU burst but
# the game loop runs on dedicated cores, eliminating scheduler jitter.
if [ -n "${RUST_CPU_CORES:-}" ]; then
    (
        echo "[startup] Waiting for game port to open before applying CPU affinity..."
        while ! grep -q '00000000:6D6F' /proc/net/udp 2>/dev/null; do
            sleep 5
        done
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            taskset -cp "${RUST_CPU_CORES}" "$SERVER_PID" 2>/dev/null
            echo "[startup] Game-loop CPU pinned to cores ${RUST_CPU_CORES}"
        fi
    ) &
fi

wait "$SERVER_PID"
