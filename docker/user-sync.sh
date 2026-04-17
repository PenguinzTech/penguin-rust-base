#!/bin/bash
# =============================================================================
# user-sync.sh — Sync admins/moderators/bans from MySQL to Rust server via RCON
#
# Runs every 5 minutes via supercronic. No-op if USER_SYNC_DB_* env vars not set.
#
# DB schema (rust_users table):
#   steamid  VARCHAR(17)
#   username VARCHAR(128)
#   role     ENUM('admin','moderator','ban')
#   reason   VARCHAR(255)  -- ban reason
#   PRIMARY KEY (steamid, role)
#
# Env vars:
#   USER_SYNC_DB_HOST  — MySQL host (required to activate sync)
#   USER_SYNC_DB_PORT  — MySQL port (default 3306)
#   USER_SYNC_DB_USER  — MySQL user (required)
#   USER_SYNC_DB_PASS  — MySQL password (required)
#   USER_SYNC_DB       — MySQL database name (required)
# =============================================================================
set -uo pipefail

# shellcheck source=lib-functions.sh
. /usr/local/lib/penguin-rust/lib-functions.sh

LOG_PREFIX="[usersync]"

# ─── Guard 1: required env vars ─────────────────────────────────────────────
for var in USER_SYNC_DB_HOST USER_SYNC_DB_USER USER_SYNC_DB_PASS USER_SYNC_DB; do
    if [ -z "${!var:-}" ]; then
        exit 0
    fi
done

USER_SYNC_DB_PORT="${USER_SYNC_DB_PORT:-3306}"
IDENTITY="${RUST_SERVER_IDENTITY:-rust_server}"
IDENTITY_DIR="/steamcmd/rust/server/${IDENTITY}/cfg"
STATE_FILE="${IDENTITY_DIR}/.usersync-state.json"

# ─── Guard 2: server must be running ────────────────────────────────────────
if ! pgrep -f RustDedicated >/dev/null 2>&1; then
    echo "${LOG_PREFIX} Server not running yet — skipping"
    exit 0
fi

# Load RCON password
load_rcon_password

# ─── Query DB ────────────────────────────────────────────────────────────────
# Exit 0 on failure — don't remove entries if DB is unreachable
DB_OUT=$(mysql \
    -h "${USER_SYNC_DB_HOST}" \
    -P "${USER_SYNC_DB_PORT}" \
    -u "${USER_SYNC_DB_USER}" \
    -p"${USER_SYNC_DB_PASS}" \
    "${USER_SYNC_DB}" \
    --batch --skip-column-names --connect-timeout=10 \
    -e "SELECT steamid, username, role, reason FROM rust_users ORDER BY role, steamid;" \
    2>/dev/null) || {
    echo "${LOG_PREFIX} ERROR: Failed to query database — skipping sync (state unchanged)"
    exit 0
}

# ─── Parse DB output ─────────────────────────────────────────────────────────
# key=steamid, value=username (admins/mods) or "username|reason" (bans)
declare -A db_admins db_mods db_bans

while IFS=$'\t' read -r steamid username role reason; do
    [ -z "$steamid" ] && continue
    case "$role" in
        admin)     db_admins["$steamid"]="$username" ;;
        moderator) db_mods["$steamid"]="$username" ;;
        ban)       db_bans["$steamid"]="${username}|${reason}" ;;
    esac
done <<< "$DB_OUT"

# ─── Load previous state from JSON file ──────────────────────────────────────
declare -A prev_admins prev_mods prev_bans

if [ -f "$STATE_FILE" ]; then
    while IFS='=' read -r steamid username; do
        prev_admins["$steamid"]="$username"
    done < <(jq -r '.admins // {} | to_entries[] | .key + "=" + .value' "$STATE_FILE" 2>/dev/null || true)

    while IFS='=' read -r steamid username; do
        prev_mods["$steamid"]="$username"
    done < <(jq -r '.moderators // {} | to_entries[] | .key + "=" + .value' "$STATE_FILE" 2>/dev/null || true)

    while IFS='=' read -r steamid val; do
        prev_bans["$steamid"]="$val"
    done < <(jq -r '.bans // {} | to_entries[] | .key + "=" + .value' "$STATE_FILE" 2>/dev/null || true)
fi

changes=0

# Helper: issue RCON and count change
rcon_change() {
    send_rcon "$1" "usersync"
    changes=$((changes + 1))
}

# ── Admins ──────────────────────────────────────────────────────────────────
for steamid in "${!db_admins[@]}"; do
    if [ -z "${prev_admins[$steamid]+x}" ]; then
        username="${db_admins[$steamid]}"
        echo "${LOG_PREFIX} +admin ${steamid} (${username})"
        rcon_change "ownerid ${steamid} \"${username}\""
    fi
done
for steamid in "${!prev_admins[@]}"; do
    if [ -z "${db_admins[$steamid]+x}" ]; then
        echo "${LOG_PREFIX} -admin ${steamid}"
        rcon_change "removeowner ${steamid}"
    fi
done

# ── Moderators ──────────────────────────────────────────────────────────────
for steamid in "${!db_mods[@]}"; do
    if [ -z "${prev_mods[$steamid]+x}" ]; then
        username="${db_mods[$steamid]}"
        echo "${LOG_PREFIX} +mod ${steamid} (${username})"
        rcon_change "moderatorid ${steamid} \"${username}\""
    fi
done
for steamid in "${!prev_mods[@]}"; do
    if [ -z "${db_mods[$steamid]+x}" ]; then
        echo "${LOG_PREFIX} -mod ${steamid}"
        rcon_change "removemoderator ${steamid}"
    fi
done

# ── Bans ────────────────────────────────────────────────────────────────────
for steamid in "${!db_bans[@]}"; do
    if [ -z "${prev_bans[$steamid]+x}" ]; then
        IFS='|' read -r username reason <<< "${db_bans[$steamid]}"
        echo "${LOG_PREFIX} +ban ${steamid} (${username})"
        rcon_change "banid ${steamid} \"${username}\" \"${reason}\""
    fi
done
for steamid in "${!prev_bans[@]}"; do
    if [ -z "${db_bans[$steamid]+x}" ]; then
        echo "${LOG_PREFIX} -ban ${steamid}"
        rcon_change "unban ${steamid}"
    fi
done

# ── Flush if anything changed ────────────────────────────────────────────────
if [ "$changes" -gt 0 ]; then
    send_rcon "server.writecfg" "usersync"
    echo "${LOG_PREFIX} Flushed ${changes} change(s) via server.writecfg"
else
    echo "${LOG_PREFIX} No changes"
fi

# ── Write new state file ─────────────────────────────────────────────────────
mkdir -p "$IDENTITY_DIR"

# Build JSON objects for state file
_admins_json="{}"
for steamid in "${!db_admins[@]}"; do
    _admins_json=$(echo "$_admins_json" | jq --arg k "$steamid" --arg v "${db_admins[$steamid]}" '. + {($k): $v}')
done

_mods_json="{}"
for steamid in "${!db_mods[@]}"; do
    _mods_json=$(echo "$_mods_json" | jq --arg k "$steamid" --arg v "${db_mods[$steamid]}" '. + {($k): $v}')
done

_bans_json="{}"
for steamid in "${!db_bans[@]}"; do
    _bans_json=$(echo "$_bans_json" | jq --arg k "$steamid" --arg v "${db_bans[$steamid]}" '. + {($k): $v}')
done

jq -n \
    --argjson admins "$_admins_json" \
    --argjson moderators "$_mods_json" \
    --argjson bans "$_bans_json" \
    '{admins: $admins, moderators: $moderators, bans: $bans}' > "$STATE_FILE"
