# Extension Manager

Runtime plugin management for live Rust servers — activate, deactivate, and update Oxide plugins without restarts using in-game chat, F1 console, or RCON.

---

## Commands

| Command | Arguments | Description |
|---|---|---|
| `/extension list` | — | List enabled, patched-available, and disabled plugins |
| `/extension add <name> [source]` | name, optional source | Activate a plugin |
| `/extension remove <name>` | name | Deactivate a running plugin |
| `/extension update <name> [source]` | name, optional source | Re-fetch and hot-reload a plugin |

Console / RCON equivalents use dots:

```
extension.list
extension.add TruePVE
extension.remove TruePVE
extension.update TruePVE github
```

`<name>` is the plugin filename without `.cs` (case-insensitive for lookup, original case preserved on disk).

---

## Permission

All commands require the `extensionmanager.admin` Oxide permission.

Grant to a player:

```
oxide.grant user <steamid> extensionmanager.admin
oxide.grant group admin extensionmanager.admin
```

RCON and the server console bypass this check — they are already authenticated.

---

## Source Resolution

The `[source]` argument controls where `add` and `update` fetch plugins from.

| Source | Behaviour |
|---|---|
| *(omitted)* | Uses `PLUGIN_SOURCE` env var (default: `github`) |
| `github` | Check `patched/` → `disabled/` cache → GitHub release tarball |
| `baked` | `patched/` → `disabled/` cache only; no network fetch |
| `umod` | Download directly from umod.org |

**Cache priority for `github` / `baked` mode:**

1. **`patched/`** — pre-applied patches shipped with the image; used when a community plugin needed a source-level fix
2. **`disabled/`** — baked-in compressed copies (`.cs.gz`); instant gunzip, no network
3. **GitHub release tarball** — fetched from `PenguinzTech/penguin-rust-plugins`, SHA-256 verified
4. **umod.org** — only when `source=umod` or `PLUGIN_SOURCE=umod`

---

## How It Works

`ExtensionManager.cs` (Oxide plugin) handles permission checks and input validation, then immediately delegates all disk and network operations to `manage-plugin.sh` via a background bash fork:

```
/extension add TruePVE
  → ExtensionManager.cs validates name ([A-Za-z0-9_-], max 64 chars)
  → Process.Start("/usr/local/bin/manage-plugin.sh", ["add", "TruePVE"])
  → Returns immediately — game loop never blocks
  → manage-plugin.sh runs in background
  → Sends "say [ExtensionManager] TruePVE added — Oxide loading..." via RCON when done
```

`extension.list` is synchronous (filesystem scan, ~1ms) and never forks.

---

## Troubleshooting

All output from background operations is appended to `/tmp/pluginmgr.log` inside the container:

```bash
docker exec rust-server tail -50 /tmp/pluginmgr.log
```

Common failure messages:

| Message | Cause |
|---|---|
| `ERROR: not found on GitHub` | Slug not in `penguin-rust-plugins` releases; try `source=umod` |
| `ERROR: hash mismatch` | Tarball corrupted in transit; retry or check upstream |
| `ERROR: not currently enabled` | Tried to remove a plugin that isn't loaded |
| `ERROR: failed to add ... from umod` | umod.org unreachable or plugin slug incorrect |

---

## Plugin Directories

```
oxide/plugins/               ← active (loaded by Oxide)
    TruePVE.cs
    TruePVE.hash

oxide/plugins/patched/       ← pre-patched source files (shipped with image)
    Vanish.cs                ← patched version, not the umod original

oxide/plugins/disabled/      ← baked cache (compressed)
    truepve.cs.gz
    truepve.hash
    whitelist.cs.gz
    whitelist.hash
```
