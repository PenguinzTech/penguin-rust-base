#!/bin/bash
# =============================================================================
# perf-sample.sh — Sample CPU/RAM/disk and append to perf.csv
#
# Runs every 5 minutes via supercronic. The game server is never involved —
# all reads are from /proc and df. The Oxide plugin reads the CSV on demand.
#
# CSV format (no header row):
#   timestamp_unix, cpu_pct, mem_used_kb, mem_total_kb,
#   disk_used_kb, disk_total_kb, disk_pct
#
# Output path:
#   /steamcmd/rust/server/<RUST_SERVER_IDENTITY>/perf.csv
#
# Env vars:
#   RUST_SERVER_IDENTITY  — server identity dir (default: rust_server)
#   SERVER_PERF_LOG_HOURS — how many hours of entries to retain (default: 24)
# =============================================================================
set -uo pipefail

IDENTITY="${RUST_SERVER_IDENTITY:-rust_server}"
KEEP_HOURS="${SERVER_PERF_LOG_HOURS:-24}"
CSV_DIR="/steamcmd/rust/server/${IDENTITY}"
CSV_PATH="${CSV_DIR}/perf.csv"

mkdir -p "${CSV_DIR}"

# ─── CPU: 1-second delta sample from /proc/stat ──────────────────────────────
get_cpu_stats() {
    local _ user nice system idle iowait irq softirq steal
    read -r _ user nice system idle iowait irq softirq steal _ < /proc/stat
    echo "${idle} $(( user + nice + system + idle + iowait + irq + softirq + steal ))"
}

s1=$(get_cpu_stats)
sleep 1
s2=$(get_cpu_stats)

read -r idle1 total1 <<< "${s1}"
read -r idle2 total2 <<< "${s2}"
d_idle=$(( idle2 - idle1 ))
d_total=$(( total2 - total1 ))
cpu_pct=$(( d_total > 0 ? (d_total - d_idle) * 100 / d_total : 0 ))

# ─── Memory from /proc/meminfo (KB) ─────────────────────────────────────────
mem_total_kb=$(awk '/^MemTotal:/{print $2}'     /proc/meminfo)
mem_avail_kb=$(awk '/^MemAvailable:/{print $2}' /proc/meminfo)
mem_used_kb=$(( mem_total_kb - mem_avail_kb ))

# ─── Disk usage for server data dir (KB) ────────────────────────────────────
read -r disk_total_kb disk_used_kb disk_pct <<< \
    "$(df -k /steamcmd/rust | awk 'NR==2 {gsub(/%/,"",$5); print $2, $3, $5}')"

# ─── Append sample ───────────────────────────────────────────────────────────
ts=$(date +%s)
echo "${ts},${cpu_pct},${mem_used_kb},${mem_total_kb},${disk_used_kb},${disk_total_kb},${disk_pct}" \
    >> "${CSV_PATH}"

# ─── Prune entries older than KEEP_HOURS ─────────────────────────────────────
cutoff=$(( ts - KEEP_HOURS * 3600 ))
tmp=$(mktemp)
awk -F, -v c="${cutoff}" '$1 > c' "${CSV_PATH}" > "${tmp}" && mv "${tmp}" "${CSV_PATH}" || rm -f "${tmp}"
