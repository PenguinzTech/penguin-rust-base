# penguin-rust-base

[![GHCR](https://img.shields.io/badge/ghcr.io-penguin--rust--base-blue)](https://github.com/PenguinzTech/penguin-rust-base/pkgs/container/penguin-rust-base)

A production-ready Docker image for Rust dedicated game servers with Oxide mod framework and 11 pre-installed plugins. Game files (~6GB) are baked at build time to eliminate first-boot download waits. Automatically rebuilds every 4 hours when Oxide or Steam updates, with daily checks for plugin updates.

Perfect for operators, communities, and teams extending with proprietary plugins via `FROM`.

---

## What's Included

- **Rust Dedicated Server** — Steam app 258550, latest version
- **Oxide Mod Framework** — Auto-updated every 4 hours
- **11 Pre-Installed Plugins** (from umod.org):
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
- **Debian 12 Bookworm Runtime** — Lightweight, secure, production-hardened container

---

## Quick Start

### Basic Server (50 players, 3000-sized map)

```bash
docker run -d \
  --name rust-server \
  -p 28015:28015/udp \
  -p 28015:28015/tcp \
  -p 28016:28016/tcp \
  -e RUST_SERVER_NAME="My Rust Server" \
  -e RUST_SERVER_MAXPLAYERS=50 \
  -e RUST_RCON_PASSWORD="changeme123" \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

### With Admin Access

```bash
docker run -d \
  --name rust-server \
  -p 28015:28015/udp \
  -p 28015:28015/tcp \
  -p 28016:28016/tcp \
  -v rust-data:/steamcmd/rust/server \
  -e RUST_SERVER_NAME="My Rust Server" \
  -e RUST_SERVER_MAXPLAYERS=50 \
  -e RUST_RCON_PASSWORD="secure-password" \
  -e RUST_ADMIN_STEAMIDS="76561198000000000,76561198000000001" \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

### Docker Compose Example

```yaml
version: '3.8'
services:
  rust:
    image: ghcr.io/penguinztechinc/penguin-rust-base:latest
    container_name: rust-server
    ports:
      - "28015:28015/udp"
      - "28015:28015/tcp"
      - "28016:28016/tcp"
    volumes:
      - rust-data:/steamcmd/rust/server
    environment:
      RUST_SERVER_NAME: "My Community Server"
      RUST_SERVER_MAXPLAYERS: 100
      RUST_SERVER_WORLDSIZE: 4000
      RUST_RCON_PASSWORD: "your-secure-password"
      RUST_ADMIN_STEAMIDS: "76561198000000000,76561198000000001"
    restart: unless-stopped

volumes:
  rust-data:
```

---

## Environment Variables

All variables have sensible defaults. Set only what you need to override.

| Variable | Default | Description |
|----------|---------|-------------|
| `RUST_SERVER_NAME` | `Rust Server` | Server name displayed in browser |
| `RUST_SERVER_PORT` | `28015` | Game server UDP port |
| `RUST_SERVER_MAXPLAYERS` | `50` | Maximum concurrent players |
| `RUST_SERVER_WORLDSIZE` | `3000` | Map size in meters (e.g., 2000, 3000, 4000) |
| `RUST_SERVER_SEED` | `12345` | World seed for map generation |
| `RUST_SERVER_SAVE_INTERVAL` | `300` | Save world every N seconds |
| `RUST_SERVER_IDENTITY` | `rust_server` | Server identity (used for save files) |
| `RUST_RCON_PORT` | `28016` | RCON/WebRCON TCP port |
| `RUST_RCON_PASSWORD` | *(none)* | RCON password; RCON disabled if unset |
| `RUST_RCON_WEB` | `1` | Enable WebRCON (`1` = on, `0` = off) |
| `MONO_MAX_HEAP` | `16g` | Mono garbage collector heap limit (e.g., `8g`, `16g`, `32g`) |
| `RUST_CPU_CORES` | *(none)* | CPU cores to pin game loop (e.g., `0,1` or leave empty to disable) |
| `RUST_ADMIN_STEAMIDS` | *(none)* | Comma-separated Steam IDs; grants Rust owner + Oxide permissions |
| `OXIDE_DISABLED_PLUGINS` | *(none)* | Comma-separated plugin names to disable at runtime |
| `RUST_SERVER_EXTRA_ARGS` | *(none)* | Additional arguments passed to RustDedicated |

### Example: 100-Player Server with Custom Map

```bash
docker run -d \
  --name rust-100 \
  -p 28015:28015/udp \
  -p 28015:28015/tcp \
  -p 28016:28016/tcp \
  -v rust-data:/steamcmd/rust/server \
  -e RUST_SERVER_NAME="100-Player Server" \
  -e RUST_SERVER_MAXPLAYERS=100 \
  -e RUST_SERVER_WORLDSIZE=4000 \
  -e RUST_SERVER_SEED=999888 \
  -e RUST_RCON_PASSWORD="MySecurePassword123" \
  -e MONO_MAX_HEAP=24g \
  -e RUST_ADMIN_STEAMIDS="76561198000000000" \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

---

## Admin Provisioning

### RUST_ADMIN_STEAMIDS

Pass comma-separated Steam IDs to automatically grant admin access on every container startup:

```bash
-e RUST_ADMIN_STEAMIDS="76561198000000000,76561198000000001,76561198000000002"
```

**What happens:**
1. Steam ID is added to `users.cfg` as an owner (native Rust server permission)
2. **AutoAdmin.cs plugin** grants all Oxide plugin permissions for that Steam ID (except `vanish.permanent`)
3. On next restart, process repeats — idempotent and safe

**To find your Steam ID:**
- Visit [SteamIDFinder](https://www.steamidfinder.com/)
- Enter your Steam profile URL
- Copy the **64-bit SteamID** (e.g., `76561198000000000`)

---

## Runtime Plugin Toggle

### OXIDE_DISABLED_PLUGINS

Disable any plugin at runtime without rebuilding the image:

```bash
# Disable Vanish and Whitelist
-e OXIDE_DISABLED_PLUGINS="Vanish,Whitelist"

# Disable multiple plugins
-e OXIDE_DISABLED_PLUGINS="AdminUtilities,CopyPaste,NightLantern"
```

**How it works:**
- Oxide scans the `oxide/plugins/` directory on startup
- Disabled plugin files are moved to `oxide/plugins-disabled/` (non-scanning location)
- Files are **not deleted** — they restore from image on restart
- Toggle is purely environment-variable-controlled

**Example: Disable PvE for a PvP wipe**

```bash
# Standard server (Whitelist + TruePVE)
docker run -e "OXIDE_DISABLED_PLUGINS=TruePVE" ...

# Later, switch to PvP
docker restart rust-server  # With updated env var
```

---

## Whitelist Plugin (Closed Server)

The **Whitelist** plugin is pre-installed and enabled by default. By default, the server is **closed** to join.

### Two Ways to Grant Access

#### 1. Admin Bypass (Default)
Admins (from `RUST_ADMIN_STEAMIDS`) bypass the whitelist. Set `Admin Excluded = true` in `oxide/data/Whitelist.json` (default).

#### 2. Grant via Whitelist Permission
Use RCON to grant `whitelist.allow` permission:

```bash
# Connect via RCON (e.g., Rustcord bot, console, or web UI)
oxide.grant user 76561198000000000 whitelist.allow
```

Player can now join. Permission is stored in `oxide/data/Whitelist.json` and persists across restarts.

### Disable Whitelist Entirely

```bash
-e OXIDE_DISABLED_PLUGINS="Whitelist"
```

---

## Oxide Data Persistence

The `oxide/data/` directory contains all plugin data: permissions, whitelisted players, config settings.

### First Boot
- `oxide/data/` is created from image seed (empty, basic defaults)
- Symlinked to persistent storage (if using a volume mount)

### Persist Across Restarts
Mount a volume to preserve plugin data:

```bash
docker run -d \
  -v rust-oxide-data:/steamcmd/rust/server/oxide/data \
  ghcr.io/penguinztechinc/penguin-rust-base:latest
```

### Reseed Data Directory
To reset all plugins settings, whitelist, and permissions:

```bash
docker exec rust-server rm -rf /steamcmd/rust/server/oxide/data
docker restart rust-server
```

Container will recreate `oxide/data/` from image on next start. All plugins reload with defaults.

---

## Image Tags & Versioning

> ⚠️ **IMPORTANT: Tag Strategy & Immutability**

Every build produces **two tags**:
- **`latest`** — Always points to the most recent build (mutable)
- **Unix epoch timestamp** — Immutable, unique per build (e.g., `1747123456`)

### Why Two Tags?

| Tag | Mutability | Use Case |
|-----|-----------|----------|
| `latest` | Mutable | Development, testing — always get the newest Oxide/Steam updates |
| `1747123456` | Immutable | Production, pinning — freeze a known-good build for stability |

### Using the Immutable Tag

```bash
# Pull a specific, known-good build
docker pull ghcr.io/penguinztechinc/penguin-rust-base:1747123456

# Dockerfile
FROM ghcr.io/penguinztechinc/penguin-rust-base:1747123456

# Helm values.yaml
image:
  repository: ghcr.io/penguinztechinc/penguin-rust-base
  tag: "1747123456"
```

### List All Available Tags

Use `skopeo` to list available epoch tags:

```bash
docker run --rm quay.io/skopeo/stable list-tags \
  docker://ghcr.io/penguinztechinc/penguin-rust-base | head -20
```

Or query GitHub Container Registry directly:

```bash
curl -s https://ghcr.io/v2/penguinztechinc/penguin-rust-base/tags/list | jq '.tags'
```

---

## Extending This Image

Add custom plugins or configuration by using this image as a base:

### Example: Custom Plugins

```dockerfile
FROM ghcr.io/penguinztechinc/penguin-rust-base:1747123456

# Copy custom/proprietary plugins
COPY --chown=rustserver:rustserver my-plugins/ /steamcmd/rust/oxide/plugins/

# Copy custom plugin data (config, whitelist, permissions)
COPY --chown=rustserver:rustserver my-data/ /steamcmd/rust/oxide/data/

# Optionally override server name
ENV RUST_SERVER_NAME="My Custom Server"
```

Build and run:

```bash
docker build -t my-rust-server:latest .
docker run -d -p 28015:28015/udp -p 28015:28015/tcp my-rust-server:latest
```

### Accessing Your Extended Image

Store in private registry or GitHub Container Registry:

```bash
docker tag my-rust-server:latest ghcr.io/my-org/my-rust-server:latest
docker push ghcr.io/my-org/my-rust-server:latest
```

---

## Automatic Updates

### Build Automation

This image automatically rebuilds on a schedule:

- **Every 4 hours** — Check for Oxide and Steam updates; rebuild if changes detected
- **Every 24 hours** — Check for umod plugin updates; rebuild if new versions available
- **On dispatch** — Rebuilds trigger downstream private images (if configured)

### Manual Rebuild

Trigger a rebuild immediately:

```bash
gh workflow run build.yml
```

(Requires `gh` CLI and GitHub write access)

---

## Building Locally

Build the image locally with required arguments:

```bash
docker build \
  --build-arg OXIDE_VERSION=2.0.5 \
  --build-arg STEAM_BUILD_ID=3191471 \
  --build-arg UMOD_PLUGINS_HASH=abc123def456 \
  -t penguin-rust-base:local .
```

Required arguments:
- `OXIDE_VERSION` — Oxide release version (e.g., `2.0.5`)
- `STEAM_BUILD_ID` — Steam app 258550 build ID (e.g., `3191471`)
- `UMOD_PLUGINS_HASH` — Hash of combined plugin dependencies (ensures reproducibility)

**Note:** First build downloads ~6GB of game files. Subsequent builds reuse cached layers if Oxide/Steam unchanged.

---

## Security

### Scanned

- **Dockerfile** — hadolint, Checkov security checks
- **start.sh** — Shellcheck syntax and style validation
- **Image CVEs** — Trivy vulnerability scanner (post-build)
- **Secrets** — gitleaks prevents hardcoded credentials

### NOT Scanned

Third-party `.cs` plugin files (from umod.org and lone.design) are **not scanned** — security is the responsibility of plugin authors. Vet plugins before use in production.

### Container Security

- Runs as non-root user (`rustserver:rustserver`)
- Read-only root filesystem where possible
- Capabilities dropped to minimum required
- No hardcoded secrets or credentials

---

## Networking

### Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| `28015` | UDP + TCP | Game server port (connections + queries) |
| `28016` | TCP | RCON / WebRCON port |

### Port Mapping

Forward **both UDP and TCP** on port 28015:

```bash
-p 28015:28015/udp -p 28015:28015/tcp
```

**RCON is optional.** If `RUST_RCON_PASSWORD` is not set, RCON is disabled.

---

## Troubleshooting

### Server Won't Start

1. **Check logs:**
   ```bash
   docker logs rust-server | tail -50
   ```

2. **Common issues:**
   - **Port already in use** — Check if another service is on 28015/28016
   - **Insufficient memory** — Increase Docker memory limit or reduce `MONO_MAX_HEAP`
   - **Disk space** — Game files require ~6GB; ensure volume has free space

### Players Can't Join

1. **Check firewall** — Port 28015 UDP/TCP must be open to the internet
2. **Check server port** — Verify `RUST_SERVER_PORT` matches firewall rule (default 28015)
3. **Check Whitelist** — If Whitelist plugin enabled, player must be granted access or be admin
4. **Check logs for errors:**
   ```bash
   docker exec rust-server grep -i "error\|fail" /var/log/rust/server.log
   ```

### Oxide Plugins Not Loading

1. **Check plugin directory:**
   ```bash
   docker exec rust-server ls /steamcmd/rust/oxide/plugins/
   ```

2. **Check Oxide logs:**
   ```bash
   docker exec rust-server tail -50 /steamcmd/rust/server/oxide/logs/log.txt
   ```

3. **Verify plugin file permissions:**
   ```bash
   docker exec rust-server ls -la /steamcmd/rust/oxide/plugins/
   ```

4. **Check if plugin is disabled:**
   ```bash
   echo $OXIDE_DISABLED_PLUGINS
   ```

---

## Performance Tuning

### CPU Pinning

Pin the game loop to specific cores for predictable performance:

```bash
-e RUST_CPU_CORES="0,1,2,3"
```

Use even-numbered cores on high-core-count systems (NUMA awareness).

### Memory Tuning

Adjust Mono GC heap limit based on player count:

| Players | MONO_MAX_HEAP | Notes |
|---------|---------------|-------|
| ≤50 | `8g` | Minimum for 50-player |
| 50–100 | `16g` | Default; good for typical servers |
| 100–200 | `24g` | Large community servers |
| >200 | `32g+` | Requires high-spec hardware |

```bash
-e MONO_MAX_HEAP=24g
```

### Save Interval

Increase `RUST_SERVER_SAVE_INTERVAL` for better performance if disk I/O is a bottleneck:

```bash
# Save less frequently (default is 300s / 5min)
-e RUST_SERVER_SAVE_INTERVAL=600
```

---

## Contributing

Issues, feature requests, and pull requests welcome on [GitHub](https://github.com/PenguinzTech/penguin-rust-base).

- **Bug reports** — Include Docker version, container logs, and reproduction steps
- **Plugin requests** — Propose umod.org plugins; must be stable and widely-used
- **Documentation** — Help improve this README

---

## License

Container image: MIT License

Included plugins: Licensed under their respective authors' licenses (see umod.org for details)

Rust and Steam: Licensed by Facepunch Studios. By using this image, you agree to the Rust Server License (see `https://www.rust.facepunch.com/`)

---

## Support

- **Documentation** — See this README
- **Rust Server Issues** — [Rust Support](https://support.facepunch.com/)
- **Oxide Framework** — [Oxide Docs](https://umod.org/documentation)
- **Plugin Help** — [umod Community](https://umod.org/community)
- **Image Issues** — [GitHub Issues](https://github.com/PenguinzTech/penguin-rust-base/issues)

---

**Built by PenguinzTech** — Reliable, production-ready Rust infrastructure.
