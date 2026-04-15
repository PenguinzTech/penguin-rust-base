#!/bin/bash
# =============================================================================
# Plugin manager — invoked by PluginManager.cs via Process.Start (background).
#
# Usage: manage-plugin.sh <add|remove|update|list> <name> [source]
#
# add:    Activate a plugin from:
#           1. patched/   — pre-patched .cs (preferred over baked cache)
#           2. disabled/  — baked .cs.gz (gunzip in-place, no network)
#           3. github     — GitHub API → tarball → sha256 verify → extract
#           4. umod       — umod.org direct download
# remove: Deactivate an enabled plugin (gzips .cs back to disabled/).
# update: Re-fetch from source and oxide.reload via RCON.
# list:   Print enabled + available (disabled + patched). Synchronous, no RCON.
#
# All output goes to /tmp/pluginmgr.log (redirected by PluginManager.cs).
# In-game feedback sent via send_rcon "say [PluginManager] ...".
#
# Env vars (inherited from container):
#   PLUGIN_SOURCE      — github (default), umod, baked
#   RUST_RCON_PASSWORD — for oxide.reload; loaded from .rcon.pw if unset
#   RUST_RCON_PORT     — default 28016
#   RUST_SERVER_IDENTITY
# =============================================================================
set -euo pipefail

# shellcheck source=lib-functions.sh
. /usr/local/lib/penguin-rust/lib-functions.sh

OXIDE_PLUGINS_DIR="/steamcmd/rust/oxide/plugins"
PER_PLUGIN_DIR="/etc/penguin-rust-plugins/per-plugin"
PLUGINS_REPO="PenguinzTech/penguin-rust-plugins"
GITHUB_API="https://api.github.com/repos/${PLUGINS_REPO}"

ACTION="${1:-}"
NAME="${2:-}"
SOURCE="${3:-${PLUGIN_SOURCE:-github}}"

load_rcon_password

# ─── list ────────────────────────────────────────────────────────────────────
if [ "${ACTION}" = "list" ]; then
    msg="=== Enabled ==="$'\n'
    for f in "${OXIDE_PLUGINS_DIR}"/*.cs; do
        [ -f "${f}" ] || continue
        msg+="  + $(basename "${f}" .cs)"$'\n'
    done
    msg+="=== Available (patched) ==="$'\n'
    for f in "${OXIDE_PLUGINS_DIR}/patched"/*.cs; do
        [ -f "${f}" ] || continue
        msg+="  ~ $(basename "${f}" .cs)"$'\n'
    done
    msg+="=== Available (disabled) ==="$'\n'
    for f in "${OXIDE_PLUGINS_DIR}/disabled"/*.cs.gz; do
        [ -f "${f}" ] || continue
        cs=$(basename "${f}" .gz)
        msg+="  - $(basename "${cs}" .cs)"$'\n'
    done
    echo "${msg}"
    exit 0
fi

# ─── Remaining commands require a name ───────────────────────────────────────
if [ -z "${NAME}" ]; then
    echo "ERROR: name required for ${ACTION}" >&2
    exit 1
fi

# ─── add ─────────────────────────────────────────────────────────────────────
if [ "${ACTION}" = "add" ]; then
    slug="${NAME,,}"  # lowercase for file and API lookups

    # 1. Patched copy takes priority (pre-patched .cs — copy directly)
    patched_file=$(find "${OXIDE_PLUGINS_DIR}/patched" -maxdepth 1 -iname "${NAME}.cs" 2>/dev/null | head -1 || true)
    if [ -n "${patched_file}" ] && [ "${SOURCE}" != "umod" ]; then
        cs_name=$(basename "${patched_file}")
        cp --preserve=mode "${patched_file}" "${OXIDE_PLUGINS_DIR}/${cs_name}"
        echo "[pluginmgr] add: ${NAME} activated from patched cache"
        send_rcon "say [PluginManager] ${NAME} added (patched) — Oxide loading..." "pluginmgr"
        exit 0
    fi

    # 2. Baked disabled/ cache (gunzip, no network)
    gz_file=$(find "${OXIDE_PLUGINS_DIR}/disabled" -maxdepth 1 -iname "${NAME}.cs.gz" 2>/dev/null | head -1 || true)
    if [ -n "${gz_file}" ] && [ "${SOURCE}" != "umod" ]; then
        cs_name=$(basename "${gz_file}" .gz)
        gunzip -c "${gz_file}" > "${OXIDE_PLUGINS_DIR}/${cs_name}"
        hash_file=$(find "${OXIDE_PLUGINS_DIR}/disabled" -maxdepth 1 -iname "${slug}.hash" 2>/dev/null | head -1 || true)
        [ -n "${hash_file}" ] && cp "${hash_file}" "${OXIDE_PLUGINS_DIR}/"
        echo "[pluginmgr] add: ${NAME} activated from baked cache"
        send_rcon "say [PluginManager] ${NAME} added — Oxide loading..." "pluginmgr"
        exit 0
    fi

    # 3. umod.org
    if [ "${SOURCE}" = "umod" ]; then
        if fetch_from_umod "${slug}"; then
            send_rcon "say [PluginManager] ${NAME} added from umod — Oxide loading..." "pluginmgr"
        else
            send_rcon "say [PluginManager] ERROR: failed to add ${NAME} from umod" "pluginmgr"
            exit 1
        fi
        exit 0
    fi

    # 4. GitHub release
    if [ "${SOURCE}" = "github" ]; then
        ALL_TAGS=$(curl -sf "${GITHUB_API}/releases?per_page=100" \
            | jq -r '.[].tag_name' 2>/dev/null || true)
        latest_tag=$(echo "${ALL_TAGS}" \
            | awk -v s="${slug}-" 'index($0,s)==1' \
            | awk -F- '{print $NF"\t"$0}' | sort -n | tail -1 | cut -f2 || true)
        if [ -z "${latest_tag}" ]; then
            send_rcon "say [PluginManager] ERROR: ${NAME} not found on GitHub" "pluginmgr"
            exit 1
        fi
        workdir="$(mktemp -d)"
        tarball_name=$(curl -sf "${GITHUB_API}/releases/tags/${latest_tag}" \
            | jq -r '.assets[].name' 2>/dev/null \
            | grep "^${slug}-.*\.tar\.gz$" | grep -v '\.sha256$' | head -1 || true)
        if [ -z "${tarball_name}" ]; then
            send_rcon "say [PluginManager] ERROR: no tarball for ${NAME}" "pluginmgr"
            rm -rf "${workdir}"; exit 1
        fi
        tarball_url="https://github.com/${PLUGINS_REPO}/releases/download/${latest_tag}"
        if ! curl -sfL "${tarball_url}/${tarball_name}" -o "${workdir}/${tarball_name}"; then
            send_rcon "say [PluginManager] ERROR: download failed for ${NAME}" "pluginmgr"
            rm -rf "${workdir}"; exit 1
        fi
        sidecar_sha=$(curl -sfL "${tarball_url}/${tarball_name}.sha256" \
            | awk '{print $1}' 2>/dev/null || true)
        actual_sha=$(sha256sum "${workdir}/${tarball_name}" | awk '{print $1}')
        if [ -n "${sidecar_sha}" ] && [ "${sidecar_sha}" != "${actual_sha}" ]; then
            send_rcon "say [PluginManager] ERROR: hash mismatch for ${NAME}" "pluginmgr"
            rm -rf "${workdir}"; exit 1
        fi
        extract_dir="$(mktemp -d)"
        tar -xzf "${workdir}/${tarball_name}" -C "${extract_dir}"
        if ! (cd "${extract_dir}" && sha256sum -c "${slug}.hash" >/dev/null 2>&1); then
            send_rcon "say [PluginManager] ERROR: plugin hash mismatch for ${NAME}" "pluginmgr"
            rm -rf "${workdir}" "${extract_dir}"; exit 1
        fi
        find "${extract_dir}" -maxdepth 1 -name '*.cs' \
            -exec cp --preserve=mode {} "${OXIDE_PLUGINS_DIR}/" \;
        cp "${extract_dir}/${slug}.hash" "${OXIDE_PLUGINS_DIR}/" 2>/dev/null || true
        rm -rf "${workdir}" "${extract_dir}"
        send_rcon "say [PluginManager] ${NAME} added from GitHub — Oxide loading..." "pluginmgr"
        exit 0
    fi

    send_rcon "say [PluginManager] ERROR: ${NAME} not found (source=${SOURCE})" "pluginmgr"
    exit 1
fi

# ─── remove ──────────────────────────────────────────────────────────────────
if [ "${ACTION}" = "remove" ]; then
    cs_file=$(find "${OXIDE_PLUGINS_DIR}" -maxdepth 1 -iname "${NAME}.cs" 2>/dev/null | head -1 || true)
    if [ -z "${cs_file}" ]; then
        send_rcon "say [PluginManager] ERROR: ${NAME} is not currently enabled" "pluginmgr"
        exit 1
    fi
    mkdir -p "${OXIDE_PLUGINS_DIR}/disabled"
    cs_basename=$(basename "${cs_file}")
    gzip -c "${cs_file}" > "${OXIDE_PLUGINS_DIR}/disabled/${cs_basename}.gz"
    rm -f "${cs_file}"
    echo "[pluginmgr] remove: ${NAME} deactivated (compressed back to disabled/)"
    send_rcon "say [PluginManager] ${NAME} removed — Oxide unloading..." "pluginmgr"
    exit 0
fi

# ─── update ──────────────────────────────────────────────────────────────────
if [ "${ACTION}" = "update" ]; then
    slug="${NAME,,}"

    if [ "${SOURCE}" = "umod" ]; then
        if ! fetch_from_umod "${slug}"; then
            send_rcon "say [PluginManager] ERROR: update failed for ${NAME} from umod" "pluginmgr"
            exit 1
        fi
    else
        ALL_TAGS=$(curl -sf "${GITHUB_API}/releases?per_page=100" \
            | jq -r '.[].tag_name' 2>/dev/null || true)
        latest_tag=$(echo "${ALL_TAGS}" \
            | awk -v s="${slug}-" 'index($0,s)==1' \
            | awk -F- '{print $NF"\t"$0}' | sort -n | tail -1 | cut -f2 || true)
        if [ -z "${latest_tag}" ]; then
            send_rcon "say [PluginManager] ERROR: ${NAME} not found for update" "pluginmgr"
            exit 1
        fi
        workdir="$(mktemp -d)"
        tarball_name=$(curl -sf "${GITHUB_API}/releases/tags/${latest_tag}" \
            | jq -r '.assets[].name' 2>/dev/null \
            | grep "^${slug}-.*\.tar\.gz$" | grep -v '\.sha256$' | head -1 || true)
        if [ -n "${tarball_name}" ]; then
            tarball_url="https://github.com/${PLUGINS_REPO}/releases/download/${latest_tag}"
            if curl -sfL "${tarball_url}/${tarball_name}" -o "${workdir}/${tarball_name}"; then
                extract_dir="$(mktemp -d)"
                tar -xzf "${workdir}/${tarball_name}" -C "${extract_dir}"
                find "${extract_dir}" -maxdepth 1 -name '*.cs' \
                    -exec cp --preserve=mode {} "${OXIDE_PLUGINS_DIR}/" \;
                rm -rf "${extract_dir}"
            fi
            rm -rf "${workdir}"
        else
            rm -rf "${workdir}"
        fi
    fi

    send_rcon "oxide.reload ${NAME}" "pluginmgr"
    send_rcon "say [PluginManager] ${NAME} updated and reloaded" "pluginmgr"
    exit 0
fi

echo "Usage: manage-plugin.sh <add|remove|update|list> <name> [source]" >&2
exit 1
