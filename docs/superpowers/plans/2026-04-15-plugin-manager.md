# Plugin Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `/plugin add|remove|update|list` commands that let owners/admins manage Oxide plugins at runtime without server restart, with all heavy work forked to background bash so the single-threaded game loop is never blocked.

**Architecture:** A baked-in Oxide C# plugin (PluginManager.cs) registers console + chat commands, validates input, then forks a bash script (manage-plugin.sh) via System.Diagnostics.Process for all mutating operations. The bash script leverages existing lib-functions.sh helpers and sends RCON feedback when done. The /list command is synchronous (filesystem scan, ~1ms).

**Tech Stack:** C# / Oxide 2.x RustPlugin, Bash, existing lib-functions.sh helpers (fetch_from_umod, send_rcon, load_rcon_password).

---

## Task 1: manage-plugin.sh

**Files:**
- Create: `docker/manage-plugin.sh`

- [ ] **Step 1: Create the script**

```bash
#!/bin/bash
# =============================================================================
# manage-plugin.sh — runtime plugin add/remove/update/list for Oxide.
# Called by PluginManager.cs via System.Diagnostics.Process (background fork).
# Sources lib-functions.sh for send_rcon, load_rcon_password, fetch_from_umod.
# =============================================================================
set -euo pipefail

# ─── Bootstrap ───────────────────────────────────────────────────────────────
LIB="/usr/local/lib/penguin-rust/lib-functions.sh"
# shellcheck source=/usr/local/lib/penguin-rust/lib-functions.sh
source "${LIB}"

OXIDE_PLUGINS_DIR="${OXIDE_PLUGINS_DIR:-/steamcmd/rust/oxide/plugins}"
PER_PLUGIN_DIR="${PER_PLUGIN_DIR:-/etc/penguin-rust-plugins/per-plugin}"
PLUGINS_REPO="${PLUGINS_REPO:-PenguinzTech/penguin-rust-plugins}"
GITHUB_API="https://api.github.com/repos/${PLUGINS_REPO}"

load_rcon_password

ACTION="${1:-}"
NAME="${2:-}"
SOURCE="${3:-${PLUGIN_SOURCE:-github}}"

# ─── Usage guard ─────────────────────────────────────────────────────────────
if [ -z "${ACTION}" ]; then
    echo "[manage-plugin] usage: manage-plugin.sh <add|remove|update|list> [name] [source]" >&2
    exit 1
fi

# ─── Helpers ─────────────────────────────────────────────────────────────────

# Emit a log line to stdout (captured by Docker logs) and echo to stderr for
# immediate visibility when run interactively.
log() {
    echo "[manage-plugin] $*"
}

# Broadcast a message to all in-game players via RCON say.
broadcast() {
    send_rcon "say [PluginManager] $*" "manage-plugin"
}

# Find the exact .cs path in OXIDE_PLUGINS_DIR, case-insensitively.
# Prints the path; returns 1 if not found.
find_active_cs() {
    local name="$1"
    local found
    found=$(find "${OXIDE_PLUGINS_DIR}" -maxdepth 1 -iname "${name}.cs" 2>/dev/null | head -1 || true)
    if [ -z "${found}" ]; then
        return 1
    fi
    echo "${found}"
}

# Find the .cs.gz in disabled/, case-insensitively.
# Prints the path; returns 1 if not found.
find_disabled_gz() {
    local name="$1"
    local found
    found=$(find "${OXIDE_PLUGINS_DIR}/disabled" -maxdepth 1 -iname "${name}.cs.gz" 2>/dev/null | head -1 || true)
    if [ -z "${found}" ]; then
        return 1
    fi
    echo "${found}"
}

# Fetch the latest GitHub release tag for a slug.
# Prints the tag string; returns 1 if none found.
latest_github_tag() {
    local slug="$1"
    local tag
    tag=$(curl -sf "${GITHUB_API}/releases" \
        | jq -r '.[].tag_name' 2>/dev/null \
        | awk -v s="${slug}-" 'index($0,s)==1' \
        | awk -F- '{print $NF"\t"$0}' \
        | sort -n \
        | tail -1 \
        | cut -f2 \
        || true)
    if [ -z "${tag}" ]; then
        return 1
    fi
    echo "${tag}"
}

# Download and install a plugin from a GitHub release tag.
# Mirrors _download_and_activate from start.sh.
# Returns 0 on success, 1 on download failure. Exits on integrity failure.
_github_download() {
    local slug="$1"
    local latest_tag="$2"

    local tarball_name
    tarball_name=$(curl -sf "${GITHUB_API}/releases/tags/${latest_tag}" \
        | jq -r '.assets[].name' 2>/dev/null \
        | grep "^${slug}-.*\.tar\.gz$" | grep -v '\.sha256$' | head -1 || true)
    [ -z "${tarball_name}" ] && return 1

    local workdir extract_dir
    workdir="$(mktemp -d)"
    extract_dir="$(mktemp -d)"

    if ! curl -sfL \
        "https://github.com/${PLUGINS_REPO}/releases/download/${latest_tag}/${tarball_name}" \
        -o "${workdir}/${tarball_name}"; then
        rm -rf "${workdir}" "${extract_dir}"
        return 1
    fi

    local sidecar_sha actual_sha
    sidecar_sha=$(curl -sfL \
        "https://github.com/${PLUGINS_REPO}/releases/download/${latest_tag}/${tarball_name}.sha256" \
        | awk '{print $1}' || true)
    actual_sha=$(sha256sum "${workdir}/${tarball_name}" | awk '{print $1}')

    if [ -n "${sidecar_sha}" ] && [ "${sidecar_sha}" != "${actual_sha}" ]; then
        log "FATAL: tarball sha256 mismatch for ${slug} — refusing to install"
        rm -rf "${workdir}" "${extract_dir}"
        exit 1
    fi

    tar -xzf "${workdir}/${tarball_name}" -C "${extract_dir}"
    if ! (cd "${extract_dir}" && sha256sum -c "${slug}.hash" >/dev/null 2>&1); then
        log "FATAL: ${slug} plugin hash mismatch post-extract — refusing to install"
        rm -rf "${workdir}" "${extract_dir}"
        exit 1
    fi

    find "${extract_dir}" -maxdepth 1 -name '*.cs' \
        -exec cp --preserve=mode {} "${OXIDE_PLUGINS_DIR}/" \;
    cp "${extract_dir}/${slug}.hash" "${OXIDE_PLUGINS_DIR}/"

    # Refresh per-plugin dir and disabled/ cache for next restart
    mkdir -p "${PER_PLUGIN_DIR}/${slug}"
    cp -a "${extract_dir}"/* "${PER_PLUGIN_DIR}/${slug}/" 2>/dev/null || true
    find "${extract_dir}" -maxdepth 1 -name '*.cs' | \
        while IFS= read -r cs; do
            gzip -c "${cs}" > "${OXIDE_PLUGINS_DIR}/disabled/$(basename "${cs}").gz"
        done
    cp "${extract_dir}/${slug}.hash" "${OXIDE_PLUGINS_DIR}/disabled/"

    rm -rf "${workdir}" "${extract_dir}"
    return 0
}

# ─── Subcommands ─────────────────────────────────────────────────────────────

cmd_list() {
    local enabled_list disabled_list msg

    enabled_list=$(find "${OXIDE_PLUGINS_DIR}" -maxdepth 1 -name '*.cs' \
        -exec basename {} .cs \; 2>/dev/null | sort | tr '\n' ' ' || true)
    disabled_list=$(find "${OXIDE_PLUGINS_DIR}/disabled" -maxdepth 1 -name '*.cs.gz' \
        -exec basename {} .cs.gz \; 2>/dev/null | sort | tr '\n' ' ' || true)

    log "=== Enabled plugins ==="
    log "${enabled_list:-  (none)}"
    log "=== Available (disabled) ==="
    log "${disabled_list:-  (none)}"

    msg="Enabled: ${enabled_list:-none} | Available: ${disabled_list:-none}"
    broadcast "${msg}"
}

cmd_add() {
    local name="$1"
    local source="$2"
    local slug="${name,,}"

    log "add: name=${name} source=${source}"

    # Already active?
    if find_active_cs "${name}" >/dev/null 2>&1; then
        log "${name} is already active"
        broadcast "${name} is already active"
        return 0
    fi

    if [ "${source}" = "umod" ]; then
        if fetch_from_umod "${slug}"; then
            broadcast "${name} added (umod) — Oxide loading..."
        else
            log "ERROR: failed to fetch ${name} from umod"
            broadcast "ERROR: failed to add ${name} (umod fetch failed)"
            return 1
        fi
        return 0
    fi

    if [ "${source}" = "local" ] || [ "${source}" = "github" ]; then
        # Try baked cache first
        local gz_path
        if gz_path=$(find_disabled_gz "${name}"); then
            local cs_name
            cs_name=$(basename "${gz_path}" .gz)
            gunzip -c "${gz_path}" > "${OXIDE_PLUGINS_DIR}/${cs_name}"
            # Copy .hash sidecar if present (lowercase slug)
            local hash_src="${OXIDE_PLUGINS_DIR}/disabled/${slug}.hash"
            if [ -f "${hash_src}" ]; then
                cp "${hash_src}" "${OXIDE_PLUGINS_DIR}/"
            fi
            log "activated from baked cache: ${cs_name}"
            broadcast "${name} added (baked cache) — Oxide loading..."
            return 0
        fi

        if [ "${source}" = "local" ]; then
            log "ERROR: ${name} not in baked cache and source=local — cannot download"
            broadcast "ERROR: ${name} not found in baked cache (source=local)"
            return 1
        fi

        # source=github, not in baked cache — download
        local latest_tag
        if ! latest_tag=$(latest_github_tag "${slug}"); then
            log "ERROR: ${name} not found in penguin-rust-plugins releases"
            broadcast "ERROR: ${name} not found on GitHub — check the plugin name"
            return 1
        fi
        if _github_download "${slug}" "${latest_tag}"; then
            broadcast "${name} added (github) — Oxide loading..."
        else
            log "ERROR: failed to download ${name} from GitHub (${latest_tag})"
            broadcast "ERROR: failed to add ${name} (GitHub download failed)"
            return 1
        fi
        return 0
    fi

    log "ERROR: unknown source '${source}' — must be github, umod, or local"
    broadcast "ERROR: unknown source '${source}'"
    return 1
}

cmd_remove() {
    local name="$1"
    local cs_path

    log "remove: name=${name}"

    if ! cs_path=$(find_active_cs "${name}"); then
        log "ERROR: ${name}.cs not found in active plugins"
        broadcast "ERROR: ${name} is not currently active"
        return 1
    fi

    local slug="${name,,}"
    mkdir -p "${OXIDE_PLUGINS_DIR}/disabled"
    gzip -c "${cs_path}" > "${OXIDE_PLUGINS_DIR}/disabled/${name}.cs.gz"
    # Copy hash sidecar if present
    if [ -f "${OXIDE_PLUGINS_DIR}/${slug}.hash" ]; then
        cp "${OXIDE_PLUGINS_DIR}/${slug}.hash" "${OXIDE_PLUGINS_DIR}/disabled/${slug}.hash"
        rm -f "${OXIDE_PLUGINS_DIR}/${slug}.hash"
    fi
    # Delete .cs — Oxide file-watch fires unload automatically
    rm -f "${cs_path}"

    log "removed: ${name} (moved to disabled/)"
    broadcast "${name} removed — Oxide unloading..."
}

cmd_update() {
    local name="$1"
    local source="$2"
    local slug="${name,,}"

    log "update: name=${name} source=${source}"

    if [ "${source}" = "umod" ]; then
        if fetch_from_umod "${slug}"; then
            send_rcon "oxide.reload ${name}" "manage-plugin"
            broadcast "${name} updated (umod) and reloaded"
        else
            log "ERROR: failed to update ${name} from umod"
            broadcast "ERROR: failed to update ${name} (umod fetch failed)"
            return 1
        fi
        return 0
    fi

    if [ "${source}" = "local" ]; then
        # Re-activate from baked cache
        local gz_path
        if gz_path=$(find_disabled_gz "${name}"); then
            local cs_name
            cs_name=$(basename "${gz_path}" .gz)
            gunzip -c "${gz_path}" > "${OXIDE_PLUGINS_DIR}/${cs_name}"
            local hash_src="${OXIDE_PLUGINS_DIR}/disabled/${slug}.hash"
            if [ -f "${hash_src}" ]; then
                cp "${hash_src}" "${OXIDE_PLUGINS_DIR}/"
            fi
            send_rcon "oxide.reload ${name}" "manage-plugin"
            broadcast "${name} updated (local baked) and reloaded"
        else
            log "ERROR: ${name} not in baked cache (source=local)"
            broadcast "ERROR: ${name} not found in baked cache"
            return 1
        fi
        return 0
    fi

    if [ "${source}" = "github" ]; then
        local latest_tag
        if ! latest_tag=$(latest_github_tag "${slug}"); then
            log "WARNING: ${name} not found on GitHub — trying baked cache"
            local gz_path
            if gz_path=$(find_disabled_gz "${name}"); then
                local cs_name
                cs_name=$(basename "${gz_path}" .gz)
                gunzip -c "${gz_path}" > "${OXIDE_PLUGINS_DIR}/${cs_name}"
                send_rcon "oxide.reload ${name}" "manage-plugin"
                broadcast "${name} updated (baked cache fallback) and reloaded"
                return 0
            fi
            broadcast "ERROR: ${name} not found on GitHub or in baked cache"
            return 1
        fi

        # Compare hashes to see if update is needed
        local local_hash_file="${OXIDE_PLUGINS_DIR}/disabled/${slug}.hash"
        local local_sha=""
        if [ -f "${local_hash_file}" ]; then
            local_sha=$(awk '{print $1}' "${local_hash_file}")
        fi

        local upstream_sha
        upstream_sha=$(curl -sfL \
            "https://github.com/${PLUGINS_REPO}/releases/download/${latest_tag}/${slug}.hash" \
            | awk '{print $1}' 2>/dev/null || true)

        if [ -n "${local_sha}" ] && [ -n "${upstream_sha}" ] && [ "${local_sha}" = "${upstream_sha}" ]; then
            log "${name} is already up to date (${local_sha:0:12}..)"
            send_rcon "oxide.reload ${name}" "manage-plugin"
            broadcast "${name} already up to date — reloaded"
            return 0
        fi

        if _github_download "${slug}" "${latest_tag}"; then
            send_rcon "oxide.reload ${name}" "manage-plugin"
            broadcast "${name} updated (github) and reloaded"
        else
            log "ERROR: failed to download update for ${name}"
            broadcast "ERROR: failed to update ${name} (GitHub download failed)"
            return 1
        fi
        return 0
    fi

    log "ERROR: unknown source '${source}'"
    broadcast "ERROR: unknown source '${source}'"
    return 1
}

# ─── Dispatch ────────────────────────────────────────────────────────────────

case "${ACTION}" in
    list)
        cmd_list
        ;;
    add)
        if [ -z "${NAME}" ]; then
            log "ERROR: 'add' requires a plugin name"
            broadcast "ERROR: usage: plugin.add <name> [source]"
            exit 1
        fi
        cmd_add "${NAME}" "${SOURCE}"
        ;;
    remove)
        if [ -z "${NAME}" ]; then
            log "ERROR: 'remove' requires a plugin name"
            broadcast "ERROR: usage: plugin.remove <name>"
            exit 1
        fi
        cmd_remove "${NAME}"
        ;;
    update)
        if [ -z "${NAME}" ]; then
            log "ERROR: 'update' requires a plugin name"
            broadcast "ERROR: usage: plugin.update <name> [source]"
            exit 1
        fi
        cmd_update "${NAME}" "${SOURCE}"
        ;;
    *)
        log "ERROR: unknown action '${ACTION}' — must be add|remove|update|list"
        broadcast "ERROR: unknown action '${ACTION}'"
        exit 1
        ;;
esac
```

Create the file at `docker/manage-plugin.sh` with the content above and make it executable:
```bash
chmod +x docker/manage-plugin.sh
```

- [ ] **Step 2: Verify with shellcheck**

```bash
shellcheck -x --source-path=docker docker/manage-plugin.sh
```

Expected: no warnings or errors.

- [ ] **Step 3: Commit**

```bash
git add docker/manage-plugin.sh
git commit -m "feat: manage-plugin.sh for runtime plugin add/remove/update/list"
```

---

## Task 2: PluginManager.cs

**Files:**
- Create: `docker/plugins/PluginManager.cs`

- [ ] **Step 1: Create PluginManager.cs**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("PluginManager", "PenguinzTech", "1.0.0")]
    [Description("Runtime plugin management: add/remove/update/list via console and chat")]
    class PluginManager : RustPlugin
    {
        private const string AdminPerm = "pluginmanager.admin";
        private const string ScriptPath = "/usr/local/bin/manage-plugin.sh";
        private static readonly string PluginsDir = "/steamcmd/rust/oxide/plugins";

        void Init()
        {
            permission.RegisterPermission(AdminPerm, this);
        }

        // ── Console commands (F1 console + RCON) ─────────────────────────────

        [ConsoleCommand("plugin.add")]
        void CmdAdd(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg))
            {
                arg.ReplyWith("No permission: pluginmanager.admin required.");
                return;
            }
            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Usage: plugin.add <name> [source]");
                return;
            }
            string name = arg.Args[0];
            if (!IsValidName(name))
            {
                arg.ReplyWith($"Invalid plugin name '{name}'. Only [A-Za-z0-9_-] allowed, max 64 chars.");
                return;
            }
            string source = arg.Args.Length >= 2 ? arg.Args[1] : "";
            ForkScript("add", name, source);
            arg.ReplyWith($"Job started: add {name}. Check console/broadcast for completion.");
        }

        [ConsoleCommand("plugin.remove")]
        void CmdRemove(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg))
            {
                arg.ReplyWith("No permission: pluginmanager.admin required.");
                return;
            }
            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Usage: plugin.remove <name>");
                return;
            }
            string name = arg.Args[0];
            if (!IsValidName(name))
            {
                arg.ReplyWith($"Invalid plugin name '{name}'. Only [A-Za-z0-9_-] allowed, max 64 chars.");
                return;
            }
            ForkScript("remove", name);
            arg.ReplyWith($"Job started: remove {name}. Check console/broadcast for completion.");
        }

        [ConsoleCommand("plugin.update")]
        void CmdUpdate(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg))
            {
                arg.ReplyWith("No permission: pluginmanager.admin required.");
                return;
            }
            if (arg.Args == null || arg.Args.Length < 1)
            {
                arg.ReplyWith("Usage: plugin.update <name> [source]");
                return;
            }
            string name = arg.Args[0];
            if (!IsValidName(name))
            {
                arg.ReplyWith($"Invalid plugin name '{name}'. Only [A-Za-z0-9_-] allowed, max 64 chars.");
                return;
            }
            string source = arg.Args.Length >= 2 ? arg.Args[1] : "";
            ForkScript("update", name, source);
            arg.ReplyWith($"Job started: update {name}. Check console/broadcast for completion.");
        }

        [ConsoleCommand("plugin.list")]
        void CmdList(ConsoleSystem.Arg arg)
        {
            if (!HasPermission(arg))
            {
                arg.ReplyWith("No permission: pluginmanager.admin required.");
                return;
            }
            arg.ReplyWith(BuildPluginList());
        }

        // ── Chat command: /plugin <action> [name] [source] ───────────────────

        [ChatCommand("plugin")]
        void ChatPlugin(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player))
            {
                SendReply(player, "No permission: pluginmanager.admin required.");
                return;
            }
            if (args == null || args.Length < 1)
            {
                SendReply(player, "Usage: /plugin <add|remove|update|list> [name] [source]");
                return;
            }

            string action = args[0].ToLowerInvariant();

            switch (action)
            {
                case "list":
                    SendReply(player, BuildPluginList());
                    return;

                case "add":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Usage: /plugin add <name> [source]");
                        return;
                    }
                    if (!IsValidName(args[1]))
                    {
                        SendReply(player, $"Invalid plugin name '{args[1]}'. Only [A-Za-z0-9_-] allowed, max 64 chars.");
                        return;
                    }
                    ForkScript("add", args[1], args.Length >= 3 ? args[2] : "");
                    SendReply(player, $"Job started: add {args[1]}. Watch chat for completion.");
                    return;

                case "remove":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Usage: /plugin remove <name>");
                        return;
                    }
                    if (!IsValidName(args[1]))
                    {
                        SendReply(player, $"Invalid plugin name '{args[1]}'. Only [A-Za-z0-9_-] allowed, max 64 chars.");
                        return;
                    }
                    ForkScript("remove", args[1]);
                    SendReply(player, $"Job started: remove {args[1]}. Watch chat for completion.");
                    return;

                case "update":
                    if (args.Length < 2)
                    {
                        SendReply(player, "Usage: /plugin update <name> [source]");
                        return;
                    }
                    if (!IsValidName(args[1]))
                    {
                        SendReply(player, $"Invalid plugin name '{args[1]}'. Only [A-Za-z0-9_-] allowed, max 64 chars.");
                        return;
                    }
                    ForkScript("update", args[1], args.Length >= 3 ? args[2] : "");
                    SendReply(player, $"Job started: update {args[1]}. Watch chat for completion.");
                    return;

                default:
                    SendReply(player, $"Unknown action '{action}'. Valid: add, remove, update, list.");
                    return;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private bool HasPermission(ConsoleSystem.Arg arg)
        {
            // RCON / server console (no player) = always allowed
            if (arg.Connection == null) return true;
            var player = arg.Player();
            return player != null && permission.UserHasPermission(player.UserIDString, AdminPerm);
        }

        private bool HasPermission(BasePlayer player)
        {
            return permission.UserHasPermission(player.UserIDString, AdminPerm);
        }

        // Validate plugin name: only [A-Za-z0-9_-], 1-64 chars
        private bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
            foreach (char c in name)
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
            return true;
        }

        // Fork manage-plugin.sh in background — returns immediately
        private void ForkScript(string action, string pluginName, string source = "")
        {
            var args = string.IsNullOrEmpty(source)
                ? $"'{action}' '{pluginName}'"
                : $"'{action}' '{pluginName}' '{source}'";
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{ScriptPath} {args} >>/tmp/pluginmgr.log 2>&1 &\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
            // Fire-and-forget — do NOT Wait(). Script sends RCON feedback when done.
        }

        // Synchronous list (filesystem scan — fast, no network)
        private string BuildPluginList()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Enabled plugins ===");
            if (Directory.Exists(PluginsDir))
            {
                foreach (var f in Directory.GetFiles(PluginsDir, "*.cs"))
                    sb.AppendLine($"  + {Path.GetFileNameWithoutExtension(f)}");
            }
            var disabled = Path.Combine(PluginsDir, "disabled");
            if (Directory.Exists(disabled))
            {
                sb.AppendLine("=== Available (disabled) ===");
                foreach (var f in Directory.GetFiles(disabled, "*.cs.gz"))
                {
                    // Strip both .gz and .cs to get the plugin name
                    var withoutGz = Path.GetFileNameWithoutExtension(f);
                    var pluginName = Path.GetFileNameWithoutExtension(withoutGz);
                    sb.AppendLine($"  - {pluginName}");
                }
            }
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 2: Verify brace balance**

Since the Oxide compiler is not available in the build environment, verify basic C# structural correctness:

```bash
echo "Syntax check — brace balance (counts must match):"
echo "  Open  {: $(grep -c '{' docker/plugins/PluginManager.cs)"
echo "  Close }: $(grep -c '}' docker/plugins/PluginManager.cs)"
```

Counts must match. Also verify the file contains the expected key symbols:

```bash
grep -c '\[ConsoleCommand' docker/plugins/PluginManager.cs   # expect 4
grep -c '\[ChatCommand'    docker/plugins/PluginManager.cs   # expect 1
grep -c 'IsValidName'      docker/plugins/PluginManager.cs   # expect 5+ (def + 4 call sites)
grep -c 'ForkScript'       docker/plugins/PluginManager.cs   # expect 4+ (def + 3 call sites)
```

- [ ] **Step 3: Commit**

```bash
git add docker/plugins/PluginManager.cs
git commit -m "feat: PluginManager Oxide plugin for runtime plugin management"
```

---

## Task 3: Dockerfile + CI wiring

**Files:**
- Modify: `docker/Dockerfile`
- Modify: `.github/workflows/security.yml`

- [ ] **Step 1: Update Dockerfile**

Find the line that copies `AutoAdmin.cs` into the plugins directory. It will look like:
```dockerfile
COPY --chown=rustserver:rustserver plugins/AutoAdmin.cs /steamcmd/rust/oxide/plugins/
```

Immediately after that line, add:
```dockerfile
COPY --chown=rustserver:rustserver plugins/PluginManager.cs /steamcmd/rust/oxide/plugins/
```

Find the section where `check-plugin-updates.sh` (or similar scripts) are copied to `/usr/local/bin/`. After that block, add:
```dockerfile
COPY --chown=rustserver:rustserver manage-plugin.sh /usr/local/bin/manage-plugin.sh
RUN chmod +x /usr/local/bin/manage-plugin.sh
```

- [ ] **Step 2: Add shellcheck in security.yml**

In the `lint-shell` job, after the last existing `shellcheck` line, add:
```yaml
          shellcheck -x --source-path=docker docker/manage-plugin.sh
```

- [ ] **Step 3: Commit**

```bash
git add docker/Dockerfile .github/workflows/security.yml
git commit -m "chore: wire manage-plugin.sh and PluginManager.cs into image"
```

---

## Task 4: Docs

**Files:**
- Create: `docs/plugin-manager.md`

- [ ] **Step 1: Create docs/plugin-manager.md**

```markdown
# Plugin Manager

Runtime plugin management via in-game console and chat commands. Lets owners and administrators add, remove, update, and list Oxide plugins without server restart or SSH access.

## Commands

Available as console commands (RCON, F1 console) and chat commands (`/plugin <action>`):

| Console | Chat | Description |
|---|---|---|
| `plugin.add <name> [source]` | `/plugin add <name> [source]` | Activate a plugin |
| `plugin.remove <name>` | `/plugin remove <name>` | Deactivate a plugin |
| `plugin.update <name> [source]` | `/plugin update <name> [source]` | Update to latest and hot-reload |
| `plugin.list` | `/plugin list` | List all locally available plugins |

`<name>` is the `.cs` filename without extension (case-insensitive). `[source]` is optional: `github` (default), `umod`, or `local`.

## Permissions

`pluginmanager.admin` — required for all commands. Grant it:

```
oxide.grant group admin pluginmanager.admin
```

RCON and server console bypass the permission check (already trusted).

## How it works

`plugin.add`, `plugin.remove`, and `plugin.update` fork a background bash script (`/usr/local/bin/manage-plugin.sh`) so the single-threaded game loop is never blocked. Completion is reported back to all connected admins via RCON `say` as `[PluginManager] <message>`.

`plugin.list` is synchronous (filesystem scan, ~1ms).

## Source resolution

| Source | Behaviour |
|---|---|
| `github` (default) | Check baked cache first; download from `penguin-rust-plugins` releases if missing or stale |
| `umod` | Always fetch fresh from umod.org |
| `local` | Baked cache only — no network calls |

## Examples

```
# Add TruePVE from default source (github)
plugin.add TruePVE

# Add a plugin from umod
plugin.add Kits umod

# Remove a plugin
plugin.remove Whitelist

# Update to latest
plugin.update TruePVE

# List available plugins
plugin.list
```

## Notes

- Operation logs written to `/tmp/pluginmgr.log` inside the container
- Oxide auto-loads added plugins and auto-unloads removed plugins (file-watch)
- `plugin.update` explicitly calls `oxide.reload <name>` for immediate hot-reload
```

- [ ] **Step 2: Commit**

```bash
git add docs/plugin-manager.md
git commit -m "docs: plugin manager reference"
```
