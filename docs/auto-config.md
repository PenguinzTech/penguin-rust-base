# Automatic Resource-Based Server Configuration

## Overview

On first deployment, the `start.sh` script automatically detects available CPUs and memory, then selects appropriate `worldSize` and `maxPlayers` values from a predefined tier table. This configuration is written to a lock file on the persistent volume. On subsequent restarts, the lock file is detected and the detection block is skipped entirely — preventing worldSize changes that would corrupt an existing world.

This ensures:
- **First-time ease:** No manual configuration needed for basic deployments
- **World stability:** Once a world is created, configuration remains locked
- **Predictable scaling:** New deployments automatically right-size for available resources

---

## Tier Table

The following table maps hardware resources to Rust server configuration:

| Memory   | CPUs | worldSize | maxPlayers |
|----------|------|-----------|------------|
| < 4 GB   | ≥1   | 750       | 10         |
| 4–7 GB   | ≥1   | 2000      | 40         |
| 8–15 GB  | ≥2   | 3000      | 75         |
| 16–31 GB | ≥4   | 4000      | 100        |
| 32+ GB   | ≥4   | 4500      | 150        |

### CPU Floor Rule

If CPU count is below the required minimum for a tier, the server **drops to the next tier down**. For example:
- **10 GB RAM + 1 CPU** → 2000 (not 3000, which requires ≥2 CPUs)
- **16 GB RAM + 2 CPUs** → 3000 (not 4000, which requires ≥4 CPUs)

---

## Plugin Resource Impact

> **Warning:** These tiers are based on a **vanilla Oxide server with default umod plugins bundled in this image**. Every Oxide plugin loaded adds to memory and CPU overhead — especially plugins that run `OnTick` hooks, manage entities, or maintain large in-memory data structures.
>
> Servers with many plugins or heavy plugins (e.g., XDQuest, Kits, WaterBases) should consider **stepping down one tier** from the auto-detected recommendation, or bumping their memory/CPU allocation. Monitor RSS memory in the first 30 minutes after world generation completes to verify the tier is adequate.

---

## Overriding Auto-Config

To skip auto-detection and explicitly set values:

### Docker Run
```bash
docker run -e RUST_SERVER_WORLDSIZE=2000 -e RUST_SERVER_MAXPLAYERS=50 ...
```

### Helm (penguin-rust)
Set `serverConfig.worldSize` and `serverConfig.maxPlayers` in `values.yaml`. These are always passed as environment variables and take precedence over auto-detection:

```yaml
serverConfig:
  worldSize: 2000
  maxPlayers: 50
```

### Both Methods
- **If both env vars are set**, auto-config is skipped entirely
- **If only one is set**, the other is auto-detected from the tier table
- Helm values always override docker run env vars for Kubernetes deployments

---

## Re-triggering Detection

The lock file is located at:
```
/steamcmd/rust/server/<identity>/.auto-config.lock
```

(default identity is `rust_server`)

To re-trigger resource detection — for example, after adding more RAM to a Kubernetes pod or starting fresh on a new PVC:

### Kubernetes
```bash
kubectl exec -n <namespace> <pod-name> -- rm /steamcmd/rust/server/rust_server/.auto-config.lock
# Then restart the pod
kubectl rollout restart deployment/<deployment-name> -n <namespace>
```

### Docker
```bash
docker exec <container> rm /steamcmd/rust/server/rust_server/.auto-config.lock
# Then restart the container
docker restart <container>
```

### Fresh PVC
When the PVC is deleted and a new deployment is created, the lock file is automatically wiped — the next start gets a fresh detection.

---

## Lock File Contents

The lock file records the hardware detected and configuration applied. Example:

```
# Auto-config lock — delete this file to re-trigger resource detection on next start.
# Generated: 2025-04-13T12:00:00Z
DETECTED_CPUS=4
DETECTED_MEM_GB=14
AUTO_WORLDSIZE=3000
AUTO_MAXPLAYERS=75
APPLIED_WORLDSIZE=3000
APPLIED_MAXPLAYERS=75
```

This file:
- Records detection timestamp for auditing
- Shows detected and applied values for comparison
- Can be manually deleted to force re-detection on next restart
- Is automatically created in `start.sh` if not present
