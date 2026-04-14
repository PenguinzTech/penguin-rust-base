#!/bin/bash
# =============================================================================
# Wipe executor — invoked by supercronic at the computed wipe trigger time.
#
# supercronic fires this at WIPE_TIME minus 60 minutes on the configured day.
# This script only needs to verify:
#   1. Stamp file (don't re-wipe same day after a fast container restart)
#   2. First-Thursday guard (when WIPE_SCHED is unset, only dom <= 7)
#   3. 2w/3w interval modulo (skip non-matching weeks)
# Then runs the 60-minute RCON warning countdown, wipes, and stops the server.
#
# Env vars (inherited from container):
#   WIPE_SCHED, WIPE_DAY, WIPE_TIME, WIPE_BP
#   RUST_SERVER_IDENTITY, RUST_RCON_PASSWORD, RUST_RCON_PORT
# =============================================================================
set -uo pipefail

# shellcheck source=lib-functions.sh
. /usr/local/lib/penguin-rust/lib-functions.sh

# ─── Guards ─────────────────────────────────────────────────────────────────
if ! pgrep -f RustDedicated >/dev/null 2>&1; then
    exit 0
fi

_W_IDENT="${RUST_SERVER_IDENTITY:-rust_server}"
_W_DIR="/steamcmd/rust/server/${_W_IDENT}"
_W_STAMP="${_W_DIR}/.last-wipe"

today=$(date -u +%Y-%m-%d)
dow=$(date -u +%u)
dom=$(date -u +%-d)

# Never wipe twice on the same calendar day
if [ -f "${_W_STAMP}" ] && [ "$(cat "${_W_STAMP}")" = "${today}" ]; then
    exit 0
fi

# First-Thursday-only mode: skip if not first week of month
if [ -z "${WIPE_SCHED:-}" ]; then
    if [ "${dom}" -gt 7 ]; then
        echo "[wipe] Not first week of month (dom=${dom}) — skipping"
        exit 0
    fi
fi

# 2w/3w interval check
case "${WIPE_SCHED:-}" in
    2w|3w)
        case "${WIPE_SCHED}" in
            2w) interval=2 ;;
            3w) interval=3 ;;
        esac
        epoch_weeks=$(( $(date -u +%s) / 604800 ))
        if [ $(( epoch_weeks % interval )) -ne 0 ]; then
            echo "[wipe] Not a ${WIPE_SCHED} week (epoch_week=${epoch_weeks}) — skipping"
            exit 0
        fi
        ;;
esac

load_rcon_password

# ─── Wipe sequence (60-minute warning countdown) ────────────────────────────
bp="${WIPE_BP:-false}"
# First-Thursday of month: always wipe blueprints (Facepunch forced wipe)
if [ "${dow}" -eq 4 ] && [ "${dom}" -le 7 ]; then
    bp=true
fi

echo "[wipe] Wipe triggered — blueprint_wipe=${bp} (warnings start now, wipe in 60 min)"

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

# SIGTERM the main process (PID 1 = start.sh) which propagates to RustDedicated
kill -TERM 1 2>/dev/null || true
