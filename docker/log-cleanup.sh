#!/bin/bash
# =============================================================================
# log-cleanup.sh — Compress and prune Rust server log files
#
# Runs daily at 03:00 UTC via supercronic.
#
# Policy (keyed off file creation/birth time, not modification time — Rust
# appends to the same log file continuously so mtime is always "now"):
#   Created > 24 hours ago, uncompressed (.log)  → gzip in place → .log.gz
#   Created > 7 days ago (compressed or raw)     → delete
#
# Age is determined via stat -c %W (birth time). Falls back to stat -c %Z
# (inode change time) if the filesystem doesn't report birth time (returns 0).
#
# Scopes:
#   /steamcmd/rust/oxide/logs/           — Oxide plugin logs
#   /steamcmd/rust/server/<identity>/    — server identity logs
#   /steamcmd/rust/*.log                 — RustDedicated root logs
# =============================================================================
set -uo pipefail

LOG_PREFIX="[log-cleanup]"

IDENTITY="${RUST_SERVER_IDENTITY:-rust_server}"

LOG_DIRS=(
    "/steamcmd/rust/oxide/logs"
    "/steamcmd/rust/server/${IDENTITY}"
    "/steamcmd/rust"
)

THRESHOLD_COMPRESS=$(( $(date +%s) - 86400   ))   # 24 hours
THRESHOLD_DELETE=$(( $(date +%s)   - 604800  ))   # 7 days

compressed=0
deleted=0

# Return the creation/birth time of a file in seconds since epoch.
# Uses stat birth time (%W); falls back to inode-change time (%Z) if
# the filesystem doesn't support birth time (returns 0).
file_birth() {
    local f="$1"
    local birth
    birth=$(stat -c %W "${f}" 2>/dev/null || echo 0)
    if [ "${birth}" = "0" ] || [ -z "${birth}" ]; then
        birth=$(stat -c %Z "${f}" 2>/dev/null || echo 0)
    fi
    echo "${birth}"
}

for dir in "${LOG_DIRS[@]}"; do
    [ -d "${dir}" ] || continue

    # Enumerate all log files (compressed and uncompressed) in one pass
    while IFS= read -r -d '' f; do
        birth=$(file_birth "${f}")
        [ "${birth}" -eq 0 ] && continue  # can't determine age, skip

        if [ "${birth}" -le "${THRESHOLD_DELETE}" ]; then
            rm -f "${f}"
            echo "${LOG_PREFIX} deleted: ${f}"
            (( deleted++ )) || true
        elif [[ "${f}" != *.gz ]] && [ "${birth}" -le "${THRESHOLD_COMPRESS}" ]; then
            if gzip "${f}" 2>/dev/null; then
                echo "${LOG_PREFIX} compressed: ${f}"
                (( compressed++ )) || true
            else
                echo "${LOG_PREFIX} WARNING: failed to compress ${f}" >&2
            fi
        fi
    done < <(find "${dir}" -maxdepth 2 \( -name "*.log" -o -name "*.log.gz" \) -print0)
done

if (( compressed > 0 || deleted > 0 )); then
    echo "${LOG_PREFIX} done — compressed: ${compressed}, deleted: ${deleted}"
else
    echo "${LOG_PREFIX} nothing to do"
fi
