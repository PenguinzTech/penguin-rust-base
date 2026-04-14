# WAF Sidecar

Go-based network-layer firewall that protects Rust dedicated servers from DDoS floods, cheater reconnect storms, RCON brute-force, and packet anomalies.

## Works in Pure Vanilla Mode

The WAF operates **below the Rust game engine** — at the UDP/TCP packet level. It requires no Oxide, no plugins, no mods. Every protection described below runs on vanilla servers out of the box.

Facepunch Rust runs a **single-threaded C#/Mono game loop**. Every packet it processes — even malicious ones — consumes that thread. The WAF intercepts traffic before the game server sees it: malicious packets are dropped in Go's concurrent runtime (cheap), clean packets are forwarded. The game loop stays responsive even under attack.

## Architecture

The WAF is an optional sidecar container sharing the same pod as the game server (same network namespace). When enabled:

1. WAF claims the public-facing ports: `28015` (game), `28016` (RCON), `28017` (query)
2. Game server shifts to loopback offset ports: `28115`, `28116`, `28117`
3. WAF forwards clean traffic to loopback — zero inter-pod latency
4. No change to external port mappings

```
Internet
    ↓
WAF sidecar (28015 / 28016 / 28017)  ─┐
                                        │ same pod, loopback
Game server (28115 / 28116 / 28117)  ◄─┘
```

## Protections

### Always Active (Vanilla + Oxide)

| Protection | Detail |
|---|---|
| **Rate limiting** | Per-IP packet rate cap (default: 150 pps). Excess packets dropped. |
| **Flood detection** | Sustained high packet rate from a single IP triggers a timed block. Default: 10 connections/sec threshold. |
| **RCON brute-force** | Failed RCON auth attempts tracked per IP. After N failures (default: 5), IP is throttled. |
| **Ban evasion** | Steam64 ID extracted from Rust handshake packets. Banned player reconnecting via a new IP is still blocked. |
| **IP block list** | Static/dynamic IP block list applied before any game protocol parsing. |
| **Packet anomalies** | Malformed or oversized packets dropped before the game server sees them. |
| **Aimbot heuristics** | Coefficient of variation (CV) of inter-packet timing intervals. Bot-like timing consistency (low CV) is flagged. |
| **Priority bypass** | Admin/owner Steam64 IDs always forwarded immediately — skip all heuristics and rate checks. |

### With Oxide (Optional Enhancements)

When Oxide plugins are running, they can push runtime enforcement rules to the WAF via a loopback REST API. Plugins are the "eyes" (they observe game behaviour); the WAF is the "enforcement arm" (it blocks at the network layer).

Use cases:
- Plugin detects an ESP cheater in-game → pushes a BLOCK rule for their Steam64 ID
- Plugin detects a speedhacker → pushes a THROTTLE rule to degrade their connection
- Plugin triggers a ALERT rule for suspicious players → operators see it in Prometheus

Rules survive until explicitly deleted or the WAF is restarted. They are not required for base protections to function.

## 12-Step Inspection Pipeline

Every inbound packet is processed in order:

1. **SteamID extraction** — Parse Rust handshake; extract Steam64 ID from `proto.Auth.external_auth_id`
2. **Priority check** — Admin/owner whitelist? Forward immediately; skip remaining steps
3. **Allowlist lookup** — IP in allowlist? Forward immediately
4. **IP block check** — IP in block list? Drop
5. **SteamID block check** — Steam64 ID in block list? Drop (catches ban evasion)
6. **Rate limit** — Per-IP request rate over threshold? Drop excess packets
7. **Flood detection** — Sustained high pps from single IP? Block
8. **RCON brute-force** — Too many failed RCON auth attempts? Throttle IP
9. **Pattern heuristics** — Aimbot timing CV, packet size anomalies? Flag
10. **Rule engine** — Match against custom rules pushed by Oxide plugins
11. **Rule actions** — Apply matched rule action (LOG / ALERT / THROTTLE / DROP / BLOCK)
12. **Forward** — Send clean packet to game server loopback offset port

## Config File Sync

The WAF auto-polls Rust's server config files every 5 minutes (mtime-gated — only re-parses if the file changed):

- `bans.cfg` — banned Steam64 IDs synced into the block list
- `users.cfg` — owner/admin Steam64 IDs synced into the priority whitelist

No manual WAF restart needed when bans are updated in-game.

## Enabling

### Kubernetes (Helm)

```bash
helm install rust-server ./k8s/helm/rust-server --set waf.enabled=true
```

### Kubernetes (Helm with custom limits)

```bash
helm install rust-server ./k8s/helm/rust-server \
  --set waf.enabled=true \
  --set waf.rateLimitPPS=200 \
  --set waf.floodThresholdCPS=15 \
  --set waf.rconBanAfter=3
```

When `waf.enabled=false` (default), the sidecar container is not added to the pod — zero overhead, zero port remapping.

## Environment Variables

| Variable | Default | Description |
|---|---|---|
| `WAF_LISTEN_GAME_PORT` | `28015` | Public-facing game port |
| `WAF_UPSTREAM_GAME_PORT` | `28115` | Loopback game port (game server listens here) |
| `WAF_LISTEN_RCON_PORT` | `28016` | Public-facing RCON port |
| `WAF_UPSTREAM_RCON_PORT` | `28116` | Loopback RCON port |
| `WAF_LISTEN_QUERY_PORT` | `28017` | Public-facing query port |
| `WAF_UPSTREAM_QUERY_PORT` | `28117` | Loopback query port |
| `WAF_API_PORT` | `8080` | Loopback REST API (Oxide plugins) |
| `WAF_METRICS_PORT` | `9090` | Prometheus metrics |
| `WAF_RATE_LIMIT_PPS` | `150.0` | Per-IP packet rate cap (packets/sec) |
| `WAF_FLOOD_THRESHOLD_CPS` | `10.0` | Connections/sec threshold for flood detection |
| `WAF_RCON_BAN_AFTER` | `5` | Failed RCON auth attempts before throttle |
| `WAF_AIMBOT_CV` | `0.05` | Timing CV threshold for aimbot flagging (lower = stricter) |
| `WAF_PRIORITY_STEAM_IDS` | *(none)* | Comma-separated Steam64 IDs that always bypass checks |
| `WAF_USERS_CFG_PATH` | *(none)* | Path to `users.cfg` for admin sync |
| `WAF_BANS_CFG_PATH` | *(none)* | Path to `bans.cfg` for ban sync |
| `WAF_CFG_POLL_INTERVAL` | `5m` | How often to re-check config files |

## Oxide Plugin REST API

The WAF management API listens on `127.0.0.1:8080` (loopback only — no auth needed from same pod).

### Endpoints

| Method | Endpoint | Payload | Behaviour |
|---|---|---|---|
| `GET` | `/healthz` | — | `200 OK` if WAF is ready |
| `POST` | `/api/v1/block` | `{"ip": "1.2.3.4", "duration_secs": 3600}` | Block IP for N seconds |
| `POST` | `/api/v1/throttle` | `{"ip": "1.2.3.4", "delay_ms": 100}` | Delay packets from IP by N ms |
| `POST` | `/api/v1/allowlist` | `{"ip": "1.2.3.4"}` | Add IP to permanent allowlist |
| `POST` | `/api/v1/report` | `{"ip": "1.2.3.4", "reason": "aimbot", "steam64": "765..."}` | Report suspicious IP (logged for operators) |
| `GET` | `/api/v1/suspects` | — | List flagged IPs and offense counts |
| `GET` | `/api/v1/stats/{ip}` | — | Packet stats for IP (count, rate, sizes, Steam64) |
| `GET` | `/api/v1/stats/steam/{steamId}` | — | Packet stats for Steam64 ID across all IPs |
| `GET` | `/api/v1/rules` | — | List all active rules |
| `POST` | `/api/v1/rules` | See below | Create rule |
| `PATCH` | `/api/v1/rules/{id}` | Same as POST | Update rule |
| `DELETE` | `/api/v1/rules/{id}` | — | Delete rule |
| `GET` | `/api/v1/priority` | — | List admin/owner Steam64 whitelist |
| `POST` | `/api/v1/priority` | `{"steam64": "765..."}` | Add Steam64 to priority whitelist |
| `DELETE` | `/api/v1/priority/{steam64}` | — | Remove from priority whitelist |

### Rule Payload

```json
{
  "id": "my-rule-1",
  "port": 28015,
  "packet_rate_min": 1000,
  "packet_rate_max": 5000,
  "packet_size_min": 64,
  "packet_size_max": 512,
  "payload_hex": "deadbeef",
  "timing_cv_max": 0.03,
  "steam64": "76561198123456789",
  "action": "DROP"
}
```

All fields except `id` and `action` are optional. Rules match when **all specified conditions** are true.

Rule actions: `LOG`, `ALERT`, `THROTTLE`, `DROP`, `BLOCK`

Up to 200 active rules.

### Oxide Plugin Example

```csharp
using System.Net.Http;
using System.Text;

class WafPlugin : RustPlugin
{
    private static readonly HttpClient _client = new();
    private const string WafBase = "http://127.0.0.1:8080";

    // Called by your cheat detection logic
    void ReportCheater(BasePlayer player, string reason)
    {
        var json = $"{{\"steam64\":\"{player.UserIDString}\",\"reason\":\"{reason}\"}}";
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        _client.PostAsync($"{WafBase}/api/v1/report", content);
    }

    // Hard block — use when confident
    void BlockPlayer(BasePlayer player, int durationSecs = 3600)
    {
        var json = $"{{\"ip\":\"{player.net.connection.ipaddress.Split(':')[0]}\",\"duration_secs\":{durationSecs}}}";
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        _client.PostAsync($"{WafBase}/api/v1/block", content);
    }
}
```

## Prometheus Metrics

Exposed on `:9090/metrics`.

| Metric | Type | Description |
|---|---|---|
| `waf_packets_total` | Counter | Total packets processed, labelled by port |
| `waf_packets_forwarded_total` | Counter | Packets forwarded to game server |
| `waf_packets_dropped_total` | Counter | Packets dropped, labelled by reason: `rate_limit`, `flood`, `blocked_ip`, `blocked_steam`, `brute_force`, `rule`, `pattern` |
| `waf_packet_rate_pps` | Gauge | Current pps per IP (top 10 by rate) |
| `waf_blocked_ips_active` | Gauge | Current count of blocked IPs |
| `waf_rules_active` | Gauge | Current count of active rules |
| `waf_steam_ids_seen` | Gauge | Unique Steam64 IDs observed this interval |
| `waf_rcon_auth_failures` | Counter | Failed RCON auth attempts by IP |
| `waf_latency_ms` | Histogram | Packet processing latency (p50, p99) |

## WAF vs iptables DDoS Protection

The image includes two independent DDoS mitigations that complement each other:

| | WAF Sidecar | iptables (`NET_ADMIN`) |
|---|---|---|
| **Requires** | Nothing extra | `NET_ADMIN` capability |
| **Works vanilla** | Yes | Yes |
| **Protocol aware** | Yes — Steam protocol, SteamID | No — IP/port only |
| **Aimbot / cheat heuristics** | Yes | No |
| **RCON brute-force** | Yes | No |
| **Oxide integration** | Yes | No |
| **Prometheus metrics** | Yes | No |
| **Ban evasion detection** | Yes (Steam64) | No |

Use both for defence in depth: iptables drops volumetric floods at kernel level before they reach Go; the WAF handles protocol-aware enforcement.

## Source Code

The WAF lives in `waf/` at the repo root. It is built into the overlay image during the CI pipeline and embedded as a sidecar in the Kubernetes pod definition.

```
waf/
├── cmd/waf/main.go             # Entry point, env config, goroutine wiring
├── internal/
│   ├── api/server.go           # REST API (block, throttle, rules, stats)
│   ├── cfg/poller.go           # bans.cfg / users.cfg file watcher
│   ├── detect/
│   │   ├── flood.go            # Flood / connection-rate detector
│   │   ├── patterns.go         # Packet size + timing heuristics
│   │   ├── ratelimit.go        # Per-IP token bucket rate limiter
│   │   ├── rcon.go             # RCON brute-force tracker
│   │   └── steamid.go          # Steam64 → IP mapper
│   ├── metrics/metrics.go      # Prometheus counters / gauges / histograms
│   ├── proxy/
│   │   ├── pipeline.go         # Shared pipeline struct
│   │   ├── rcon.go             # TCP RCON proxy
│   │   └── udp.go              # UDP game / query proxy
│   ├── rules/engine.go         # Snort-like rule engine
│   └── state/state.go          # Shared in-memory state (blocks, allowlist, priority)
└── Dockerfile                  # Multi-stage Go build → debian:bookworm-slim runtime
```

## See Also

- Game server Helm chart: `k8s/helm/rust-server`
- Kustomize overlays: `k8s/kustomize/overlays/{alpha,beta,prod}`
- iptables DDoS protection: [docs/ddos-protection.md](ddos-protection.md)
