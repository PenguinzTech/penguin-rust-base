#!/bin/bash
# =============================================================================
# Plugin live-update checker — run by supercronic every 5 minutes.
#
# Compares baked plugin hashes against the latest releases in penguin-rust-plugins.
# If a newer scan-clean version exists, downloads it, hash-verifies, copies to
# oxide/plugins/, and hot-reloads via RCON. Players don't notice — Oxide handles
# live .cs reloads without server restart.
#
# Uses curl + jq (no gh CLI needed). Public repo, no auth required.
#
# Only runs when PLUGIN_SOURCE=github (the default). Skipped under umod mode.
#
# Env vars (inherited from container):
#   PLUGIN_SOURCE          — github (default) or umod; skip if umod
#   PLUGIN_UPDATE_ENABLED  — set to 0 to disable (default: 1)
#   RUST_RCON_PASSWORD     — needed for oxide.reload; skip reload if unset
#   RUST_RCON_PORT         — default 28016
#   RUST_SERVER_IDENTITY   — for .rcon.pw fallback
# =============================================================================
set -uo pipefail

# shellcheck source=lib-functions.sh
. /usr/local/lib/penguin-rust/lib-functions.sh

# ─── Guards ─────────────────────────────────────────────────────────────────
if [ "${PLUGIN_UPDATE_ENABLED:-1}" = "0" ]; then
    exit 0
fi

if [ "${PLUGIN_SOURCE:-github}" != "github" ]; then
    exit 0
fi

# Only check if the server is actually running
if ! pgrep -f RustDedicated >/dev/null 2>&1; then
    exit 0
fi

PLUGINS_REPO="PenguinzTech/penguin-rust-plugins"
GITHUB_API="https://api.github.com/repos/${PLUGINS_REPO}"
PER_PLUGIN_DIR="/etc/penguin-rust-plugins/per-plugin"
OXIDE_PLUGINS_DIR="/steamcmd/rust/oxide/plugins"
UPDATED=0
FAILED=0

load_rcon_password

# Fetch all release tags once (public repo, no auth needed)
ALL_TAGS=$(curl -sf "${GITHUB_API}/releases?per_page=100" \
    | jq -r '.[].tag_name' 2>/dev/null || true)

if [ -z "${ALL_TAGS}" ]; then
    # API rate limit or network issue — silently skip this cycle
    exit 0
fi

# ─── Check each baked plugin ────────────────────────────────────────────────
for dir in "${PER_PLUGIN_DIR}"/*/; do
    [ -d "${dir}" ] || continue
    slug="$(basename "${dir}")"
    hash_file="${dir}${slug}.hash"
    [ -f "${hash_file}" ] || continue

    # Current baked hash
    current_sha=$(awk '{print $1}' "${hash_file}")

    # Find latest release tag for this slug (highest epoch suffix)
    latest_tag=$(echo "${ALL_TAGS}" \
        | awk -v s="${slug}-" 'index($0,s)==1' \
        | awk -F- '{print $NF"\t"$0}' | sort -n | tail -1 | cut -f2 || true)

    [ -z "${latest_tag}" ] && continue

    # Download just the hash file from the release to compare
    upstream_sha=$(curl -sfL \
        "https://github.com/${PLUGINS_REPO}/releases/download/${latest_tag}/${slug}.hash" \
        | awk '{print $1}' 2>/dev/null || true)

    [ -z "${upstream_sha}" ] && continue

    # Same hash = no update
    if [ "${current_sha}" = "${upstream_sha}" ]; then
        continue
    fi

    echo "[plugin-update] ${slug}: update available (${current_sha:0:12}.. -> ${upstream_sha:0:12}..)"

    # Download the full tarball + sha256 sidecar
    workdir="$(mktemp -d)"
    tarball_url="https://github.com/${PLUGINS_REPO}/releases/download/${latest_tag}"

    # Find the exact tarball filename from release assets
    tarball_name=$(curl -sf "${GITHUB_API}/releases/tags/${latest_tag}" \
        | jq -r '.assets[].name' 2>/dev/null \
        | grep "^${slug}-.*\.tar\.gz$" | grep -v '\.sha256$' | head -1 || true)

    if [ -z "${tarball_name}" ]; then
        echo "[plugin-update] WARNING: no tarball asset found for ${slug} ${latest_tag}" >&2
        FAILED=$((FAILED + 1))
        rm -rf "${workdir}"
        continue
    fi

    if ! curl -sfL "${tarball_url}/${tarball_name}" -o "${workdir}/${tarball_name}"; then
        echo "[plugin-update] WARNING: failed to download ${slug} tarball" >&2
        FAILED=$((FAILED + 1))
        rm -rf "${workdir}"
        continue
    fi

    # Verify tarball integrity via sha256 sidecar
    sidecar_sha=$(curl -sfL "${tarball_url}/${tarball_name}.sha256" \
        | awk '{print $1}' 2>/dev/null || true)
    actual_sha=$(sha256sum "${workdir}/${tarball_name}" | awk '{print $1}')
    if [ -n "${sidecar_sha}" ] && [ "${sidecar_sha}" != "${actual_sha}" ]; then
        echo "[plugin-update] FATAL: sha256 mismatch for ${slug} tarball — skipping" >&2
        FAILED=$((FAILED + 1))
        rm -rf "${workdir}"
        continue
    fi

    # Extract and verify the plugin hash
    extract_dir="$(mktemp -d)"
    tar -xzf "${workdir}/${tarball_name}" -C "${extract_dir}"
    if ! (cd "${extract_dir}" && sha256sum -c "${slug}.hash" >/dev/null 2>&1); then
        echo "[plugin-update] FATAL: ${slug} plugin hash mismatch post-extract — skipping" >&2
        FAILED=$((FAILED + 1))
        rm -rf "${workdir}" "${extract_dir}"
        continue
    fi

    # Update the baked plugin directory
    cp -a "${extract_dir}"/* "${PER_PLUGIN_DIR}/${slug}/"

    # Copy .cs to oxide/plugins/
    find "${extract_dir}" -maxdepth 1 -name '*.cs' \
        -exec cp --preserve=mode {} "${OXIDE_PLUGINS_DIR}/" \;

    # Hot-reload via RCON — Oxide detects file change but explicit reload is faster
    cs_files=$(find "${extract_dir}" -maxdepth 1 -name '*.cs' -exec basename {} .cs \;)
    for plugin_name in ${cs_files}; do
        send_rcon "oxide.reload ${plugin_name}"
        echo "[plugin-update] ${slug}: reloaded ${plugin_name} via RCON"
    done

    UPDATED=$((UPDATED + 1))
    rm -rf "${workdir}" "${extract_dir}"
done

if [ "${UPDATED}" -gt 0 ] || [ "${FAILED}" -gt 0 ]; then
    echo "[plugin-update] Check complete: ${UPDATED} updated, ${FAILED} failed"
fi
