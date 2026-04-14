#!/bin/bash
# =============================================================================
# Shared functions for penguin-rust scripts.
# Sourced by start.sh, check-plugin-updates.sh, wipe-check.sh, and tests.
# =============================================================================

# ─── RCON helper ─────────────────────────────────────────────────────────────
# Sends a single command to the server via WebSocket RCON (rcon.web 1).
# Requires websocat and RUST_RCON_PASSWORD. Silently no-ops if either missing.
send_rcon() {
    local cmd="$1"
    local caller="${2:-lib}"
    [ -z "${RUST_RCON_PASSWORD:-}" ] && return 0
    command -v websocat >/dev/null 2>&1 || return 0
    printf '{"Identifier":1,"Message":"%s","Name":"%s"}\n' "${cmd}" "${caller}" | \
        websocat -n1 --no-close --timeout 5 \
        "ws://127.0.0.1:${RUST_RCON_PORT:-28016}/${RUST_RCON_PASSWORD}" \
        >/dev/null 2>&1 || true
}

# ─── Load RCON password ─────────────────────────────────────────────────────
# Reads the auto-generated RCON password from the PVC if not already in env.
load_rcon_password() {
    if [ -z "${RUST_RCON_PASSWORD:-}" ]; then
        local pw_file="/steamcmd/rust/server/${RUST_SERVER_IDENTITY:-rust_server}/.rcon.pw"
        if [ -f "${pw_file}" ]; then
            RUST_RCON_PASSWORD=$(cat "${pw_file}")
            export RUST_RCON_PASSWORD
        fi
    fi
}

# ─── Fetch plugin from umod.org ─────────────────────────────────────────────
# Downloads a plugin by slug. Tries .cs first (90%+), falls back to .zip.
# Returns 0 on success, 1 on failure.
fetch_from_umod() {
    local slug="$1"
    local filename
    filename=$(curl -fsSL "https://umod.org/plugins/${slug}.json" \
                | jq -r '.title // empty' \
                | tr -d ' ' \
                || true)
    [ -z "${filename}" ] && filename="${slug}"

    # Try .cs first (90%+ of umod plugins)
    local target="${OXIDE_PLUGINS_DIR}/${filename}.cs"
    if curl -fsSL "https://umod.org/plugins/${slug}.cs" -o "${target}" 2>/dev/null; then
        echo "[startup] fetched umod:${slug} -> ${filename}.cs"
        return 0
    fi

    # Fallback: some plugins are distributed as .zip (extract .cs from inside)
    local zip_tmp
    zip_tmp="$(mktemp -d)"
    if curl -fsSL "https://umod.org/plugins/${slug}.zip" -o "${zip_tmp}/${slug}.zip" 2>/dev/null; then
        if unzip -qo "${zip_tmp}/${slug}.zip" -d "${zip_tmp}/extracted" 2>/dev/null; then
            local cs_count
            cs_count=$(find "${zip_tmp}/extracted" -name '*.cs' | wc -l)
            if [ "${cs_count}" -gt 0 ]; then
                find "${zip_tmp}/extracted" -name '*.cs' \
                    -exec cp --preserve=mode {} "${OXIDE_PLUGINS_DIR}/" \;
                echo "[startup] fetched umod:${slug} -> extracted ${cs_count} .cs from .zip"
                rm -rf "${zip_tmp}"
                return 0
            fi
        fi
    fi
    rm -rf "${zip_tmp}"

    echo "[startup] WARNING: failed to fetch umod:${slug} (.cs and .zip both failed)" >&2
    return 1
}

# ─── Stage plugin from baked image ───────────────────────────────────────────
# Verifies hash, copies .cs to oxide/plugins/. Returns:
#   0 = success, 1 = not found (benign), 2 = hash mismatch (fatal)
stage_from_baked() {
    local slug="$1"
    local src_dir="${PER_PLUGIN_DIR}/${slug}"
    [ -d "${src_dir}" ] || return 1
    if ! (cd "${src_dir}" && sha256sum -c "${slug}.hash" >/dev/null 2>&1); then
        echo "[startup] FATAL: ${slug} baked hash mismatch — refusing to stage" >&2
        return 2
    fi
    find "${src_dir}" -maxdepth 1 -name '*.cs' \
        -exec cp --preserve=mode {} "${OXIDE_PLUGINS_DIR}/" \;
    echo "[startup] staged baked:${slug} (hash verified)"
    return 0
}

# ─── Compute wipe trigger time ───────────────────────────────────────────────
# Given WIPE_TIME (HH:MM), returns the trigger time 60 minutes earlier.
# Output: "MM HH" (cron-format minute and hour)
compute_wipe_trigger() {
    local wipe_time="$1"
    local hour="${wipe_time%%:*}"
    local min="${wipe_time##*:}"
    local total=$(( (10#${hour} * 60 + 10#${min}) - 60 ))
    if [ "${total}" -lt 0 ]; then
        total=$(( total + 1440 ))
    fi
    local trigger_h=$(( total / 60 ))
    local trigger_m=$(( total % 60 ))
    printf '%d %d\n' "${trigger_m}" "${trigger_h}"
}

# ─── Map day abbreviation to cron DOW ────────────────────────────────────────
# Returns 0-6 (0=Sun) for cron compatibility.
day_to_cron_dow() {
    local day="$1"
    case "${day}" in
        M|Mo|Mon) echo 1 ;;
        Tu|Tue)   echo 2 ;;
        W|We|Wed) echo 3 ;;
        Th|Thu)   echo 4 ;;
        F|Fr|Fri) echo 5 ;;
        Sa|Sat)   echo 6 ;;
        Su|Sun)   echo 0 ;;
        *)        echo 4 ;;
    esac
}
