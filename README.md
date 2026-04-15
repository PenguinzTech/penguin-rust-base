# penguin-rust-base

[![GHCR](https://img.shields.io/badge/ghcr.io-penguin--rust--base-blue)](https://github.com/PenguinzTech/penguin-rust-base/pkgs/container/penguin-rust-base)
[![Tests](https://github.com/PenguinzTech/penguin-rust-base/actions/workflows/test.yml/badge.svg)](https://github.com/PenguinzTech/penguin-rust-base/actions/workflows/test.yml)
[![Security](https://github.com/PenguinzTech/penguin-rust-base/actions/workflows/security.yml/badge.svg)](https://github.com/PenguinzTech/penguin-rust-base/actions/workflows/security.yml)
[![Build](https://github.com/PenguinzTech/penguin-rust-base/actions/workflows/build-image.yml/badge.svg)](https://github.com/PenguinzTech/penguin-rust-base/actions/workflows/build-image.yml)

A production-ready Docker image for Rust dedicated game servers with Oxide mod framework and plugins baked in. Game files (~6GB) are baked at build time to eliminate first-boot download waits. Automatically rebuilds every 4 hours when Oxide or Steam updates, with startup-time checks for plugin updates.

Perfect for operators, communities, and teams extending with proprietary plugins via `FROM`.

📖 **Full configuration reference:** [docs/CONFIGURATION.md](docs/CONFIGURATION.md)

---

## What's Included

- **Rust Dedicated Server** — Steam app 258550, latest version
- **Oxide Mod Framework** — Auto-updated every 4 hours
- **Pre-Installed Plugins** — all plugins published to [penguin-rust-plugins](https://github.com/PenguinzTech/penguin-rust-plugins) are automatically baked in on every image build (no static list to maintain):
  - **AdminUtilities** — Admin commands (noclip, god mode, kick, ban, give, spawn)
  - **BGrade** — Automatically upgrade building grades
  - **CopyPaste** — Copy and paste buildings
  - **Vanish** — Admin invisibility toggle
  - **RemoverTool** — Remove placed objects
  - **UnburnableMeat** — Prevents cooked meat from burning
  - **VehicleDecayProtection** — Per-vehicle decay protection permissions
  - **NightLantern** — Auto-lights fires/lanterns at night
  - **TruePVE** — PvE protection rules
  - **StackSizeController** — Customize item stack sizes
  - **Whitelist** — Restrict server access to whitelisted players
- **AutoAdmin Plugin** — Custom PenguinzTech plugin that auto-provisions Oxide permissions from environment variables
- **PluginManager Plugin** — Runtime `/plugin add|remove|update|list` commands; manage plugins live without a restart (see [docs/plugin-manager.md](docs/plugin-manager.md))
- **Patched Community Plugins** — 10 popular community plugins pre-patched for Oxide API compatibility (removed APIs replaced so they work on current Rust builds): AntiOfflineRaid, BetterChat, BetterChatMute, DynamicPVP, NTeleportation, PlayerAdministration, Quests, TreePlanter, VehicleLicence, ZoneManager — see [docs/ACKNOWLEDGEMENTS.md](docs/ACKNOWLEDGEMENTS.md)
- **WAF Sidecar** — Go-based network-layer firewall that protects the game server from DDoS floods, cheater reconnect storms, RCON brute-force, and packet anomalies — **works in pure vanilla mode** with no Oxide required (see [docs/waf.md](docs/waf.md))
- **Auto-Configuration** — First-boot tuning of `worldSize`/`maxPlayers` based on available CPU/RAM
- **Wipe Scheduler** — Configurable map wipes with in-game RCON warnings (60-minute lead time)
- **DDoS Protection** — Per-source-IP rate limiting via iptables (opt-in, requires `NET_ADMIN`)
- **Debian 12 Bookworm Runtime** — Lightweight, secure, production-hardened container

---

## Quick Start

### Basic Server (auto-tuned world size and player limit)

```bash
docker run -d \
  --name rust-server \
  -p 28015:28015/udp \
  -p 28015:28015/tcp \
  -p 28016:28016/tcp \
  -e RUST_SERVER_NAME="My Rust Server" \
  ghcr.io/penguinztech/penguin-rust-base:latest
```

### With Admin Access + Persistent Volume

```bash
docker run -d \
  --name rust-server \
  -p 28015:28015/udp \
  -p 28015:28015/tcp \
  -p 28016:28016/tcp \
  -v rust-data:/steamcmd/rust/server \
  -e RUST_SERVER_NAME="My Rust Server" \
  -e RUST_ADMIN_STEAMIDS="76561198000000000,76561198000000001" \
  ghcr.io/penguinztech/penguin-rust-base:latest
```

### Docker Compose

```yaml
services:
  rust:
    image: ghcr.io/penguinztech/penguin-rust-base:latest
    container_name: rust-server
    ports:
      - "28015:28015/udp"
      - "28015:28015/tcp"
      - "28016:28016/tcp"
    volumes:
      - rust-data:/steamcmd/rust/server
    environment:
      RUST_SERVER_NAME: "My Community Server"
      RUST_SERVER_DESCRIPTION: "Weekly wipes, friendly community"
      RUST_SERVER_TAGS: "weekly,vanilla,pvp"
      WIPE_SCHED: "1w"
      WIPE_DAY: "Th"
      RUST_ADMIN_STEAMIDS: "76561198000000000"
    restart: unless-stopped

volumes:
  rust-data:
```

---

## Configuration

All settings are controlled via environment variables. The most commonly set ones:

| Variable | Default | Description |
|---|---|---|
| `RUST_SERVER_NAME` | `Rust Server` | Server name in browser |
| `RUST_SERVER_MAXPLAYERS` | *(auto-detected)* | Max concurrent players |
| `RUST_SERVER_WORLDSIZE` | *(auto-detected)* | Map size in meters |
| `RUST_SERVER_SEED` | `12345` | World seed |
| `RUST_RCON_PASSWORD` | *(auto-generated)* | RCON password; generated and persisted to PVC if unset |
| `RUST_ADMIN_STEAMIDS` | *(none)* | Comma-separated admin Steam IDs |
| `WIPE_SCHED` | *(first Thu of month)* | `1w`, `2w`, `3w`, or `off` |
| `PLUGIN_SOURCE` | `github` | `github` (baked→GitHub→umod chain), `baked` (no network), or `umod` (always umod.org) |
| `PLUGIN_UMOD_FALLBACK` | `1` | In `github` mode, fall back to umod.org for slugs not in penguin-rust-plugins (`0` to disable) |

📖 **Everything else** — browser listing (description, tags, URL, logo), server behaviour (PvE, radiation, tickrate), wipe schedule details, DDoS protection, admin provisioning, plugin toggles, auto-config tiers, performance tuning — is documented in **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)**.

Specialist guides:
- **[docs/auto-config.md](docs/auto-config.md)** — first-boot resource detection, lock file behaviour
- **[docs/wipe-schedule.md](docs/wipe-schedule.md)** — wipe cadence, blueprint wipes, warning schedule
- **[docs/ddos-protection.md](docs/ddos-protection.md)** — iptables rate limiting and auto-ban
- **[docs/waf.md](docs/waf.md)** — Go WAF sidecar: vanilla-compatible DDoS/flood/cheat protection, Oxide integration, Prometheus metrics

---

## Image Tags & Versioning

Every build produces two tags:

| Tag | Mutability | Use Case |
|-----|-----------|----------|
| `latest` | Mutable | Development, testing — always get the newest Oxide/Steam updates |
| `<unix-epoch>` | Immutable | Production, pinning — freeze a known-good build |

```bash
# Pin to a specific build in production
FROM ghcr.io/penguinztech/penguin-rust-base:1747123456
```

List available tags:
```bash
curl -s https://ghcr.io/v2/penguinztech/penguin-rust-base/tags/list | jq '.tags'
```

---

## Extending This Image

```dockerfile
FROM ghcr.io/penguinztech/penguin-rust-base:1747123456

# Custom/proprietary plugins
COPY --chown=rustserver:rustserver my-plugins/ /steamcmd/rust/oxide/plugins/

# Pre-seeded plugin data
COPY --chown=rustserver:rustserver my-data/ /steamcmd/rust/oxide/data/

# Default overrides
ENV RUST_SERVER_NAME="My Custom Server"
```

---

## Plugin Caching

Plugins are baked into the image as gzip-compressed `.cs.gz` files alongside a `.hash` sidecar. This serves two purposes:

**Startup speed** — On every boot, `start.sh` compares the baked hash against the latest release in `penguin-rust-plugins`. If they match, the plugin is decompressed and activated in milliseconds from the local layer — no network round-trip. Only plugins that have been updated since the image was built are downloaded at startup.

**Efficient layer sharing for multi-server providers** — All plugin `.cs.gz` files live in a single immutable Docker layer. Hosts running many Rust server containers (or different server images built `FROM` this base) share that layer on disk and in the registry pull cache. Plugins are only decompressed into the container's writable layer when activated, so the compressed originals remain shared.

```
oxide/plugins/disabled/     ← shared, compressed, in image layer
    truepve.cs.gz
    truepve.hash
    whitelist.cs.gz
    whitelist.hash
    ...

oxide/plugins/              ← per-container writable layer, uncompressed
    TruePVE.cs              ← only after RUST_PLUGINS=truepve
    Whitelist.cs
```

Plugins are disabled by default. Set `RUST_PLUGINS` to activate specific ones:

```bash
-e RUST_PLUGINS="truepve,whitelist,vanish"
```

---

## Automatic Updates

- **Every 4 hours** — check Oxide and Steam for updates, rebuild if changed
- **On dispatch** — manual `gh workflow run build.yml`
- **On startup** — compare baked plugin hashes against latest `penguin-rust-plugins` releases; download updates only when needed

---

## WAF Sidecar (Network-Layer Protection)

The image ships a **Go WAF sidecar** that operates at the network layer — below the Rust game engine. It intercepts all traffic before it reaches the single-threaded C#/Mono game loop and drops malicious packets in Go's concurrent runtime where they are cheap.

**Crucially, this works on pure vanilla servers.** No Oxide, no plugins, no mods required. Every protection below runs at the packet level regardless of server configuration:

| Protection | How it works |
|---|---|
| **DDoS / flood** | Per-IP packet-rate limiter; sustained floods auto-blocked |
| **RCON brute-force** | Failed auth counter; offending IP throttled after N attempts |
| **Ban evasion** | Steam64 ID extracted from handshake; banned player blocked across IP changes |
| **Packet anomalies** | Malformed/oversized packets dropped before game sees them |
| **Aimbot heuristics** | Inter-packet timing CV analysis; bot-like consistency flagged |

When Oxide *is* running, plugins can push runtime rules to the WAF via a loopback REST API — reporting detected cheaters for immediate network-layer enforcement without a game restart.

Enable in Kubernetes (Helm):

```bash
helm install rust-server ./k8s/helm/rust-server --set waf.enabled=true
```

📖 **Full WAF reference:** [docs/waf.md](docs/waf.md)

---

## Networking

| Port | Protocol | Purpose |
|------|----------|---------|
| `28015` | UDP + TCP | Game server port (connections + queries) |
| `28016` | TCP | RCON / WebRCON |
| `28017` | UDP | Server browser query |

Forward **both UDP and TCP** on port 28015. RCON and query ports are optional.

> **WAF port note:** When the WAF sidecar is enabled, it occupies the public-facing ports (`28015/28016/28017`) and shifts the game server to loopback offsets (`28115/28116/28117`). No change to external port mappings required.

---

## Security

Runs as non-root (`rustserver:rustserver`, UID 1000); no capabilities required unless DDoS protection is enabled. RCON password auto-generated on first boot and persisted to the PVC — never embedded. The WAF sidecar also runs as a dedicated non-root user (`waf:waf`) and adds zero inbound attack surface — its management API listens on loopback only.

Full details — CI scanners, what's not scanned, reporting vulnerabilities: **[docs/SECURITY.md](docs/SECURITY.md)**.  
DDoS protection setup: **[docs/ddos-protection.md](docs/ddos-protection.md)**.  
WAF sidecar: **[docs/waf.md](docs/waf.md)**.

---

## Troubleshooting

```bash
# Check logs
docker logs rust-server | tail -50

# Server won't start — typical causes:
# - Port conflict on 28015/28016
# - Memory limit below MONO_MAX_HEAP
# - Volume permissions (must be writable by UID 1000)

# Retrieve auto-generated RCON password
docker exec rust-server cat /steamcmd/rust/server/rust_server/.rcon.pw

# Plugin status
docker exec rust-server ls /steamcmd/rust/oxide/plugins/
docker exec rust-server tail -50 /steamcmd/rust/server/oxide/logs/log.txt
```

Full troubleshooting: [docs/CONFIGURATION.md](docs/CONFIGURATION.md)

---

## Contributing

Issues, feature requests, and pull requests welcome on [GitHub](https://github.com/PenguinzTech/penguin-rust-base).

---

## License

- **Container image:** MIT License
- **Bundled plugins:** Their respective authors' licenses (see [umod.org](https://umod.org))
- **Rust / Steam:** Licensed by Facepunch Studios — by using this image you agree to the [Rust Server License](https://www.rust.facepunch.com/)

---

## Support

- [GitHub Issues](https://github.com/PenguinzTech/penguin-rust-base/issues) — image-specific issues
- [Rust Support](https://support.facepunch.com/) — game server issues
- [Oxide Docs](https://umod.org/documentation) — mod framework
- [umod Community](https://umod.org/community) — plugin help

---

**Built by PenguinzTech** — Reliable, production-ready Rust infrastructure.
