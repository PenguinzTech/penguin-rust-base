# Plugin Reference

All plugins in this image are GPL-3.0-only. See [LICENSE](../LICENSE).

Community plugins from umod.org are fetched at build time via [penguin-rust-plugins](https://github.com/PenguinzTech/penguin-rust-plugins) — a curated, version-locked upstream cache that controls exactly which plugin versions are baked into each image.

---

## Custom / First-Party Plugins

Built and maintained by PenguinzTech. Source lives in `docker/plugins/`.

---

### AutoAdmin

**File:** `docker/plugins/AutoAdmin.cs`  
**Version:** 2.0.0

Provisions the `admin` Oxide group with the correct per-plugin permissions on every boot and on every plugin hot-reload, driven entirely by the `RUST_ADMIN_STEAMIDS` environment variable. No manual `oxide.grant` calls required.

**How it works:**

- On `OnServerInitialized`, creates the `admin` group if absent, grants all known plugin permissions to it, then adds each Steam ID from `RUST_ADMIN_STEAMIDS` to the group.
- On `OnPluginLoaded`, re-runs the permission grant for that specific plugin — catches dynamic permissions (e.g. per-vehicle VehicleLicence perms) registered during `Init`.
- Two grant strategies per plugin:
  - **Explicit list** — plugins with a fixed, well-known permission set (AdminUtilities, BGrade, CopyPaste, Vanish, TruePVE, etc.). Only those exact permissions are granted.
  - **Prefix scan** — plugins that register permissions dynamically from config (RemoverTool, VehicleLicence, Kits, ZoneManager, etc.). All permissions matching the plugin's prefix are granted.

**Notable exclusion:** `vanish.permanent` is intentionally not granted — it auto-applies invisibility and freezes movement on every connect, which is disruptive even for admins.

**Configuration:** No config file. Driven entirely by `RUST_ADMIN_STEAMIDS` (comma-separated Steam64 IDs).

---

### PluginManager

**File:** `docker/plugins/PluginManager.cs`  
**Version:** 1.0.0

Exposes runtime plugin management via console and chat without a server restart. Delegates actual file operations to `/usr/local/bin/manage-plugin.sh`.

**Commands:**

| Command | Description |
|---|---|
| `plugin.add <name> [source]` | F1/RCON: download and activate a plugin |
| `plugin.remove <name>` | F1/RCON: deactivate and remove a plugin |
| `plugin.update <name> [source]` | F1/RCON: re-download and reload a plugin |
| `plugin.list` | F1/RCON: list enabled, patched-available, and disabled plugins |
| `/plugin <sub> [name] [source]` | In-game chat equivalent (requires `pluginmanager.admin`) |

**Permissions:** `pluginmanager.admin`

**Security hardening (baked into original implementation):**

- **Shell injection prevention** — `Fork()` uses `ProcessStartInfo.ArgumentList` instead of bash `-c` string interpolation. Plugin names and sources are never interpolated into a shell string.
- **Input validation** — plugin names are validated to `[A-Za-z0-9_-]`, max 64 chars, before any fork.
- **Permission bypass prevention** — `CheckPerm()` guards against plugin-invoked console calls with `a.Connection == null && !a.IsClientside` (the `IsClientside` check blocks calls originating from other plugins via `ConsoleSystem.Run`).
- **`arg.Player()` removed** — uses `a.Connection?.player as BasePlayer` instead of the removed extension method.

---

### MorningFog

**File:** `docker/plugins/MorningFog.cs`  
**Version:** 1.0.0  
**Status:** Disabled — opt-in via `RUST_PLUGINS=morningfog`

Applies a Gaussian bell-curve fog effect during a configurable morning window (default 05:00–10:00 server time, peak at 08:00). Also fires a small random fog event in a configurable post-window hour for atmospheric variety.

**How it works:**

Polls the in-game clock every `CheckInterval` seconds (default 60s) via a `timer.Once` chain. Within the window, fog density is computed as a Gaussian curve centered on `PeakHour` with standard deviation `Sigma`. Outside the window, density is set to 0. Fog is applied via `weather.fog` console command.

**Configuration (`oxide/config/MorningFog.json`):**

| Key | Default | Description |
|---|---|---|
| `WindowStart` | `5.0` | Fog window start (server hour) |
| `WindowEnd` | `10.0` | Fog window end (server hour) |
| `PeakHour` | `8.0` | Hour of maximum fog density |
| `MaxDensity` | `1.0` | Peak fog density (0.0–1.0) |
| `Sigma` | `1.0` | Bell curve width; higher = longer ramp up/down |
| `CheckInterval` | `60` | Seconds between fog checks |
| `RandomFogHour` | `11.0` | Hour to trigger the random post-window fog |
| `RandomFogMaxDensity` | `0.13` | Upper bound for random fog density |

**On unload:** Resets fog to 0 and cancels the timer.

---

### SafeSpace

**File:** `docker/plugins/SafeSpace.cs`  
**Version:** 1.0.0  
**Status:** Disabled — opt-in via `RUST_PLUGINS=safespace`

Kid-friendly server mode. Blocks four communication vectors unless a player has the corresponding permission:

| Vector | Permission to allow | Hook used |
|---|---|---|
| Sign / photo frame painting | `safespace.signs` | `OnSignUpdate` |
| Global chat | `safespace.globalchat` | `OnPlayerChat` (blocks Global channel only; team/local/clan unaffected) |
| Voice chat | `safespace.voice` | `OnPlayerVoice` — suppressed silently; player is notified once on connect via GUIAnnouncements if present, otherwise `ChatMessage` |
| Notes (written text on note items) | `safespace.notes` | `OnItemAction`, `OnPlayerLootEnd`, `OnItemAddedToContainer` |

**Note blocking caveat:** Oxide has no pre-hook for in-game note editing (it's a client-side UI). SafeSpace instead clears `item.text` on item actions, loot-end events, and inventory additions — so anything written by an unauthorized player is wiped before it can be shared. This is a best-effort approach.

**Chat command:** `/safespace` — shows the player their current allow/block status for each vector.

**Configuration (`oxide/config/SafeSpace.json`):** Each block type can be individually toggled with `BlockSigns`, `BlockGlobalChat`, `BlockVoice`, `BlockNotes` (all `true` by default).

---

### Pets2

**File:** `docker/plugins/patched/Pets2.cs` (disabled by default)  
**Version:** 1.0.0  
**Status:** Disabled — opt-in via `RUST_PLUGINS=pets2`

Tame animal companions — one pet per player. Pets are friendly to all players and follow owner commands.

**Commands:**

| Command | Description |
|---|---|
| `/pet tame` | Tame the animal you're looking at |
| `/pet release` | Release your current pet |
| `/pet follow` | Pet follows you |
| `/pet idle` | Pet stays in place |
| `pets.attack` (console) | Order pet to attack your crosshair target |

**Why disabled by default:** Pets introduce server-side NPC AI state that interacts unpredictably with some map configurations and can cause performance spikes on high-population servers. Enable explicitly when you want it.

---

## Patched Community Plugins

Community plugins from [umod.org](https://umod.org) that required API fixes to work with current Oxide/Rust builds. Source lives in `docker/plugins/patched/`. Each file has a `// PATCHED by penguin-rust-base:` comment at the top recording the specific changes.

The root cause for most patches is the same set of Oxide 2.x / Rust API breaking changes:

| Broken API | Replacement |
|---|---|
| `BasePlayer.FindByID(ulong)` | `BasePlayer.FindAwakeOrSleeping(string)` — takes string, not ulong |
| `arg.Player()` extension method | `arg.Connection?.player as BasePlayer` |
| `net.connection` field | `.Connection` property (capital C) |
| `ConVar.Chat.ChatChannel` | `Chat.ChatChannel` (namespace moved) |
| `userID.Get()` | `userID` directly (`.Get()` removed) |
| `BuildingPrivlidge` (typo) | `BuildingPrivilege` (fixed in Facepunch source) |
| `OnPlayerChat(ConsoleSystem.Arg)` | `OnPlayerChat(BasePlayer, string, Chat.ChatChannel)` |

---

### AntiOfflineRaid

**Upstream:** [umod.org/plugins/anti-offline-raid](https://umod.org/plugins/anti-offline-raid)

**Patches applied:**
- `FindByID` → `FindAwakeOrSleeping` with `ulong.ToString()` (lines 440, 674)
- `net.connection` → `.Connection`
- `BuildingPrivlidge` → `BuildingPrivilege`
- Fixed operator precedence in a null-coalescing expression (line 361)
- `OnPlayerChat(ConsoleSystem.Arg)` hook signature → `OnPlayerChat(BasePlayer, string, Chat.ChatChannel)`

---

### BetterChat

**Upstream:** [umod.org/plugins/better-chat](https://umod.org/plugins/better-chat)

**Patches applied:**
- `FindByID` → `FindAwakeOrSleeping` with `ulong.ToString()` (line 202)
- `net.connection` → `.Connection`
- `ConVar.Chat.ChatChannel` → `Chat.ChatChannel`
- `chat.add` player ID argument: Rust API changed from `string` to `ulong`

---

### BetterChatMute

**Upstream:** [umod.org/plugins/better-chat-mute](https://umod.org/plugins/better-chat-mute)

**Patches applied:**
- `Chat.ChatChannel` removed from Oxide 2.x — replaced with `int` cast, comparing to `0` (the value of `Chat.ChatChannel.Global` per the enum definition)

---

### DynamicPVP

**Upstream:** [umod.org/plugins/dynamic-pvp](https://umod.org/plugins/dynamic-pvp)  
**Requires:** ZoneManager

**Patches applied:**
- `FindByID` → `FindAwakeOrSleeping` with `ulong.ToString()` (line 3459)
- `net.connection` → `.Connection`
- `userID.Get()` → `userID` (lines 535, 3620)

---

### NTeleportation

**Upstream:** [umod.org/plugins/n-teleportation](https://umod.org/plugins/n-teleportation)

**Patches applied:**
- `FindByID` → `FindAwakeOrSleeping` with `ulong.ToString()`
- `net.connection` → `.Connection`
- `BuildingPrivlidge` → `BuildingPrivilege`
- `PrivilegeTool` → `global::BuildingPrivilege` (Oxide 2.x namespace change)
- C# 9 target-typed `new()` expressions replaced with explicit types for C# 8 compiler compatibility

---

### PlayerAdministration

**Upstream:** [umod.org/plugins/player-administration](https://umod.org/plugins/player-administration)

**Patches applied:**
- `FindByID` → `FindAwakeOrSleeping` with `ulong.ToString()`
- `net.connection` → `.Connection`

---

### Quests

**Upstream:** [umod.org/plugins/quests](https://umod.org/plugins/quests)

**Patches applied:**
- `FindByID` → `FindAwakeOrSleeping` with `ulong.ToString()` (line 348)

---

### TreePlanter

**Upstream:** [umod.org/plugins/tree-planter](https://umod.org/plugins/tree-planter)

**Patches applied:**
- Removed Oxide APIs updated (FindByID and related connection APIs)

---

### VehicleLicence

**Upstream:** [umod.org/plugins/vehicle-licence](https://umod.org/plugins/vehicle-licence)

**Patches applied:**
- `FindByID` → `FindAwakeOrSleeping` with `ulong.ToString()`
- `userID.Get()` → `userID`

---

### ZoneManager

**Upstream:** [umod.org/plugins/zone-manager](https://umod.org/plugins/zone-manager)

**Patches applied:**
- `arg.Player()` → `arg.Connection?.player as BasePlayer`
- `userID.Get()` → `userID`

**Performance optimisations applied (8 total):**
- Distance checks use `sqrMagnitude` instead of `magnitude` (avoids `Math.Sqrt`)
- `EntityZones` changed from `List<Zone>` to `HashSet<Zone>` (O(1) membership checks)
- `ZoneGrid` spatial index added — zones bucketed by grid cell so per-entity zone lookups skip the full zone list
- Zone-entry queue uses `HashSet` deduplication to prevent re-processing the same entity
- Bounds pre-check before expensive per-zone tests
- `(Zone)component` cast instead of `GetComponent<Zone>()` where type is already known
- `List` capacity hints added on hot-path allocations to reduce GC pressure
