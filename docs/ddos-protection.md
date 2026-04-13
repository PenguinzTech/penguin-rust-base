# DDoS Protection

Optional per-source-IP UDP rate limiting for the Rust game port, implemented via `iptables` `hashlimit` in the container's network namespace.

**Default: off.** Set `DDOS_PROTECT=1` to enable. When off, the image runs with no extra capabilities.

---

## When to enable

- Public-facing servers without an upstream L3/L4 scrubber (e.g. bare `docker run` on a VPS)
- Small operators without Cloudflare Spectrum / AWS Shield / GCP Armor in front
- Homelabs where the ISP doesn't filter volumetric UDP floods

**When NOT to enable** — skip if your traffic already passes through:

- Cloudflare Spectrum (TCP/UDP proxying with built-in DDoS protection)
- A dedicated DDoS scrubber (Corero, Arbor, etc.)
- An L4 load balancer with rate limiting (MetalLB + BGP policies, HAProxy, etc.)
- A Kubernetes CNI with NetworkPolicy-based rate limits (Cilium `BandwidthManager`, Calico, etc.)

In those cases, the upstream layer handles floods before packets reach the container, and the in-container filter just adds overhead.

---

## Environment variables

| Variable | Default | Description |
|---|---|---|
| `DDOS_PROTECT` | `0` | `1` to enable iptables rate limiting; `0` to skip entirely |
| `DDOS_UDP_RATE` | `60` | Sustained packets/sec allowed per source IP |
| `DDOS_UDP_BURST` | `120` | Burst packets allowed before rate-limit kicks in |

Rust clients typically send 30–40 pps during active play, so `60/120` leaves headroom for legitimate spikes (voice, entity churn) while still dropping IPs that hit thousands of pps.

---

## Required capability

`DDOS_PROTECT=1` needs `CAP_NET_ADMIN` so `iptables` can write netfilter rules:

```bash
docker run --cap-add=NET_ADMIN -e DDOS_PROTECT=1 ...
```

```yaml
# Kubernetes
securityContext:
  capabilities:
    add: ["NET_ADMIN"]
```

**If the capability is missing**, startup logs `WARNING: DDOS_PROTECT=1 but iptables setup failed (missing NET_ADMIN?) — continuing without protection` and the server starts normally — the container never crashes on a missing cap.

---

## What it does

On startup, if `DDOS_PROTECT=1`, the container installs a chain:

```
iptables -N RUST_DDOS
iptables -A RUST_DDOS -m hashlimit \
    --hashlimit-above ${DDOS_UDP_RATE}/sec \
    --hashlimit-burst ${DDOS_UDP_BURST} \
    --hashlimit-mode srcip \
    --hashlimit-name rust_ddos \
    --hashlimit-htable-expire 60000 \
    -j DROP
iptables -A RUST_DDOS -j ACCEPT
iptables -I INPUT -p udp --dport ${RUST_SERVER_PORT} -j RUST_DDOS
```

**How it works:**

- The kernel hashes each incoming UDP packet by source IP
- Per-IP token bucket: `DDOS_UDP_BURST` tokens, refilled at `DDOS_UDP_RATE`/sec
- Packets arriving when the bucket is empty → `DROP` (never reaches Rust)
- Hash entries expire 60s after last packet, so legitimate reconnects aren't penalized
- All in kernel space — zero userspace overhead per packet

**What it does NOT do:**

- No L7 inspection (can't tell Rust query flood from gameplay traffic)
- No permanent bans (token bucket resets; blocked IP just has to slow down)
- No correlation with SteamID or Rust logs (see "Roadmap" below)
- No protection against spoofed source IPs (spoofed floods saturate upstream anyway)

---

## Tuning

**Symptoms that rate is too low** (legitimate players dropped):

- Players report rubber-banding or disconnects during raids / grenades / firefights
- Rust server logs show `NetworkError: timeout` for normal-looking players

**Fix:** increase `DDOS_UDP_RATE` in steps of 30 (60 → 90 → 120) until symptoms clear.

**Symptoms that rate is too high** (floods leaking through):

- Rust server RSS or CPU spikes during attacks
- `tcpdump -i eth0 -n udp port 28015` shows single source IPs at thousands of pps

**Fix:** decrease `DDOS_UDP_BURST` first (120 → 60 → 30); only lower `DDOS_UDP_RATE` if burst alone isn't enough.

**Verify rules loaded:**

```bash
docker exec rust-server iptables -L RUST_DDOS -v -n
```

The `packets` and `bytes` columns on the `DROP` rule count what's been filtered.

---

## Kubernetes-layer alternative

If you're on K8s and don't want to grant `NET_ADMIN` to the game pod, push rate limiting to the cluster edge instead:

**Cilium** (if your CNI):

```yaml
apiVersion: cilium.io/v2
kind: CiliumNetworkPolicy
metadata:
  name: rust-ratelimit
spec:
  endpointSelector:
    matchLabels:
      app: rust-server
  ingress:
    - fromEntities: [world]
      toPorts:
        - ports:
            - port: "28015"
              protocol: UDP
          rateLimit:
            requestsPerSecond: 60
```

**Trade-off** — cluster-edge rate limiting drops floods before they hit the pod (great), but loses visibility into the SteamID ↔ IP mapping that Rust server logs provide. If you want to auto-ban specific players after correlating their Steam ID to a flood source, you need the in-container filter or a sidecar that reads Rust logs.

---

## Roadmap

- **SteamID-aware auto-ban** — a log watcher that parses Rust/Steam logs for client IPs, correlates with iptables `hashlimit` drop counters, and escalates repeat offenders to a permanent ban (respecting the admin Steam ID whitelist from `RUST_ADMIN_STEAMIDS`). Not yet implemented.
- **fail2ban integration** — use `fail2ban` to watch Rust logs and ban IPs with a configurable jail. Not yet implemented.
