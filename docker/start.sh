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

# ─── Plugin source selection (github=scanned, umod=bypass) ──────────────────
# PLUGIN_SOURCE=github (default) uses the plugins baked into the image from
# penguin-rust-plugins (ClamAV/YARA/Semgrep/gitleaks/trivy clean). Hashes are
# verified at startup against /etc/penguin-rust-plugins/per-plugin/${slug}/${slug}.hash.
# PLUGIN_SOURCE=umod pulls fresh copies directly from umod.org over the baked
# ones — bypasses the scan gate, warns loudly. Opt-in for operators who need
# same-day upstream fixes and accept the trust tradeoff.
if [ "${OXIDE:-1}" != "0" ]; then
    PLUGIN_SOURCE="${PLUGIN_SOURCE:-github}"
    case "${PLUGIN_SOURCE}" in
        github)
            echo "[startup] PLUGIN_SOURCE=github — using scanned plugins baked into image"
            if [ "${VERIFY_PLUGIN_HASHES:-1}" = "1" ] && [ -d /etc/penguin-rust-plugins/per-plugin ]; then
                echo "[startup] Verifying plugin hashes against committed manifest..."
                (
                    cd /etc/penguin-rust-plugins/per-plugin
                    for dir in */; do
                        slug="${dir%/}"
                        if ! (cd "${slug}" && sha256sum -c "${slug}.hash" >/dev/null 2>&1); then
                            echo "[startup] FATAL: ${slug} hash mismatch at runtime — refusing to start" >&2
                            exit 1
                        fi
                    done
                ) || exit 1
                echo "[startup] All plugin hashes verified."
            fi
            ;;
        umod)
            echo "[startup] WARNING: PLUGIN_SOURCE=umod — bypassing scan pipeline, pulling directly from umod.org" >&2
            echo "[startup] WARNING: plugins fetched this way have NOT been scanned by ClamAV/YARA/Semgrep/gitleaks/trivy" >&2
            if [ -f /etc/penguin-rust-plugins/umod-plugins.txt ]; then
                while IFS='|' read -r slug filename; do
                    case "${slug}" in '#'*|'') continue ;; esac
                    if curl -fsSL "https://umod.org/plugins/${slug}.cs" \
                        -o "/steamcmd/rust/oxide/plugins/${filename}"; then
                        echo "[startup] fetched umod:${slug} → ${filename}"
                    else
                        echo "[startup] WARNING: failed to fetch umod:${slug} — keeping baked copy" >&2
                    fi
                done < /etc/penguin-rust-plugins/umod-plugins.txt
            else
                echo "[startup] WARNING: /etc/penguin-rust-plugins/umod-plugins.txt missing — no plugins refreshed" >&2
            fi
            ;;
        *)
            echo "[startup] FATAL: PLUGIN_SOURCE=${PLUGIN_SOURCE} invalid — must be 'github' or 'umod'" >&2
            exit 1
            ;;
    esac
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

# ─── Wipe schedule watcher ───────────────────────────────────────────────────
# Polls on a 30-second tick; deletes server save data and sends SIGTERM when
# the configured schedule fires. Container restart policy then starts fresh.
#
# WIPE_SCHED (unset)  — first Thursday of month only (Facepunch forced-wipe day)
# WIPE_SCHED=1w|2w|3w — every N weeks on WIPE_DAY
# WIPE_SCHED=off      — disable all automated wipes
# WIPE_DAY=Th         — day: M Tu W Th F Sa Su (default: Th)
# WIPE_TIME=06:00     — UTC time to trigger (default: 06:00)
# WIPE_BP=false       — also delete player blueprints (default: false)
#                       Note: first-Thursday wipes always delete blueprints.
#
# Wipe = delete proceduralmap.*.{map,sav,db} + optionally player.blueprints.*.db,
# write a stamp file so a same-day restart doesn't re-wipe, then stop the server.
if [ "${WIPE_SCHED:-}" != "off" ]; then
    _W_DAY="${WIPE_DAY:-Th}"
    _W_TIME="${WIPE_TIME:-06:00}"
    _W_BP="${WIPE_BP:-false}"
    _W_IDENT="${RUST_SERVER_IDENTITY:-rust_server}"
    _W_DIR="/steamcmd/rust/server/${_W_IDENT}"
    _W_STAMP="${_W_DIR}/.last-wipe"

    # Warning sequence starts 60 minutes before WIPE_TIME (actual wipe).
    # _W_TRIGGER is the HH:MM at which _wipe_run fires — 60 min before _W_TIME.
    # Handles day-rollover (e.g. WIPE_TIME=00:30 → trigger 23:30 previous day);
    # in that rare case we accept that the trigger-day check uses the prior day.
    _W_TRIGGER=$(date -u -d "1970-01-01 ${_W_TIME} UTC - 60 minutes" +%H:%M 2>/dev/null || echo "05:00")

    case "${_W_DAY}" in
        M|Mo|Mon) _W_DOW=1 ;;
        Tu|Tue)   _W_DOW=2 ;;
        W|We|Wed) _W_DOW=3 ;;
        Th|Thu)   _W_DOW=4 ;;
        F|Fr|Fri) _W_DOW=5 ;;
        Sa|Sat)   _W_DOW=6 ;;
        Su|Sun)   _W_DOW=7 ;;
        *)        _W_DOW=4 ;;
    esac

    (
        _wipe_due() {
            local today dow dom epoch_weeks interval
            today=$(date -u +%Y-%m-%d)
            # Never wipe twice on the same calendar day
            if [ -f "${_W_STAMP}" ] && [ "$(cat "${_W_STAMP}")" = "${today}" ]; then
                return 1
            fi
            # Check time window — fire 60 min before WIPE_TIME so the warning
            # sequence ends exactly at WIPE_TIME.
            if [ "$(date -u +%H:%M)" != "${_W_TRIGGER}" ]; then
                return 1
            fi
            dow=$(date -u +%u)
            dom=$(date -u +%-d)
            if [ -z "${WIPE_SCHED:-}" ]; then
                # Default: align with Facepunch forced-wipe (first Thursday of month)
                if [ "${dow}" -eq 4 ] && [ "${dom}" -le 7 ]; then
                    return 0
                fi
                return 1
            fi
            # Custom schedule: check day-of-week
            if [ "${dow}" -ne "${_W_DOW}" ]; then
                return 1
            fi
            # Check interval for 2w/3w
            case "${WIPE_SCHED:-}" in
                2w) interval=2 ;;
                3w) interval=3 ;;
                *)  return 0 ;;
            esac
            epoch_weeks=$(( $(date -u +%s) / 604800 ))
            if [ $(( epoch_weeks % interval )) -eq 0 ]; then
                return 0
            fi
            return 1
        }

        _wipe_run() {
            local today dom dow bp
            today=$(date -u +%Y-%m-%d)
            dom=$(date -u +%-d)
            dow=$(date -u +%u)
            bp="${_W_BP}"
            # First-Thursday of month: always wipe blueprints (Facepunch forced wipe)
            if [ "${dow}" -eq 4 ] && [ "${dom}" -le 7 ]; then
                bp=true
            fi
            echo "[wipe] Wipe triggered — blueprint_wipe=${bp} (warnings start now, wipe in 60 min)"
            # Hourly lead: warnings at T-60, T-50, T-40, T-30, T-20, T-10, T-5, T-1 min.
            # Sleeps between broadcasts total 60 min; send_rcon failures are logged but
            # never abort the sequence — the wipe MUST happen even if RCON is down.
            send_rcon "say [SERVER] Scheduled server wipe in 60 minutes — plan accordingly!"
            sleep 600   # → T-50
            send_rcon "say [SERVER] Server wipe in 50 minutes."
            sleep 600   # → T-40
            send_rcon "say [SERVER] Server wipe in 40 minutes."
            sleep 600   # → T-30
            send_rcon "say [SERVER] Server wipe in 30 minutes."
            sleep 600   # → T-20
            send_rcon "say [SERVER] Server wipe in 20 minutes."
            sleep 600   # → T-10
            send_rcon "say [SERVER] Server wipe in 10 minutes — wrap up your runs!"
            sleep 300   # → T-5
            send_rcon "say [SERVER] Server wipe in 5 minutes!"
            sleep 240   # → T-1
            send_rcon "say [SERVER] Server wipe in 60 seconds!"
            sleep 55    # → T-5s
            send_rcon "say [SERVER] Wiping now — see you on the new map!"
            sleep 5
            send_rcon "server.save"
            sleep 3
            # Delete map and save data
            rm -f "${_W_DIR}"/proceduralmap.*.map \
                  "${_W_DIR}"/proceduralmap.*.sav \
                  "${_W_DIR}"/proceduralmap.*.db 2>/dev/null || true
            if [ "${bp}" = "true" ]; then
                echo "[wipe] Wiping blueprints..."
                rm -f "${_W_DIR}"/player.blueprints.*.db 2>/dev/null || true
            fi
            # Stamp before SIGTERM so a fast container restart does not re-wipe
            mkdir -p "${_W_DIR}"
            printf '%s\n' "${today}" > "${_W_STAMP}"
            echo "[wipe] Save data deleted — stopping server for clean restart"
            kill -TERM "${SERVER_PID}" 2>/dev/null || true
        }

        while kill -0 "${SERVER_PID}" 2>/dev/null; do
            sleep 30
            if _wipe_due; then
                _wipe_run
                break
            fi
        done
    ) &
    echo "[startup] Wipe watcher active (sched=${WIPE_SCHED:-forced-only}, day=${_W_DAY}, wipe=${_W_TIME} UTC, warnings start ${_W_TRIGGER} UTC)"
fi

wait "$SERVER_PID"
