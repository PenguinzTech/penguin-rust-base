# Security

Overview of the security posture baked into this image, what the CI pipeline scans on every push, and what you still need to do yourself when running it.

For DDoS rate limiting, see **[ddos-protection.md](ddos-protection.md)** — that feature is separately documented because it's opt-in and has its own capability requirements.

---

## Runtime hardening

| Control | Where | Notes |
|---|---|---|
| **Non-root execution** | `USER rustserver` (UID 1000) | Container runs as unprivileged user; enforced in Dockerfile and survives image inheritance |
| **No embedded credentials** | `RUST_RCON_PASSWORD` auto-generated on first boot | 31-char alphanumeric, persisted to PVC at `.rcon.pw`, never baked into the image |
| **No capabilities by default** | Dockerfile adds none | `NET_ADMIN` only requested if operator enables DDoS protection — see [ddos-protection.md](ddos-protection.md) |
| **SHELL `-o pipefail`** | Dockerfile-wide | Pipe failures (e.g. `curl ... \| tar ...`) abort the build instead of being masked by a successful downstream command |
| **HEALTHCHECK** | Dockerfile | Uses native `pgrep` against `RustDedicated` — no `curl`/`wget` dependency; 10-minute `start-period` accommodates world generation |
| **Oxide data on PVC** | `/steamcmd/rust/server/oxide-data` | Permissions DB and whitelist survive restarts; prevents re-seeding with default data after a restart |
| **Auto-config lock file** | `.auto-config.lock` | Prevents `worldSize` churn on restart — a changed world size corrupts the on-disk save |

---

## CI security scanning

Every push to `main` runs the **Security Scanning** workflow (`.github/workflows/security.yml`). Failing a check blocks the build — pre-existing issues are not tolerated.

| Scanner | What it checks | Failure threshold |
|---|---|---|
| **hadolint** | Dockerfile best practices and security patterns | `warning` — any warning fails |
| **shellcheck** | `start.sh` — quoting, unsafe patterns, unquoted variables, subshell traps | any violation fails |
| **gitleaks** | Full git history for hardcoded secrets, API keys, credentials | any leak fails |
| **Checkov** | CKV_DOCKER_1 → CKV_DOCKER_22 (non-root user, HEALTHCHECK, no sudo, no `ADD` for remote URLs, etc.) | `soft-fail: false` |
| **Trivy** | CVEs in the published image (base image + installed packages) at `CRITICAL` + `HIGH` | `exit-code: 1` on any finding; runs post-push on `main` only |

**Action pinning** — every third-party action is pinned to a full commit SHA, not a floating tag, so a compromised action repository cannot silently inject new behaviour.

---

## What's NOT scanned

| Not scanned | Why | Your responsibility |
|---|---|---|
| **Third-party `.cs` plugin files** (umod + AutoAdmin) | Not our code; semgrep/CodeQL signal on Oxide/umod source is noisy and usually false-positive | Vet plugins manually before production; pin to a known commit with `UMOD_PLUGINS_HASH` if you need reproducibility |
| **Rust dedicated server binaries** | Binary from Steam — Facepunch's responsibility; we trust their distribution | Pin to a specific `STEAM_BUILD_ID` if you need a fixed version |
| **Oxide framework binaries** | Binary from OxideMod — upstream's responsibility | Pin `OXIDE_VERSION` if you need a fixed version |
| **Plugin runtime behaviour** | Dynamic — scanners can't model Oxide plugin hooks | Monitor `oxide/logs/log.txt` for unusual activity |

---

## Reporting vulnerabilities

If you find a vulnerability in this image (as opposed to Rust, Oxide, or a third-party plugin), please open a **private security advisory** on GitHub rather than a public issue:

→ https://github.com/PenguinzTech/penguin-rust-base/security/advisories/new

For vulnerabilities in Rust itself, report to Facepunch. For Oxide, report to the OxideMod team. For umod plugins, report to the plugin author on umod.org.
