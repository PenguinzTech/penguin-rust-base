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

# ─── Shared functions ────────────────────────────────────────────────────────
# shellcheck source=lib-functions.sh
. /usr/local/lib/penguin-rust/lib-functions.sh

# ─── File descriptor limits ──────────────────────────────────────────────────
# Default system limit causes "too many open files" during entity initialization.
# Rust opens file handles for every entity, navmesh tile, and Oxide plugin.
# Non-root: can raise soft limit up to container hard limit (typically 1M+).
ulimit -n 65535

# ─── DDoS protection (opt-in) ────────────────────────────────────────────────
# DDOS_PROTECT=1 enables per-source-IP UDP rate limiting on the game port via
# iptables hashlimit. Drops packets from IPs exceeding the configured rate
# entirely in kernel space, so Rust never sees flood traffic.
#
# Requires the container to have NET_ADMIN capability:
#   docker run --cap-add=NET_ADMIN ...
#   K8s: securityContext.capabilities.add: ["NET_ADMIN"]
#
# Default OFF so the image runs capability-free unless operators opt in.
# If DDOS_PROTECT=1 but NET_ADMIN is missing, we log and continue (no crash).
#
# Tuning:
#   DDOS_UDP_RATE   — sustained packets/sec per source IP (default 60)
#   DDOS_UDP_BURST  — burst allowance before rate-limit kicks in (default 120)
#
# Rust clients normally send ~30-40 pps during gameplay; 60/120 gives headroom
# while still shutting down floods (>10k pps per IP is typical in a volumetric attack).
if [ "${DDOS_PROTECT:-0}" = "1" ]; then
    _DDOS_RATE="${DDOS_UDP_RATE:-60}"
    _DDOS_BURST="${DDOS_UDP_BURST:-120}"
    _DDOS_PORT="${RUST_SERVER_PORT:-28015}"

    # iptables needs root + NET_ADMIN. Run via sudo-less iptables binary; if the
    # cap isn't granted, the call fails with "Permission denied" — we swallow it.
    if iptables -N RUST_DDOS 2>/dev/null && \
       iptables -A RUST_DDOS \
           -m hashlimit \
           --hashlimit-above "${_DDOS_RATE}/sec" \
           --hashlimit-burst "${_DDOS_BURST}" \
           --hashlimit-mode srcip \
           --hashlimit-name rust_ddos \
           --hashlimit-htable-expire 60000 \
           -j DROP 2>/dev/null && \
       iptables -A RUST_DDOS -j ACCEPT 2>/dev/null && \
       iptables -I INPUT -p udp --dport "${_DDOS_PORT}" -j RUST_DDOS 2>/dev/null; then
        echo "[startup] DDoS protection active — UDP/${_DDOS_PORT} limited to ${_DDOS_RATE}pps/srcip (burst ${_DDOS_BURST})"
    else
        echo "[startup] WARNING: DDOS_PROTECT=1 but iptables setup failed (missing NET_ADMIN?) — continuing without protection" >&2
    fi
fi

# ─── Oxide kill switch ───────────────────────────────────────────────────────
# OXIDE=0 restores the vanilla RustDedicated binary from the Managed.vanilla/
# snapshot baked at image build time (see Dockerfile). No Steam round-trip.
# The game files live in the image layer, so this is safe to re-run every boot —
# Managed.vanilla/ is always present on container start.
#
# When disabled, we also remove oxide/ so no plugin scan happens, and skip the
# downstream Oxide-data PVC wiring + plugin-toggle blocks.
if [ "${OXIDE:-1}" = "0" ]; then
    if [ -d /steamcmd/rust/RustDedicated_Data/Managed.vanilla ]; then
        rm -rf /steamcmd/rust/RustDedicated_Data/Managed
        mv /steamcmd/rust/RustDedicated_Data/Managed.vanilla \
           /steamcmd/rust/RustDedicated_Data/Managed
        rm -rf /steamcmd/rust/oxide
        echo "[startup] OXIDE=0 — vanilla RustDedicated restored, Oxide removed"
    else
        echo "[startup] WARNING: OXIDE=0 but Managed.vanilla snapshot missing — server will run with Oxide still patched in" >&2
    fi
fi

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
if [ "${OXIDE:-1}" != "0" ]; then
    OXIDE_DATA_PVC="/steamcmd/rust/server/oxide-data"
    OXIDE_DATA_IMAGE="/steamcmd/rust/oxide/data"

    if [ ! -d "${OXIDE_DATA_PVC}" ]; then
        echo "[startup] First boot: seeding Oxide data directory on PVC..."
        cp -rp "${OXIDE_DATA_IMAGE}" "${OXIDE_DATA_PVC}"
    fi

    rm -rf "${OXIDE_DATA_IMAGE}"
    ln -sfT "${OXIDE_DATA_PVC}" "${OXIDE_DATA_IMAGE}"
    echo "[startup] Oxide data directory linked to PVC (${OXIDE_DATA_PVC})"
fi

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
    {
        printf '# Auto-config lock — delete this file to re-trigger resource detection on next start.\n'
        printf '# Generated: %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
        printf 'DETECTED_CPUS=%s\n' "${CPUS}"
        printf 'DETECTED_MEM_GB=%s\n' "${MEM_GB}"
        printf 'AUTO_WORLDSIZE=%s\n' "${AUTO_WORLDSIZE}"
        printf 'AUTO_MAXPLAYERS=%s\n' "${AUTO_MAXPLAYERS}"
        printf 'APPLIED_WORLDSIZE=%s\n' "${RUST_SERVER_WORLDSIZE}"
        printf 'APPLIED_MAXPLAYERS=%s\n' "${RUST_SERVER_MAXPLAYERS}"
    } > "${AUTOCONFIG_LOCK}"
    echo "[startup] Auto-config locked: ${AUTOCONFIG_LOCK}"
fi

# ─── PVP / PvE mode (Oxide only) ─────────────────────────────────────────────
# TruePVE stays loaded — it supports zones, rulesets, and per-entity exceptions
# that admins may want regardless of the global default. PVP_MODE only flips
# TruePVE's `defaultAllowDamage`:
#   PVP_MODE=1 (default) → defaultAllowDamage=true   (PvP globally; carve out
#                                                     PvE zones via TruePVE)
#   PVP_MODE=0           → defaultAllowDamage=false  (PvE globally; carve out
#                                                     PvP zones via TruePVE)
#
# oxide/config/ is seeded from the image layer on every boot (only oxide/data/
# is PVC-persisted), so the boot-time PVP_MODE toggle always wins. Admin-created
# zones/rulesets persist via TruePVE's own data files (oxide/data/, on PVC).
#
# When OXIDE=0 the operator has chosen a pure vanilla server with full PvP —
# we skip this block entirely. PVP_MODE is ignored in that case.
if [ "${OXIDE:-1}" != "0" ]; then
    if [ "${PVP_MODE:-1}" = "0" ]; then
        _TPVE_ALLOW_DAMAGE="false"
        _TPVE_LABEL="PvE globally (TruePVE zones can still allow PvP where configured)"
    else
        _TPVE_ALLOW_DAMAGE="true"
        _TPVE_LABEL="PvP globally (TruePVE zones can still carve out PvE where configured)"
    fi
    mkdir -p /steamcmd/rust/oxide/config
    cat > /steamcmd/rust/oxide/config/TruePVE.json <<EOF
{
  "Config Version": "2.0.0",
  "Default RuleSet": {
    "name": "default",
    "enabled": true,
    "defaultAllowDamage": ${_TPVE_ALLOW_DAMAGE},
    "_flags": "",
    "flags": "",
    "exceptions": [],
    "groups": []
  }
}
EOF
    echo "[startup] PVP_MODE=${PVP_MODE:-1} — TruePVE defaultAllowDamage=${_TPVE_ALLOW_DAMAGE} (${_TPVE_LABEL})"
fi

# ─── Plugin provisioning (RUST_PLUGINS + PLUGIN_SOURCE) ──────────────────────────────────────
PLUGIN_SOURCE="${PLUGIN_SOURCE:-github}"
RUST_PLUGINS="${RUST_PLUGINS:-}"
PER_PLUGIN_DIR=/etc/penguin-rust-plugins/per-plugin
OXIDE_PLUGINS_DIR=/steamcmd/rust/oxide/plugins
mkdir -p "${OXIDE_PLUGINS_DIR}"

case "${PLUGIN_SOURCE}" in
    github|umod) ;;
    *)
        echo "[startup] FATAL: PLUGIN_SOURCE=${PLUGIN_SOURCE} invalid — must be 'github' or 'umod'" >&2
        exit 1
        ;;
esac

# Build the slug list. If RUST_PLUGINS is empty, use every baked plugin.
if [ -n "${RUST_PLUGINS}" ]; then
    SLUGS=$(echo "${RUST_PLUGINS}" | tr ',' ' ' | tr -s ' ')
    echo "[startup] RUST_PLUGINS explicit list: ${SLUGS}"
else
    SLUGS=""
    for dir in "${PER_PLUGIN_DIR}"/*/; do
        [ -d "${dir}" ] || continue
        SLUGS="${SLUGS} $(basename "${dir}")"
    done
    echo "[startup] RUST_PLUGINS unset — using all baked plugins: ${SLUGS}"
fi

# fetch_from_umod, stage_from_baked, send_rcon — defined in lib-functions.sh

if [ "${OXIDE:-1}" != "0" ]; then
    BAKED_COUNT=0
    FALLBACK_COUNT=0
    FALLBACK_SLUGS=""
    for slug in ${SLUGS}; do
        [ -z "${slug}" ] && continue
        case "${PLUGIN_SOURCE}" in
            github)
                if [ -d "${PER_PLUGIN_DIR}/${slug}" ]; then
                    stage_from_baked "${slug}"
                    rc=$?
                    [ "${rc}" = "2" ] && exit 1
                    BAKED_COUNT=$((BAKED_COUNT + 1))
                else
                    echo "[startup] WARNING: ${slug} not in scanned repo — falling back to umod.org (BYPASSES SCAN PIPELINE)" >&2
                    if fetch_from_umod "${slug}"; then
                        FALLBACK_COUNT=$((FALLBACK_COUNT + 1))
                        FALLBACK_SLUGS="${FALLBACK_SLUGS} ${slug}"
                    fi
                fi
                ;;
            umod)
                echo "[startup] WARNING: PLUGIN_SOURCE=umod — fetching ${slug} from umod.org (BYPASSES SCAN PIPELINE)" >&2
                if fetch_from_umod "${slug}"; then
                    FALLBACK_COUNT=$((FALLBACK_COUNT + 1))
                    FALLBACK_SLUGS="${FALLBACK_SLUGS} ${slug}"
                fi
                ;;
        esac
    done

    # Summary line — greppable for ops dashboards and log aggregation.
    # FALLBACK_COUNT > 0 means at least one plugin bypassed the scan pipeline.
    FALLBACK_SLUGS="${FALLBACK_SLUGS# }"
    if [ "${FALLBACK_COUNT}" -gt 0 ]; then
        echo "[startup] Plugin provisioning complete: ${BAKED_COUNT} baked/verified, ${FALLBACK_COUNT} umod-fallback (UNSCANNED: ${FALLBACK_SLUGS})" >&2
    else
        echo "[startup] Plugin provisioning complete: ${BAKED_COUNT} baked/verified, 0 umod-fallback"
    fi
fi

# ─── Runtime plugin toggle ───────────────────────────────────────────────────
# Disable specific plugins at runtime without rebuilding the image.
# OXIDE_DISABLED_PLUGINS=Vanish,BGrade  (comma-separated, with or without .cs)
# Oxide only scans oxide/plugins/ root — subdirectories are ignored.
# Files restore from image on next restart, so toggle is purely env-var-controlled.
# Skipped when OXIDE=0 — no plugin directory exists.
if [ "${OXIDE:-1}" != "0" ] && [ -n "${OXIDE_DISABLED_PLUGINS:-}" ]; then
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
                echo "[startup] Plugin disabled: $(basename "${candidate}")"
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

# send_rcon — defined in lib-functions.sh

# ─── SIGTERM handler ─────────────────────────────────────────────────────────
# K8s sends SIGTERM before SIGKILL. Rust needs time to flush world state.
# terminationGracePeriodSeconds in values.yaml must be >= server.saveinterval.
_term() {
    echo "[startup] SIGTERM received — sending save + quit to server..."
    # Stop supercronic first so no scheduled tasks fire during shutdown
    kill -TERM "${SUPERCRONIC_PID:-}" 2>/dev/null || true
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
# Array form allows description/tags/URLs with spaces to be quoted correctly.
# Optional args are only appended when their env var is non-empty so the game
# picks its own defaults (rather than receiving an empty string argument).
SERVER_ARGS=(
    -batchmode
    +server.hostname  "${RUST_SERVER_NAME:-Rust Server}"
    +server.port      "${RUST_SERVER_PORT:-28015}"
    +server.maxplayers "${RUST_SERVER_MAXPLAYERS:-50}"
    +server.worldsize "${RUST_SERVER_WORLDSIZE:-3000}"
    +server.seed      "${RUST_SERVER_SEED:-12345}"
    +server.saveinterval "${RUST_SERVER_SAVE_INTERVAL:-300}"
    +server.identity  "${RUST_SERVER_IDENTITY:-rust_server}"
    +server.level     "${RUST_SERVER_LEVEL:-Procedural Map}"
    +rcon.port        "${RUST_RCON_PORT:-28016}"
    +rcon.password    "${RUST_RCON_PASSWORD}"
    +rcon.web         "${RUST_RCON_WEB:-1}"
    +oxide.enabled    1
)

# Optional server metadata — only included when non-empty
[ -n "${RUST_SERVER_DESCRIPTION:-}" ] && SERVER_ARGS+=(+server.description  "${RUST_SERVER_DESCRIPTION}")
[ -n "${RUST_SERVER_TAGS:-}" ]        && SERVER_ARGS+=(+server.tags          "${RUST_SERVER_TAGS}")
[ -n "${RUST_SERVER_URL:-}" ]         && SERVER_ARGS+=(+server.url           "${RUST_SERVER_URL}")
[ -n "${RUST_SERVER_HEADERIMAGE:-}" ] && SERVER_ARGS+=(+server.headerimage   "${RUST_SERVER_HEADERIMAGE}")
[ -n "${RUST_SERVER_LOGO:-}" ]        && SERVER_ARGS+=(+server.logo          "${RUST_SERVER_LOGO}")
[ -n "${RUST_SERVER_TICKRATE:-}" ]    && SERVER_ARGS+=(+server.tickrate      "${RUST_SERVER_TICKRATE}")
[ -n "${RUST_SERVER_FPS:-}" ]         && SERVER_ARGS+=(+fps                  "${RUST_SERVER_FPS}")
# Boolean toggles — only pass when explicitly set; omitting lets the game use its defaults
[ -n "${RUST_SERVER_PVE:-}" ]         && SERVER_ARGS+=(+server.pve           "${RUST_SERVER_PVE}")
[ -n "${RUST_SERVER_RADIATION:-}" ]   && SERVER_ARGS+=(+server.radiation     "${RUST_SERVER_RADIATION}")

# Operator passthrough — word-split intentionally (multiple args in one string)
# shellcheck disable=SC2206
[ -n "${RUST_SERVER_EXTRA_ARGS:-}" ] && SERVER_ARGS+=( ${RUST_SERVER_EXTRA_ARGS} )

/steamcmd/rust/RustDedicated "${SERVER_ARGS[@]}" &

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

# ─── Supercronic (scheduled tasks) ──────────────────────────────────────────
# Static crontab has: plugin update checker (*/5) + Facepunch forced-wipe
# (first Thursday, 17:00+18:00 UTC to cover BST/GMT).
# start.sh appends soft-wipe entries when WIPE_SCHED=1w|2w|3w.
cp /etc/penguin-rust/crontab /tmp/crontab

echo "[startup] Facepunch forced-wipe: first Thursday of month, 19:00 London (baked in crontab)"

case "${WIPE_SCHED:-}" in
    off)
        echo "[startup] Soft-wipe schedule disabled (WIPE_SCHED=off)"
        ;;
    1w|2w|3w)
        _W_DAY="${WIPE_DAY:-Th}"
        _W_TIME="${WIPE_TIME:-06:00}"

        # Use shared helpers for trigger time and DOW mapping
        _TRIGGER_CRON=$(compute_wipe_trigger "${_W_TIME}")
        _TRIGGER_M="${_TRIGGER_CRON%% *}"
        _TRIGGER_H="${_TRIGGER_CRON##* }"
        _CRON_DOW=$(day_to_cron_dow "${_W_DAY}")

        cat >> /tmp/crontab <<EOF

# Soft wipe: ${WIPE_SCHED} on ${_W_DAY} at ${_W_TIME} UTC
# Warning sequence starts at $(printf '%02d:%02d' "${_TRIGGER_H}" "${_TRIGGER_M}") UTC
${_TRIGGER_M} ${_TRIGGER_H} * * ${_CRON_DOW} /usr/local/bin/wipe-check.sh
EOF

        echo "[startup] Soft-wipe scheduled: every ${WIPE_SCHED} on ${_W_DAY} at ${_W_TIME} UTC (warnings at $(printf '%02d:%02d' "${_TRIGGER_H}" "${_TRIGGER_M}") UTC)"
        ;;
    "")
        echo "[startup] No soft-wipe schedule set (forced-wipe only)"
        ;;
    *)
        echo "[startup] WARNING: WIPE_SCHED=${WIPE_SCHED} not recognized — ignoring (valid: 1w, 2w, 3w, off)" >&2
        ;;
esac

# Launch supercronic in background — it manages all scheduled tasks
supercronic /tmp/crontab &
SUPERCRONIC_PID=$!
echo "[startup] supercronic started (PID ${SUPERCRONIC_PID})"

wait "$SERVER_PID"
