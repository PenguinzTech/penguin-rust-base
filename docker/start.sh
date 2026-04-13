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

# ─── Auto-configure server for available resources ────────────────────────────
# On first deployment (no lock file), detects available CPUs and memory and picks
# sensible worldSize + maxPlayers defaults. Skips on subsequent restarts so Rust's
# on-disk world state is never invalidated by a changed worldSize.
# To re-trigger detection: delete the lock file and restart.
#
# Tier table (memory-primary; CPU count used as secondary floor):
#   < 4GB,  ≥1 CPU  → worldSize=750,  maxPlayers=10
#   4–7GB,  ≥1 CPU  → worldSize=2000, maxPlayers=40
#   8–15GB, ≥2 CPUs → worldSize=3000, maxPlayers=75
#   16–31GB,≥4 CPUs → worldSize=4000, maxPlayers=100
#   32+GB,  ≥4 CPUs → worldSize=4500, maxPlayers=150
#
# Env vars RUST_SERVER_WORLDSIZE and RUST_SERVER_MAXPLAYERS always win when set.
AUTOCONFIG_LOCK="/steamcmd/rust/server/${RUST_SERVER_IDENTITY:-rust_server}/.auto-config.lock"

if [ ! -f "${AUTOCONFIG_LOCK}" ]; then
    MEM_KB=$(grep MemTotal /proc/meminfo | awk '{print $2}')
    MEM_GB=$((MEM_KB / 1024 / 1024))
    CPUS=$(nproc)
    echo "[startup] Auto-config: detected ${CPUS} CPU(s), ${MEM_GB}GB RAM"

    # Select tier — drop one tier if CPU count is below the floor
    if   [ "$MEM_GB" -ge 32 ] && [ "$CPUS" -ge 4 ]; then
        AUTO_WORLDSIZE=4500; AUTO_MAXPLAYERS=150
    elif [ "$MEM_GB" -ge 16 ] && [ "$CPUS" -ge 4 ]; then
        AUTO_WORLDSIZE=4000; AUTO_MAXPLAYERS=100
    elif [ "$MEM_GB" -ge 8  ] && [ "$CPUS" -ge 2 ]; then
        AUTO_WORLDSIZE=3000; AUTO_MAXPLAYERS=75
    elif [ "$MEM_GB" -ge 4  ] && [ "$CPUS" -ge 1 ]; then
        AUTO_WORLDSIZE=2000; AUTO_MAXPLAYERS=40
    else
        AUTO_WORLDSIZE=750;  AUTO_MAXPLAYERS=10
    fi

    # Apply only when not explicitly set by the operator
    APPLIED_WORLDSIZE="${RUST_SERVER_WORLDSIZE:-}"
    APPLIED_MAXPLAYERS="${RUST_SERVER_MAXPLAYERS:-}"

    if [ -z "${RUST_SERVER_WORLDSIZE:-}" ]; then
        export RUST_SERVER_WORLDSIZE="${AUTO_WORLDSIZE}"
        APPLIED_WORLDSIZE="${AUTO_WORLDSIZE} (auto)"
    else
        APPLIED_WORLDSIZE="${RUST_SERVER_WORLDSIZE} (explicit)"
    fi

    if [ -z "${RUST_SERVER_MAXPLAYERS:-}" ]; then
        export RUST_SERVER_MAXPLAYERS="${AUTO_MAXPLAYERS}"
        APPLIED_MAXPLAYERS="${AUTO_MAXPLAYERS} (auto)"
    else
        APPLIED_MAXPLAYERS="${RUST_SERVER_MAXPLAYERS} (explicit)"
    fi

    echo "[startup] Auto-config: worldSize=${APPLIED_WORLDSIZE}, maxPlayers=${APPLIED_MAXPLAYERS}"

    # Write lock file — records detected hardware and applied values for reference
    mkdir -p "$(dirname "${AUTOCONFIG_LOCK}")"
    printf '# Auto-config lock — delete this file to re-trigger resource detection on next start.\n' > "${AUTOCONFIG_LOCK}"
    printf '# Generated: %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" >> "${AUTOCONFIG_LOCK}"
    printf 'DETECTED_CPUS=%s\n' "${CPUS}" >> "${AUTOCONFIG_LOCK}"
    printf 'DETECTED_MEM_GB=%s\n' "${MEM_GB}" >> "${AUTOCONFIG_LOCK}"
    printf 'AUTO_WORLDSIZE=%s\n' "${AUTO_WORLDSIZE}" >> "${AUTOCONFIG_LOCK}"
    printf 'AUTO_MAXPLAYERS=%s\n' "${AUTO_MAXPLAYERS}" >> "${AUTOCONFIG_LOCK}"
    printf 'APPLIED_WORLDSIZE=%s\n' "${RUST_SERVER_WORLDSIZE}" >> "${AUTOCONFIG_LOCK}"
    printf 'APPLIED_MAXPLAYERS=%s\n' "${RUST_SERVER_MAXPLAYERS}" >> "${AUTOCONFIG_LOCK}"
    echo "[startup] Auto-config locked: ${AUTOCONFIG_LOCK}"
fi

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
# If RUST_RCON_PASSWORD is not set, generate a random 31-char alphanumeric
# password and persist it to the PVC so it survives restarts. On subsequent
# boots the saved password is read back, keeping WebRCON accessible without
# requiring the operator to set anything explicitly.
# Password file: /steamcmd/rust/server/<identity>/.rcon.pw (on PVC)
RCON_PW_FILE="/steamcmd/rust/server/${RUST_SERVER_IDENTITY:-rust_server}/.rcon.pw"
if [ -z "${RUST_RCON_PASSWORD:-}" ]; then
    if [ -f "${RCON_PW_FILE}" ]; then
        RUST_RCON_PASSWORD=$(cat "${RCON_PW_FILE}")
        echo "[startup] RCON password loaded from ${RCON_PW_FILE}"
    else
        RUST_RCON_PASSWORD=$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 31)
        mkdir -p "$(dirname "${RCON_PW_FILE}")"
        printf '%s\n' "${RUST_RCON_PASSWORD}" > "${RCON_PW_FILE}"
        chmod 600 "${RCON_PW_FILE}"
        echo "[startup] RCON password generated and saved to ${RCON_PW_FILE}"
    fi
    export RUST_RCON_PASSWORD
fi

# Rust's Bootstrap.Init_Tier0 calls String.Replace(password, "***") for log
# sanitisation. Passing an empty string throws ArgumentException — always set
# by this point (either explicit env var or generated above).
RCON_PASSWORD_ARG="+rcon.password ${RUST_RCON_PASSWORD}"

# ─── RCON helper ─────────────────────────────────────────────────────────────
# Sends a single command to the server via WebSocket RCON (rcon.web 1).
# Requires websocat (installed in image) and RUST_RCON_PASSWORD to be set.
# Silently no-ops if either is missing — never blocks server startup.
send_rcon() {
    local cmd="$1"
    [ -z "${RUST_RCON_PASSWORD:-}" ] && return 0
    command -v websocat >/dev/null 2>&1 || return 0
    printf '{"Identifier":1,"Message":"%s","Name":"start.sh"}\n' "${cmd}" | \
        websocat -n1 --no-close --timeout 5 \
        "ws://127.0.0.1:${RUST_RCON_PORT:-28016}/${RUST_RCON_PASSWORD}" \
        >/dev/null 2>&1 || true
}

# ─── SIGTERM handler ─────────────────────────────────────────────────────────
# K8s sends SIGTERM before SIGKILL. Rust needs time to flush world state.
# terminationGracePeriodSeconds in values.yaml must be >= server.saveinterval.
_term() {
    echo "[startup] SIGTERM received — sending save + quit to server..."
    # If RCON password is set, use it to gracefully save before exit
    send_rcon "server.save"
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

# ─── Phase 2b: periodic maxPlayers adjustment based on runtime RSS ────────────
# After world gen completes, measures actual RustDedicated RSS and adjusts
# server.maxplayers via RCON. Post-world-gen RSS is a far better signal than
# available memory at boot — it captures navmesh, plugin heap, and entity data.
# Persists the value via config.save so it survives server reloads.
#
# RSS tier table (post-world-gen; more conservative than boot-time tiers):
#   < 4GB   → 10 players     4–5GB  → 40 players
#   6–9GB   → 75 players     10–15GB → 100 players    16+GB → 150 players
#
# Requires RUST_RCON_PASSWORD. Interval: RUST_MAXPLAYERS_CHECK_INTERVAL
# (default 1800s / 30 min). Set to 0 to disable.
_MP_INTERVAL="${RUST_MAXPLAYERS_CHECK_INTERVAL:-1800}"
if [ -n "${RUST_RCON_PASSWORD:-}" ] && [ "${_MP_INTERVAL}" -gt 0 ]; then
    (
        # Wait for the game port to open (world gen complete)
        while ! grep -q '00000000:6D6F' /proc/net/udp 2>/dev/null; do
            sleep 5
        done
        echo "[autoconfig] World gen complete — waiting 60s before first maxPlayers adjustment..."
        sleep 60

        while kill -0 "$SERVER_PID" 2>/dev/null; do
            RSS_KB=$(awk '/VmRSS/{print $2}' /proc/$SERVER_PID/status 2>/dev/null || echo 0)
            RSS_GB=$((RSS_KB / 1024 / 1024))

            if   [ "$RSS_GB" -ge 16 ]; then NEW_MP=150
            elif [ "$RSS_GB" -ge 10 ]; then NEW_MP=100
            elif [ "$RSS_GB" -ge 6  ]; then NEW_MP=75
            elif [ "$RSS_GB" -ge 4  ]; then NEW_MP=40
            else                            NEW_MP=10
            fi

            echo "[autoconfig] RSS ${RSS_GB}GB → server.maxplayers ${NEW_MP}"
            send_rcon "server.maxplayers ${NEW_MP}"
            send_rcon "config.save"

            sleep "${_MP_INTERVAL}"
        done
    ) &
fi

wait "$SERVER_PID"
