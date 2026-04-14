# Rust WAF Sidecar

Go-based network-layer Web Application Firewall (WAF) for Facepunch Rust dedicated game servers running in Kubernetes. Protects single-threaded Mono/C# game loop from DDoS, cheater floods, and malformed traffic before packets reach the game server.

## Why a Sidecar WAF?

Facepunch Rust runs a **single-threaded game loop** in C#/Mono. Every packet processed—even malicious ones—consumes that single thread. Packet processing stalls tick rate, causing:

- Frame hitches and lag for legitimate players
- Server unresponsive during DDoS floods
- Cheater reconnect floods blocking all other connections

The WAF sidecar intercepts traffic **before the game server** sees it. Malicious/malformed packets are dropped in Go (concurrent, lightweight goroutines). Clean traffic is forwarded. Result: game loop stays responsive even under attack.

## Architecture

Optional sidecar container sharing the same pod (same network namespace). When enabled:

1. WAF takes public-facing ports: `28015` (game), `28016` (RCON), `28017` (query)
2. Game server listens on loopback offset ports: `28115`, `28116`, `28117`
3. WAF forwards clean traffic to loopback offset ports
4. Zero impact when disabled (default)

```
Internet
    ↓
WAF (28015/28016/28017) [Kubernetes Pod]
    ↓
Game Server (28115/28116/28117) [same pod, localhost]
```

## 12-Step Inspection Pipeline

Every inbound packet goes through this sequence:

1. **SteamID Extraction** — Parse Rust handshake packets; extract Steam64 ID from `proto.Auth.external_auth_id`
2. **Priority Check** — Admin/owner whitelist? Forward immediately, skip all other checks
3. **Allowlist Lookup** — IP in allowlist? Forward immediately
4. **IP Block Check** — IP in block list? Drop packet
5. **SteamID Block Check** — Steam64 ID in block list? Drop packet (ban evasion detection)
6. **Rate Limit** — Per-IP request rate exceeding threshold? Throttle or drop
7. **Flood Detection** — Sustained high packet rate (pps) from single IP? Flag and drop
8. **RCON Brute-Force Protection** — Too many failed RCON auth attempts? Throttle IP
9. **Pattern Heuristics** — Aimbot-like timing CV, packet size anomalies, other network layer signals? Flag
10. **Rule Engine** — Match against custom rules (via Oxide plugin API). Actions: LOG, ALERT, THROTTLE, DROP, BLOCK
11. **Rule Actions** — Apply rule action (drop, throttle, log)
12. **Forward** — Send packet to game server loopback offset port

## SteamID-Aware Enforcement

WAF extracts Steam64 IDs from handshake packets and maps them to source IPs.

- **Ban evasion detection** — If a Steam64 ID changes IP, WAF recognizes it's the same banned player
- **Admin/owner protection** — Owners and admins always bypass heuristics and rules; they are always forwarded
- **Config sync** — Auto-polls Rust config files every 5 minutes (mtime-gated):
  - `bans.cfg` — banned Steam64 IDs
  - `users.cfg` — owner/admin Steam64 IDs
  - Cheap operation; only parses if file modified

## Snort-Like Rule Engine

Up to 200 runtime rules pushed by Oxide C# plugins via REST API. Rules match on:

- **Port** — game (28015), RCON (28016), query (28017)
- **Packet rate range** — e.g., 1000–5000 pps
- **Packet size range** — e.g., 64–1500 bytes
- **Payload hex pattern** — e.g., match specific malformed headers
- **Timing CV** — inter-packet interval coefficient of variation (aimbot detection)
- **Steam64 ID** — target specific players

Rule actions:

| Action | Behavior |
|--------|----------|
| `LOG` | Log packet; forward |
| `ALERT` | Log alert; forward |
| `THROTTLE` | Delay packet by N ms; forward |
| `DROP` | Discard packet |
| `BLOCK` | Block IP for N seconds |

Built-in protections (SteamID, rate limit, flood detection, brute-force) always execute first. Rules are checked last.

## Oxide Plugin API

C# Oxide plugins call `http://127.0.0.1:8080` (loopback—no authentication needed for same pod). Plugins detected a cheater in-game? Push a BLOCK rule. Suspicious pattern? Push an ALERT rule.

### Endpoints

| Method | Endpoint | Payload | Behavior |
|--------|----------|---------|----------|
| `GET` | `/healthz` | — | Return 200 OK if WAF ready |
| `POST` | `/api/v1/block` | `{"ip": "192.168.1.1", "duration_secs": 3600}` | Block IP for N seconds |
| `POST` | `/api/v1/throttle` | `{"ip": "192.168.1.1", "delay_ms": 100}` | Throttle IP packets by N ms |
| `POST` | `/api/v1/allowlist` | `{"ip": "192.168.1.1"}` | Add IP to allowlist |
| `POST` | `/api/v1/report` | `{"ip": "192.168.1.1", "reason": "aimbot", "steam64": "12345..."}` | Report suspicious IP (logged for operators) |
| `GET` | `/api/v1/suspects` | — | Return list of flagged IPs and their offense counts |
| `GET` | `/api/v1/stats/{ip}` | — | Return packet stats for IP (count, rate, sizes, SteamID) |
| `GET` | `/api/v1/stats/steam/{steamId}` | — | Return packet stats for Steam64 ID across all IPs |
| `GET` | `/api/v1/rules` | — | List all rules |
| `POST` | `/api/v1/rules` | `{"id": "rule-1", "port": 28015, "packet_size_min": 64, "packet_size_max": 512, "payload_hex": "deadbeef", "action": "DROP"}` | Create rule |
| `PATCH` | `/api/v1/rules/{id}` | Same as POST | Update rule |
| `DELETE` | `/api/v1/rules/{id}` | — | Delete rule |
| `GET` | `/api/v1/priority` | — | List admin/owner SteamID whitelist |
| `POST` | `/api/v1/priority` | `{"steam64": "12345..."}` | Add Steam64 ID to priority whitelist |
| `DELETE` | `/api/v1/priority/{steam64}` | — | Remove from priority whitelist |

## Prometheus Metrics

Exposed on `:9090/metrics`. Key metrics:

| Metric | Type | Description |
|--------|------|-------------|
| `waf_packets_total` | Counter | Total packets processed by port |
| `waf_packets_forwarded_total` | Counter | Packets forwarded to game server |
| `waf_packets_dropped_total` | Counter | Packets dropped (reason label: rate_limit, flood, blocked_ip, blocked_steam, brute_force, rule, pattern) |
| `waf_packet_rate_pps` | Gauge | Current packets/sec per IP (top 10 by rate) |
| `waf_blocked_ips_active` | Gauge | Current count of blocked IPs |
| `waf_rules_active` | Gauge | Current count of active rules |
| `waf_steam_ids_seen` | Gauge | Unique Steam64 IDs observed this interval |
| `waf_rcon_auth_failures` | Counter | Failed RCON authentication attempts by IP |
| `waf_latency_ms` | Histogram | Packet processing latency (p50, p99) |

## Configuration

Enable WAF via Helm:

```bash
helm install rust-server ./k8s/helm/rust-server --set waf.enabled=true
```

Configure via environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `WAF_ENABLED` | `false` | Enable/disable WAF |
| `WAF_GAME_PORT` | `28015` | Game port (public-facing) |
| `WAF_RCON_PORT` | `28016` | RCON port (public-facing) |
| `WAF_QUERY_PORT` | `28017` | Query port (public-facing) |
| `WAF_OFFSET_PORT_BASE` | `28115` | Loopback port base (game=28115, rcon=28116, query=28117) |
| `WAF_RATE_LIMIT_PPS` | `2000` | Per-IP packets/sec threshold |
| `WAF_FLOOD_THRESHOLD_PPS` | `5000` | Sustained pps to trigger flood detection |
| `WAF_FLOOD_DURATION_SECS` | `300` | Flooding block duration (seconds) |
| `WAF_RCON_MAX_FAILURES` | `5` | Failed RCON attempts before brute-force block |
| `WAF_RCON_FAILURE_WINDOW_SECS` | `60` | Time window for counting failures |
| `WAF_CONFIG_SYNC_INTERVAL_SECS` | `300` | Poll interval for bans.cfg / users.cfg (seconds) |
| `WAF_CONFIG_PATH` | `/rust/server` | Path to Rust server config directory |
| `WAF_API_LISTEN_ADDR` | `127.0.0.1:8080` | Loopback API address (for Oxide plugins) |
| `WAF_METRICS_LISTEN_ADDR` | `0.0.0.0:9090` | Prometheus metrics address |
| `WAF_LOG_LEVEL` | `info` | Log level: debug, info, warn, error |

## Cheat Detection Philosophy

The WAF does **not** decrypt Rust's game protocol. It operates at the network layer and detects **anomalies**, not game events:

- **Aimbot detection** — Suspicious timing consistency in inter-packet intervals (low coefficient of variation suggests bot-like behavior)
- **Packet size anomalies** — Malformed or oversized packets dropped before game server sees them
- **Flood patterns** — Sudden sustained high packet rate from single IP
- **Brute-force patterns** — Rapid RCON auth failures

**Oxide plugins are the eyes**. They observe game behavior (ESP cheating, speedhack, wall penetration, etc.) and push enforcement rules back to the WAF via the REST API:

```csharp
// Oxide plugin detects ESP cheater in-game
var steamId = "76561198123456789";
var reason = "ESP detected: suspicious line-of-sight violations";
client.PostAsync("http://127.0.0.1:8080/api/v1/report", 
    new StringContent($"{{\"steam64\": \"{steamId}\", \"reason\": \"{reason}\"}}"));
```

The WAF becomes the **enforcement arm**. It blocks the Steam64 ID across any IP, even as cheaters jump servers to evade bans.

## Deployment Notes

- WAF runs in same pod as game server (no inter-pod latency)
- Adds minimal CPU/memory overhead (Go binary, concurrent packet processing)
- No external dependencies (embedded Prometheus, no DB required)
- Health check: `curl http://127.0.0.1:8080/healthz`
- Logs all dropped packets to stdout (sanitized, no PII except Steam64 ID)

## Example: Oxide Integration

```csharp
using System.Net.Http;

class WafPlugin : RustPlugin
{
    private readonly HttpClient _client = new();

    public void OnPlayerConnected(BasePlayer player)
    {
        // WAF already has the IP and Steam64 from handshake
        // Plugins just report findings
    }

    public void OnPlayerSuspected(BasePlayer player, string cheatType)
    {
        var json = JsonConvert.SerializeObject(new {
            steam64 = player.userID.ToString(),
            reason = cheatType,
            severity = "high"
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        _client.PostAsync("http://127.0.0.1:8080/api/v1/report", content);
    }
}
```

## See Also

- Game server Helm chart: `k8s/helm/rust-server`
- Kustomize overlays: `k8s/kustomize/overlays/{alpha,beta,prod}`
- Oxide plugin examples: `plugins/waf-integration/`
