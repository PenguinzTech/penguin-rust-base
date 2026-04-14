# Configuration Reference

Complete reference for all environment variables, auto-detection behaviour, wipe scheduling, performance tuning, and plugin management.

---

## Environment Variables

All variables have sensible defaults. Set only what you need to override.

### Server identity / world

| Variable | Default | Description |
|---|---|---|
| `RUST_SERVER_NAME` | `Rust Server` | Server name displayed in browser |
| `RUST_SERVER_PORT` | `28015` | Game server UDP port |
| `RUST_SERVER_MAXPLAYERS` | *(auto-detected)* | Maximum concurrent players |
| `RUST_SERVER_WORLDSIZE` | *(auto-detected)* | Map size in meters |
| `RUST_SERVER_SEED` | `12345` | World seed for map generation |
| `RUST_SERVER_LEVEL` | `Procedural Map` | Map type: `Procedural Map`, `Barren`, `HapisIsland`, etc. |
| `RUST_SERVER_SAVE_INTERVAL` | `300` | Save world every N seconds |
| `RUST_SERVER_IDENTITY` | `rust_server` | Server identity directory name (used for save files) |

### Server browser listing

| Variable | Default | Description |
|---|---|---|
| `RUST_SERVER_DESCRIPTION` | *(none)* | Description shown in server browser |
| `RUST_SERVER_TAGS` | *(none)* | Comma-separated browser filter tags (e.g. `monthly,vanilla,us`) |
| `RUST_SERVER_URL` | *(none)* | Server website URL |
| `RUST_SERVER_HEADERIMAGE` | *(none)* | Header image URL shown in browser |
| `RUST_SERVER_LOGO` | *(none)* | Server logo URL |

### Server behaviour

| Variable | Default | Description |
|---|---|---|
| `RUST_SERVER_PVE` | *(game default)* | PvE mode: `0` = PvP, `1` = PvE |
| `RUST_SERVER_RADIATION` | *(game default)* | Radiation enabled: `0` or `1` |
| `RUST_SERVER_TICKRATE` | *(game default)* | Server tick rate (game default: 30) |
| `RUST_SERVER_FPS` | *(game default)* | Server FPS cap (game default: 30) |

Behaviour variables are only passed to `RustDedicated` when set. Leaving them blank lets the game use its own defaults.

### Mod framework & PvP mode

| Variable | Default | Description |
|---|---|---|
| `OXIDE` | `1` | `1` = Oxide + umod plugins loaded; `0` = vanilla RustDedicated restored from baked snapshot, oxide/ removed, **all plugin config/data skipped** |
| `PVP_MODE` | `1` | Applies only when `OXIDE=1`. `1` = PvP globally (TruePVE `defaultAllowDamage=true`); `0` = PvE globally (TruePVE `defaultAllowDamage=false`). TruePVE stays loaded in both modes — admins can still define zones/exceptions |

**How they interact:**

| `OXIDE` | `PVP_MODE` | Result |
|---|---|---|
| `1` (default) | `1` (default) | Oxide + all umod plugins; TruePVE loaded, `defaultAllowDamage=true` → global PvP, admins can carve out PvE zones via TruePVE |
| `1` | `0` | Oxide loaded; TruePVE loaded, `defaultAllowDamage=false` → global PvE, admins can carve out PvP zones via TruePVE |
| `0` | *ignored* | Vanilla RustDedicated, no Oxide, full PvP. `PVP_MODE` has no effect — set `RUST_SERVER_PVE=1` explicitly if you want vanilla PvE |

**How PVP_MODE is applied:** on every startup (when `OXIDE=1`), the image writes a minimal `oxide/config/TruePVE.json` with the `defaultAllowDamage` field set from `PVP_MODE`. TruePVE merges this with its own defaults on load. The `oxide/config/` directory is seeded fresh from the image layer each boot (only `oxide/data/` is persisted on PVC), so the boot-time toggle always wins — zone definitions and exceptions should be managed via TruePVE's in-game/RCON commands (which persist to `oxide/data/`) rather than edits to the config file.

Switching `OXIDE=0` restores the vanilla `Managed/` directory from the `Managed.vanilla/` snapshot baked at image build time — no Steam round-trip, no re-download. The startup script skips all plugin config writes, all Oxide data persistence, and all plugin-toggle logic in that mode — you get a pure Facepunch binary.

### RCON / admin

| Variable | Default | Description |
|---|---|---|
| `RUST_RCON_PORT` | `28016` | RCON/WebRCON TCP port |
| `RUST_RCON_PASSWORD` | *(auto-generated)* | RCON password; auto-generated and persisted to PVC if unset |
| `RUST_RCON_WEB` | `1` | WebRCON enabled (`1` = on, `0` = off) |
| `RUST_ADMIN_STEAMIDS` | *(none)* | Comma-separated Steam IDs; grants Rust owner + Oxide permissions |

### Wipe schedule

| Variable | Default | Description |
|---|---|---|
| `WIPE_SCHED` | *(forced-only)* | Wipe interval: `1w`, `2w`, `3w`, or `off` |
| `WIPE_DAY` | `Th` | Day of week: `M` `Tu` `W` `Th` `F` `Sa` `Su` |
| `WIPE_TIME` | `06:00` | UTC time the wipe actually happens (HH:MM); RCON warnings start 60 minutes earlier |
| `WIPE_BP` | `false` | Also wipe blueprints (`true`/`false`) |

### DDoS protection (opt-in)

| Variable | Default | Description |
|---|---|---|
| `DDOS_PROTECT` | `0` | `1` to enable iptables per-source-IP UDP rate limiting; `0` to skip |
| `DDOS_UDP_RATE` | `60` | Sustained packets/sec per source IP when protection is on |
| `DDOS_UDP_BURST` | `120` | Burst packets allowed before rate-limit kicks in |

Enabling `DDOS_PROTECT=1` requires `NET_ADMIN` on the container (`--cap-add=NET_ADMIN` or K8s `securityContext.capabilities.add: ["NET_ADMIN"]`). If the capability is missing, startup logs a warning and continues without protection — it never crashes. See **[ddos-protection.md](ddos-protection.md)** for tuning and K8s-layer alternatives.

### Runtime / tuning

| Variable | Default | Description |
|---|---|---|
| `MONO_MAX_HEAP` | `16g` | Mono GC heap limit (e.g. `8g`, `16g`, `32g`) |
| `RUST_CPU_CORES` | *(none)* | CPU cores for game-loop pinning (e.g. `0,1`); leave blank to skip |
| `RUST_MAXPLAYERS_CHECK_INTERVAL` | `1800` | Seconds between RSS-based maxPlayers adjustments; `0` to disable |
| `OXIDE_DISABLED_PLUGINS` | *(none)* | Comma-separated plugin names to disable at runtime |
| `RUST_SERVER_EXTRA_ARGS` | *(none)* | Additional arguments passed verbatim to `RustDedicated` |

---

## Auto-Configuration

If `RUST_SERVER_WORLDSIZE` and `RUST_SERVER_MAXPLAYERS` are not set, the server
automatically selects values on **first deployment** based on available CPUs and memory,
then locks the result so restarts never change a live world.

### Tier table

| Memory   | CPUs | worldSize | maxPlayers |
|----------|------|-----------|------------|
| < 4 GB   | ≥1   | 750       | 10         |
| 4–7 GB   | ≥1   | 2000      | 40         |
| 8–15 GB  | ≥2   | 3000      | 75         |
| 16–31 GB | ≥4   | 4000      | 100        |
| 32+ GB   | ≥4   | 4500      | 150        |

> **Plugin overhead note:** These tiers assume a vanilla Oxide + umod stack. Heavy plugins
> (XDQuest, Kits, WaterBases) add meaningful memory overhead — consider stepping down one
> tier or increasing your memory allocation on plugin-heavy servers.

### Lock file

Detection runs once. The result is written to:

```
/steamcmd/rust/server/<identity>/.auto-config.lock
```

On subsequent restarts the lock file is found and detection is skipped entirely.

**To re-trigger detection** (e.g. after a hardware upgrade):

```bash
# Remove the lock file — next restart re-detects
docker exec rust-server rm /steamcmd/rust/server/rust_server/.auto-config.lock
docker restart rust-server
```

> **Warning:** Re-triggering detection after a world has been generated will change
> `worldSize` if the detected tier differs from the locked value. A different `worldSize`
> creates a new map — all world progress is lost. Only re-trigger on a fresh deployment or
> after a planned wipe.

### Explicit overrides always win

If `RUST_SERVER_WORLDSIZE` or `RUST_SERVER_MAXPLAYERS` are set explicitly, auto-detection
is skipped for those variables. You can override one and auto-detect the other:

```bash
-e RUST_SERVER_WORLDSIZE=3000   # Explicit; maxPlayers still auto-detected
```

### Phase 2b: runtime maxPlayers adjustment

After world generation completes, a background loop reads the server's actual RSS (resident
memory) every 30 minutes and adjusts `server.maxplayers` via RCON. This is more accurate
than boot-time detection because it captures navmesh, plugin heap, and entity data.

Set `RUST_MAXPLAYERS_CHECK_INTERVAL=0` to disable this loop.

📖 Full details: [auto-config.md](auto-config.md)

---

## Wipe Schedule

The built-in wipe watcher polls every 30 seconds and deletes server save data on schedule.
After deletion it stops the server; the container restart policy brings it back with a fresh map.

### Default behaviour

Without `WIPE_SCHED`, the watcher fires on the **first Thursday of every month** —
aligned with Facepunch forced-wipe day. This is the standard community wipe cadence.

### Cadence examples

```bash
# Default — first Thursday of month only (forced-wipe aligned)
# WIPE_SCHED not set

# Weekly Thursday (common for active servers)
WIPE_SCHED=1w  WIPE_DAY=Th

# Bi-weekly Thursday
WIPE_SCHED=2w  WIPE_DAY=Th

# Bi-weekly Saturday
WIPE_SCHED=2w  WIPE_DAY=Sa  WIPE_TIME=10:00

# Every third Saturday
WIPE_SCHED=3w  WIPE_DAY=Sa

# Disable all automated wipes
WIPE_SCHED=off
```

### Blueprint wipes

By default only map data is wiped (players keep blueprints). Set `WIPE_BP=true` to also
delete `player.blueprints.*.db`.

**First-Thursday wipes always wipe blueprints** regardless of `WIPE_BP`, because Facepunch
forced wipes typically require a full reset.

### Files deleted

| File pattern | What it is | Always deleted |
|---|---|---|
| `proceduralmap.*.map` | Map geometry | ✓ |
| `proceduralmap.*.sav` | World save (entities, loot, player positions) | ✓ |
| `proceduralmap.*.db` | Map SQLite database | ✓ |
| `player.blueprints.*.db` | Blueprint progress | Only when `WIPE_BP=true` or forced-wipe day |

Player identity and death data is never deleted automatically.

📖 Full details: [wipe-schedule.md](wipe-schedule.md)

---

## Admin Provisioning

### RUST_ADMIN_STEAMIDS

Pass comma-separated Steam IDs to automatically grant admin access on every container startup:

```bash
-e RUST_ADMIN_STEAMIDS="76561198000000000,76561198000000001"
```

**What happens on each boot:**
1. Steam IDs are written to `server/<identity>/cfg/users.cfg` as `ownerid` entries
2. **AutoAdmin.cs** grants all Oxide plugin permissions for each ID (except `vanish.permanent`)
3. Removed IDs are automatically revoked (the file is rewritten clean on each boot)

**Finding your Steam ID:**
- Visit [steamid.io](https://steamid.io) or [SteamIDFinder](https://www.steamidfinder.com/)
- Copy the **64-bit SteamID** (starts with `765611...`)

### RCON password

If `RUST_RCON_PASSWORD` is not set, the startup script generates a 31-character alphanumeric
password on first boot and persists it to:

```
/steamcmd/rust/server/<identity>/.rcon.pw
```

The password is read from this file on subsequent restarts. To retrieve it:

```bash
docker exec rust-server cat /steamcmd/rust/server/rust_server/.rcon.pw
```

To rotate the password, delete the file and set a new `RUST_RCON_PASSWORD`, or delete the
file and let the script generate a new one.

---

## Runtime Plugin Toggle

### OXIDE_DISABLED_PLUGINS

Disable any plugin at runtime without rebuilding the image:

```bash
# Disable single plugin
-e OXIDE_DISABLED_PLUGINS="Whitelist"

# Disable multiple
-e OXIDE_DISABLED_PLUGINS="Vanish,AdminUtilities,NightLantern"
```

**How it works:**
- Disabled plugin files are moved to `oxide/plugins/disabled/` before Oxide loads
- Oxide only scans the `oxide/plugins/` root — `disabled/` is ignored
- Files restore from the image layer on next restart
- The toggle is idempotent and purely env-var-controlled

### Whitelist plugin

The **Whitelist** plugin is pre-installed and active by default, making the server
closed to non-admins out of the box.

**Grant access via RCON:**
```bash
oxide.grant user 76561198000000000 whitelist.allow
```

**Open the server (disable whitelist):**
```bash
-e OXIDE_DISABLED_PLUGINS="Whitelist"
```

Admins (from `RUST_ADMIN_STEAMIDS`) bypass the whitelist by default via the
`Admin Excluded = true` setting in `oxide/data/Whitelist.json`.

---

## Oxide Data Persistence

The `oxide/data/` directory contains all plugin data: permissions, whitelisted players,
and per-plugin config files.

### First boot

On first boot the directory is copied from the image to the PVC mount, then symlinked.
Any default data COPYed into the image (e.g. pre-seeded `Whitelist.json`) is present from day one.

### Persisting across restarts

Mount a volume to `/steamcmd/rust/server` (which contains `oxide-data/` as a subdirectory):

```bash
docker run -d \
  -v rust-data:/steamcmd/rust/server \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

### Resetting plugin data

```bash
# Delete the PVC copy — next boot reseeds from image
docker exec rust-server rm -rf /steamcmd/rust/server/oxide-data
docker restart rust-server
```

---

## Performance Tuning

### CPU pinning (two-phase)

Rust uses two distinct CPU profiles:

- **World generation** — multi-threaded (navmesh, terrain, occlusion grid). Benefits from
  all available cores. Restricting CPUs here wastes significant startup time.
- **Game loop** — almost entirely single-threaded. Pinning to dedicated cores eliminates
  scheduler jitter and reduces player desync on busy servers.

The startup script handles this automatically: world gen runs with no CPU restriction.
After the server opens UDP port 28015, `taskset` pins `RustDedicated` to the cores in
`RUST_CPU_CORES`.

**Recommendation:** provision your container with the full core budget at deploy time,
then narrow the game loop to dedicated cores after startup:

```yaml
# Kubernetes
resources:
  requests:
    cpu: "4"
  limits:
    cpu: "4"
env:
  - name: RUST_CPU_CORES
    value: "0,1"
```

```bash
# Docker
docker run --cpus="4" -e RUST_CPU_CORES="0,1" ...
```

Leave `RUST_CPU_CORES` empty to let the scheduler assign cores freely (fine for most setups).
On multi-socket NUMA systems, use cores from the same NUMA node to avoid cross-socket memory latency.

### Memory tuning (Mono GC)

| Players | `MONO_MAX_HEAP` | Notes |
|---------|-----------------|-------|
| ≤50     | `8g`            | Minimum for a 50-player server |
| 50–100  | `16g`           | Default; good for typical servers |
| 100–150 | `24g`           | Large community servers |
| >150    | `32g+`          | Requires high-spec hardware |

```bash
-e MONO_MAX_HEAP=24g
```

Setting this too low causes GC thrash; too high risks OOMKill at the container memory limit.
Leave at least 2–4 GB headroom between `MONO_MAX_HEAP` and your container memory limit.

### Save interval

Increase `RUST_SERVER_SAVE_INTERVAL` if disk I/O is a bottleneck:

```bash
# Save every 10 minutes instead of 5 (default 300s)
-e RUST_SERVER_SAVE_INTERVAL=600
```

---

## Advanced Examples

### Weekly-wipe competitive server

```bash
docker run -d \
  --name rust-weekly \
  -p 28015:28015/udp -p 28015:28015/tcp -p 28016:28016/tcp \
  -v rust-weekly:/steamcmd/rust/server \
  -e RUST_SERVER_NAME="Weekly PvP | No BP Wipe | 3000 Map" \
  -e RUST_SERVER_DESCRIPTION="Hardcore weekly server. Wiped every Thursday at 6 AM UTC." \
  -e RUST_SERVER_TAGS="weekly,vanilla,pvp,us" \
  -e RUST_SERVER_WORLDSIZE=3000 \
  -e RUST_SERVER_MAXPLAYERS=75 \
  -e RUST_SERVER_SEED=42000 \
  -e WIPE_SCHED=1w \
  -e WIPE_DAY=Th \
  -e WIPE_TIME=06:00 \
  -e MONO_MAX_HEAP=16g \
  -e RUST_ADMIN_STEAMIDS="76561198000000000" \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

### Monthly server with full wipes

```bash
docker run -d \
  --name rust-monthly \
  -p 28015:28015/udp -p 28015:28015/tcp -p 28016:28016/tcp \
  -v rust-monthly:/steamcmd/rust/server \
  -e RUST_SERVER_NAME="Monthly | 4000 Map | Full Wipe" \
  -e RUST_SERVER_TAGS="monthly,vanilla,pvp,eu" \
  -e RUST_SERVER_WORLDSIZE=4000 \
  -e RUST_SERVER_MAXPLAYERS=100 \
  -e WIPE_BP=true \
  -e MONO_MAX_HEAP=24g \
  -e RUST_CPU_CORES="0,1,2,3" \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

### PvE server (no whitelist)

```bash
docker run -d \
  --name rust-pve \
  -p 28015:28015/udp -p 28015:28015/tcp -p 28016:28016/tcp \
  -v rust-pve:/steamcmd/rust/server \
  -e RUST_SERVER_NAME="Friendly PvE Server" \
  -e RUST_SERVER_TAGS="pve,friendly,noob-friendly" \
  -e RUST_SERVER_PVE=1 \
  -e OXIDE_DISABLED_PLUGINS="Whitelist" \
  -e RUST_SERVER_MAXPLAYERS=50 \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

### Kubernetes Helm values snippet

```yaml
env:
  # Server identity
  - name: RUST_SERVER_NAME
    value: "My K8s Server"
  - name: RUST_SERVER_DESCRIPTION
    value: "Hosted on Kubernetes with automatic updates."
  - name: RUST_SERVER_TAGS
    value: "monthly,vanilla,pvp"
  - name: RUST_SERVER_WORLDSIZE
    value: "3000"
  - name: RUST_SERVER_MAXPLAYERS
    value: "75"
  # RCON
  - name: RUST_RCON_PASSWORD
    valueFrom:
      secretKeyRef:
        name: rust-secrets
        key: rcon-password
  # CPU pinning — 4 cores for world gen, pin game loop to 0,1
  - name: RUST_CPU_CORES
    value: "0,1"
  # Wipe schedule — bi-weekly Thursday
  - name: WIPE_SCHED
    value: "2w"
  - name: WIPE_DAY
    value: "Th"
  - name: WIPE_TIME
    value: "06:00"

resources:
  requests:
    cpu: "4"
    memory: "20Gi"
  limits:
    cpu: "4"
    memory: "20Gi"
```
