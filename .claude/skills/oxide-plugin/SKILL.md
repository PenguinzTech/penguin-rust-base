---
name: oxide-plugin
description: Create, review, or patch Oxide/uMod plugins for the Rust dedicated server. Use when the user asks to build a new plugin, review an existing one, fix a compile error, or audit for performance/security issues. Covers current Oxide API, common broken APIs, performance patterns, MySQL, supercrond timers, CUI, and security.
tools: Read, Glob, Grep, Bash, Edit, Write
---

# Oxide Plugin Developer

Write, review, and patch Oxide (uMod) plugins for the Rust dedicated server. This skill encodes current API correctness, performance patterns, security rules, and this codebase's conventions.

See [references/api-and-patterns.md](references/api-and-patterns.md) for the full API quick-reference.

---

## Workflow

### Creating a new plugin

1. **Determine storage needs** — ephemeral state → in-memory; persistent cross-wipe → MySQL; small persistent → `Interface.Oxide.DataFileSystem`
2. **Determine scheduling needs** — periodic work → `timer.Every`; cron-style (daily wipe, nightly events) → supercrond pattern; never use `OnTick`/`Update`
3. **Draft hook set** — list only the hooks actually needed; see references for correct signatures
4. **Scaffold** using the standard template below
5. **Security pass** — parameterized SQL, input validation, permission checks
6. **Performance pass** — no LINQ in hot paths, no per-tick allocations, timers cancel on `Unload`

### Reviewing an existing plugin

1. Check all hook signatures against [Removed / Changed APIs](#removed--changed-apis)
2. Check for `OnTick`, `Update`, or frame-rate hooks doing heavy work
3. Check all SQL for string concatenation (injection risk)
4. Check `ConsoleSystem.Arg` handlers for `arg.Player()` (removed)
5. Check `CheckPerm` / console permission patterns for null-connection bypass
6. Verify timers are stored and cancelled in `Unload`
7. Verify CUI panels are destroyed before rebuild

---

## Standard Plugin Template

```csharp
// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 PenguinzTech <https://penguintech.io>
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("MyPlugin", "PenguinzTech", "1.0.0")]
    [Description("What this plugin does")]
    class MyPlugin : RustPlugin
    {
        // ── Permissions ───────────────────────────────────────────────────────
        const string PermUse   = "myplugin.use";
        const string PermAdmin = "myplugin.admin";

        // ── State ─────────────────────────────────────────────────────────────
        Timer _tickTimer;

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        void OnServerInitialized()
        {
            // one-time setup, MySQL connect, data load
            _tickTimer = timer.Every(30f, OnInterval);
        }

        void Unload()
        {
            _tickTimer?.Destroy();
            // destroy all open CUI panels, close DB connection
        }

        void OnInterval() { /* periodic work */ }

        // ── Hooks ─────────────────────────────────────────────────────────────
        void OnPlayerConnected(BasePlayer player) { }

        // ── Chat commands ─────────────────────────────────────────────────────
        [ChatCommand("mycmd")]
        void CmdMy(BasePlayer player, string _, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermUse)) return;
        }

        // ── Console commands ──────────────────────────────────────────────────
        [ConsoleCommand("mycmd.action")]
        void CcAction(ConsoleSystem.Arg a)
        {
            if (!CheckPerm(a)) return;
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        bool CheckPerm(ConsoleSystem.Arg a)
        {
            // True server console only (no connection, not a plugin-invoked call)
            if (a.Connection == null && !a.IsClientside) return true;
            var p = a.Connection?.player as BasePlayer;
            if (p != null && permission.UserHasPermission(p.UserIDString, PermAdmin)) return true;
            a.ReplyWith("No permission.");
            return false;
        }
    }
}
```

---

## Deprecated APIs (avoid even if still compiling)

These APIs still work today but are flagged for removal. **Always use the replacement** — deprecated APIs accumulate technical debt and break silently on Oxide updates.

| Deprecated | Use instead |
|---|---|
| `player.displayName` in Covalence code | `IPlayer.Name` |
| `ConVar.Chat.ChatChannel` | `Chat.ChatChannel` |
| `net.connection` field on `ConsoleSystem.Arg` | `.Connection` property |
| `ConsoleSystem.Arg.GetString(int)` without length guard | Check `a.Args?.Length > n` first |
| Synchronous HTTP (`WebClient`, `HttpClient.GetResult()`) | `webrequest.Enqueue(url, null, cb, this)` |
| `RustPlugin.rust` static field | `Interface.Oxide.GetLibrary<Oxide.Game.Rust.Libraries.Rust>()` |
| `Interface.GetMod("Oxide.Core")` | `Interface.Oxide` directly |
| `BuildingPrivlidge` (typo) | `BuildingPrivilege` |

When writing new code: if in doubt, check [references/api-and-patterns.md](references/api-and-patterns.md) — the deprecated table there is the authoritative list.

---

## Removed / Changed APIs

These APIs were removed in recent Oxide/Rust updates. **Using them causes compile failure or silent no-ops.**

| Old (broken) | New (correct) | Notes |
|---|---|---|
| `BasePlayer.FindByID(ulong id)` | `BasePlayer.FindAwakeOrSleeping(id.ToString())` | Method removed; takes string |
| `arg.Player()` | `arg.Connection?.player as BasePlayer` | Extension method removed |
| `ConVar.Chat.ChatChannel` | `Chat.ChatChannel` | Namespace moved |
| `net.connection` on `ConsoleSystem.Arg` | `.Connection` (capital C) | Property renamed |
| `OnPlayerChat(ConsoleSystem.Arg args)` | `OnPlayerChat(BasePlayer player, string message, Chat.ChatChannel channel)` | Hook signature changed |
| `chat.add(Chat.ChatChannel, ulong, string, string, string)` | Verify against current umod source | Third arg changed |
| `BuildingPrivlidge` | `BuildingPrivilege` | Typo fixed in Facepunch source |

---

## Performance Rules

### Never use frame/tick hooks for work
```csharp
// BAD — fires every frame (~60/s), decimates server perf
void OnTick() { ScanAllPlayers(); }

// GOOD — use a timer
Timer _t;
void OnServerInitialized() => _t = timer.Every(5f, ScanAllPlayers);
void Unload() => _t?.Destroy();
```

### Cache player lookups
```csharp
// BAD — O(n) linear scan every call
var p = BasePlayer.activePlayerList.Find(x => x.UserIDString == id);

// GOOD — O(1) dictionary or FindAwakeOrSleeping
var p = BasePlayer.FindAwakeOrSleeping(id);
```

### Avoid LINQ in hot paths
```csharp
// BAD — allocates enumerator + lambda every call in a hook
var rich = players.Where(p => p.IsAdmin).ToList();

// GOOD — iterate directly, early exit
foreach (var p in BasePlayer.activePlayerList)
    if (p.IsAdmin) { /* ... */ break; }
```

### Store timers, destroy on Unload
```csharp
// All active timers must be stored and destroyed
private readonly List<Timer> _timers = new List<Timer>();

void OnServerInitialized()
{
    _timers.Add(timer.Every(10f, DoWork));
    _timers.Add(timer.Once(60f, DoOnce));
}

void Unload()
{
    foreach (var t in _timers) t?.Destroy();
    _timers.Clear();
}
```

---

## When to Use Supercrond (Scheduled Timers)

Use `timer.Every` / supercrond-style scheduling for work that repeats at a fixed cadence and does not need to be frame-accurate.

| Pattern | Use When |
|---|---|
| `timer.Once(delay, cb)` | Deferred one-shot action (e.g. grace period after connect) |
| `timer.Every(interval, cb)` | Polling, periodic saves, stat collection |
| Cron string via supercrond | Wipe schedules, daily resets, nightly events keyed to wall clock |
| `InvokeRepeating` on MonoBehaviour | Per-entity periodic logic tied to a GameObject lifecycle |

**Never** use `OnTick`, `Update`, or `FixedUpdate` for anything heavier than a flag check.

---

## When to Use MySQL

| Use MySQL | Use DataFileSystem |
|---|---|
| Data survives wipes and must be queryable cross-plugin | Small per-wipe config/state |
| Player progression, guilds, economy, achievements | Plugin config files |
| Data accessed by multiple plugins | Data only one plugin needs |
| Leaderboards, aggregations, history | Simple key-value blobs |

### MySQL patterns

```csharp
[PluginReference] Plugin Clans;
private Core.MySql.Libraries.MySql _mysql;
private Connection _db;

void OnServerInitialized()
{
    _mysql = Interface.Oxide.GetLibrary<Core.MySql.Libraries.MySql>();
    _db = _mysql.OpenDb("host", 3306, "db", "user", "pass", this);
    _mysql.ExecuteNonQuery(Sql.Builder.Append(@"
        CREATE TABLE IF NOT EXISTS my_table (
            steam_id VARCHAR(20) PRIMARY KEY,
            points   INT NOT NULL DEFAULT 0
        )"), _db, this);
}

void Unload() => _mysql?.CloseDb(_db);

// Always use parameterized queries — NEVER string concatenation
void AddPoints(string steamId, int pts)
{
    _mysql.ExecuteNonQuery(
        Sql.Builder.Append("INSERT INTO my_table (steam_id,points) VALUES (@0,@1) ON DUPLICATE KEY UPDATE points=points+@1", steamId, pts),
        _db, this);
}

void GetPoints(string steamId, Action<int> cb)
{
    _mysql.Query(
        Sql.Builder.Append("SELECT points FROM my_table WHERE steam_id=@0", steamId),
        _db, rows => cb(rows?.Count > 0 ? Convert.ToInt32(rows[0]["points"]) : 0));
}
```

**MySQL credential injection:** In this codebase credentials come from env vars `MYSQL_HOST`, `MYSQL_PORT`, `MYSQL_USER`, `MYSQL_PASSWORD`, `MYSQL_DATABASE` — read via `System.Environment.GetEnvironmentVariable`. The `start-wrapper.sh` substitutes them into plugin configs at boot. Do not hardcode credentials.

---

## Security Rules

### 1. Parameterized SQL — always
```csharp
// NEVER — SQL injection
_mysql.ExecuteNonQuery(Sql.Builder.Append($"DELETE FROM t WHERE id='{userInput}'"), _db);

// ALWAYS — parameterized
_mysql.ExecuteNonQuery(Sql.Builder.Append("DELETE FROM t WHERE id=@0", userInput), _db);
```

### 2. Shell commands — never interpolate user input
```csharp
// NEVER — shell injection
Process.Start("/bin/bash", $"-c \"script.sh '{userInput}'\"");

// ALWAYS — ArgumentList, no shell
var psi = new ProcessStartInfo("/usr/local/bin/script.sh") { UseShellExecute = false };
psi.ArgumentList.Add(userInput);
Process.Start(psi);
```

### 3. Console permission — guard against plugin-invoked calls
```csharp
// NEVER — allows any other plugin to invoke via ConsoleSystem.Run()
if (a.Connection == null) return true;

// ALWAYS — exclude clientside (plugin-initiated) calls
if (a.Connection == null && !a.IsClientside) return true;
```

### 4. Input validation before file/process operations
```csharp
static bool IsValidPluginName(string n)
{
    if (string.IsNullOrEmpty(n) || n.Length > 64) return false;
    foreach (var c in n)
        if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
    return true;
}
```

---

## CUI / UI Patterns

```csharp
const string PANEL = "MyPlugin_Main";

void OpenUI(BasePlayer player)
{
    CuiHelper.DestroyUi(player, PANEL); // always destroy before rebuild
    var cu = new CuiElementContainer();

    cu.Add(new CuiPanel {
        Image = { Color = "0.1 0.1 0.1 0.95" },
        RectTransform = { AnchorMin = "0.1 0.1", AnchorMax = "0.9 0.9" },
        CursorEnabled = true
    }, "Overlay", PANEL);

    // close button — console command, NOT chat command
    cu.Add(new CuiButton {
        Button = { Color = "0.7 0.1 0.1 1", Command = "myplugin.close" },
        Text   = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter },
        RectTransform = { AnchorMin = "0.95 0.95", AnchorMax = "1 1" }
    }, PANEL);

    CuiHelper.AddUi(player, cu);
}

void CloseUI(BasePlayer player) => CuiHelper.DestroyUi(player, PANEL);

[ConsoleCommand("myplugin.close")]
void CcClose(ConsoleSystem.Arg a)
{
    var p = a.Connection?.player as BasePlayer;
    if (p != null) CloseUI(p);
}
```

---

## This Codebase's Conventions

### Plugin locations

| Type | Location |
|---|---|
| Active custom plugins | `penguin-rust-base/docker/plugins/*.cs` |
| Patched third-party | `penguin-rust-base/docker/plugins/patched/*.cs` |
| Overlay custom plugins | `penguin-rust/docker/plugins/*.cs` |
| Disabled/paid (compressed) | `penguin-rust/docker/plugins/disabled/*.cs.gz` |

### Modifying a `.cs.gz` plugin

```bash
# 1. Decompress
gunzip -c docker/plugins/disabled/MyPlugin.cs.gz > /tmp/MyPlugin.cs

# 2. Edit /tmp/MyPlugin.cs

# 3. Recompress
gzip -c /tmp/MyPlugin.cs > docker/plugins/disabled/MyPlugin.cs.gz

# 4. Update hash (REQUIRED — start.sh validates hashes)
shasum -a 256 /tmp/MyPlugin.cs | awk '{print $1}' | \
  xargs -I{} echo "{}  MyPlugin.cs" > docker/plugins/disabled/myplugin.hash
```

### Adding a patch comment
Every patched file should have a comment at the top documenting what was changed:
```csharp
// PATCHED by penguin-rust-base: <description of each change>
```

### Plugin activation slugs
Slugs are lowercase filenames without extension: `AdminSync.cs` → `adminsync`, `BetterChat.cs` → `betterchat`. Kebab-case for umod/community plugins: `admin-radar`, `back-pump-jack`. Add to `helm/values-beta.yaml` under the correct section.

### MySQL credentials
Read from env vars injected by `start-wrapper.sh`:
```csharp
var host = System.Environment.GetEnvironmentVariable("MYSQL_HOST") ?? "localhost";
```
