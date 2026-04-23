#!/bin/bash
# =============================================================================
# log-cleanup.sh — Compress and prune Rust server log files
#
# Runs daily at 03:00 UTC via supercronic.
#
# Policy:
#   > 24 hours old, uncompressed (.log)  → gzip in place → .log.gz
#   > 7 days old (compressed or raw)     → delete
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

compressed=0
deleted=0

for dir in "${LOG_DIRS[@]}"; do
    [ -d "${dir}" ] || continue

    # Compress uncompressed logs older than 24 hours
    while IFS= read -r -d '' f; do
        if gzip "${f}" 2>/dev/null; then
            echo "${LOG_PREFIX} compressed: ${f}"
            (( compressed++ )) || true
        else
            echo "${LOG_PREFIX} WARNING: failed to compress ${f}" >&2
        fi
    done < <(find "${dir}" -maxdepth 2 -name "*.log" ! -name "*.log.gz" -mtime +0 -print0)

    # Delete any log files (compressed or not) older than 7 days
    while IFS= read -r -d '' f; do
        rm -f "${f}"
        echo "${LOG_PREFIX} deleted: ${f}"
        (( deleted++ )) || true
    done < <(find "${dir}" -maxdepth 2 \( -name "*.log" -o -name "*.log.gz" \) -mtime +6 -print0)
done

if (( compressed > 0 || deleted > 0 )); then
    echo "${LOG_PREFIX} done — compressed: ${compressed}, deleted: ${deleted}"
else
    echo "${LOG_PREFIX} nothing to do"
fi
