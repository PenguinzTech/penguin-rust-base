#!/usr/bin/env bash
# scripts/admin.sh — Rust server admin operations
#
# MODE=reboot        warn players → save → restart pod
# MODE=wipe          warn players → delete .sav files (no save) → restart pod
# MODE=force-restart immediate pod restart; no RCON needed
# MODE=message       broadcast MESSAGE to all players via RCON
# MODE=save          force a world save via RCON
# MODE=ban           search player by name/SteamID → confirm → banid + server.writecfg
# MODE=admin         search player → confirm → ownerid + server.writecfg
# MODE=mod           search player → confirm → moderatorid + server.writecfg
# MODE=whitelist     search player → confirm → oxide.grant user <id> whitelist.allow
#
# Usage:
#   RCON_PASSWORD=<pw> make reboot
#   RCON_PASSWORD=<pw> make wipe
#   make force-restart
#   RCON_PASSWORD=<pw> MESSAGE="text" make message
#   RCON_PASSWORD=<pw> make save
#   RCON_PASSWORD=<pw> [PLAYER=<id/name>] [REASON=<reason>] make ban
#   RCON_PASSWORD=<pw> [PLAYER=<id/name>] make admin
#   RCON_PASSWORD=<pw> [PLAYER=<id/name>] make mod
#   RCON_PASSWORD=<pw> [PLAYER=<id/name>] make whitelist

set -euo pipefail

RCON_HOST="${RCON_HOST:-}"
RCON_PORT="${RCON_PORT:-28016}"
RCON_PASSWORD="${RCON_PASSWORD:-}"
KUBE_CONTEXT="${KUBE_CONTEXT:-dal2-beta}"
NAMESPACE="${NAMESPACE:-penguin-rust}"
RELEASE="${RELEASE:-rust-server}"
SERVER_IDENTITY="${SERVER_IDENTITY:-rust_server}"
MESSAGE="${MESSAGE:-}"
PLAYER="${PLAYER:-}"
REASON="${REASON:-}"
MODE="${MODE:-reboot}"

# ── Validation ────────────────────────────────────────────────────────────────

VALID_MODES="reboot wipe force-restart message save ban admin mod whitelist"
if ! echo "$VALID_MODES" | grep -qw "$MODE"; then
  echo "ERROR: MODE must be one of: ${VALID_MODES} (got: '$MODE')"
  exit 1
fi

if [[ "$MODE" != "force-restart" && -z "$RCON_PASSWORD" ]]; then
  echo "ERROR: RCON_PASSWORD is required for MODE=${MODE}."
  echo "Usage: RCON_PASSWORD=<pw> make ${MODE}"
  exit 1
fi

# ── Auto-detect RCON host from LoadBalancer service ───────────────────────────

if [[ "$MODE" != "force-restart" && -z "$RCON_HOST" ]]; then
  echo "==> Auto-detecting RCON host from LoadBalancer service..."
  RCON_HOST=$(kubectl --context "${KUBE_CONTEXT}" get svc -n "${NAMESPACE}" \
    -l "app.kubernetes.io/instance=${RELEASE}" \
    -o jsonpath='{.items[0].status.loadBalancer.ingress[0].ip}' 2>/dev/null || true)

  if [[ -z "$RCON_HOST" ]]; then
    echo "ERROR: Could not auto-detect LoadBalancer IP. Set RCON_HOST=<ip> explicitly."
    exit 1
  fi
  echo "==> RCON host: ${RCON_HOST}"
fi

# ── RCON helpers ──────────────────────────────────────────────────────────────

rcon_cmd() {
  local cmd="$1"
  python3 - "$cmd" <<PYEOF
import asyncio, json, sys

CMD = sys.argv[1]

async def send():
    try:
        import websockets
    except ImportError:
        sys.exit("websockets required: pip3 install websockets")
    uri = "ws://${RCON_HOST}:${RCON_PORT}/${RCON_PASSWORD}"
    async with websockets.connect(uri, open_timeout=5) as ws:
        payload = json.dumps({"Identifier": 1, "Message": CMD, "Name": "WebRcon"})
        await ws.send(payload)
        resp = await asyncio.wait_for(ws.recv(), timeout=10)
        data = json.loads(resp)
        if data.get("Message"):
            print(data["Message"])

asyncio.run(send())
PYEOF
}

say() {
  echo "[${MODE}] Sending: $1"
  rcon_cmd "say $1" 2>/dev/null || true
}

# ── Player lookup via playerlist RCON ─────────────────────────────────────────
# Outputs one line per match: <steamid>\t<username>
# Exits non-zero if RCON call fails.

search_players() {
  local query="$1"
  python3 - "$query" <<PYEOF
import asyncio, json, sys

QUERY = sys.argv[1].lower()

async def search():
    try:
        import websockets
    except ImportError:
        sys.exit("websockets required: pip3 install websockets")
    uri = "ws://${RCON_HOST}:${RCON_PORT}/${RCON_PASSWORD}"
    async with websockets.connect(uri, open_timeout=5) as ws:
        payload = json.dumps({"Identifier": 99, "Message": "playerlist", "Name": "WebRcon"})
        await ws.send(payload)
        resp = await asyncio.wait_for(ws.recv(), timeout=10)
        data = json.loads(resp)
        try:
            players = json.loads(data.get("Message", "[]"))
        except (json.JSONDecodeError, TypeError):
            players = []
        matches = [p for p in players if QUERY in p.get("Username", "").lower()]
        for p in matches:
            print(f"{p['SteamID']}\t{p['Username']}")

asyncio.run(search())
PYEOF
}

# ── Resolve a player to (steamid, username) ───────────────────────────────────
# Sets RESOLVED_STEAMID and RESOLVED_NAME.
# Prompts for PLAYER if not set; prompts for confirmation if name search used.

resolve_player() {
  # Prompt if not provided
  if [[ -z "$PLAYER" ]]; then
    printf "Enter SteamID or player name: "
    read -r PLAYER
  fi

  if [[ -z "$PLAYER" ]]; then
    echo "ERROR: No player specified."
    exit 1
  fi

  # If input looks like a SteamID (17-digit number), use directly
  if [[ "$PLAYER" =~ ^[0-9]{17}$ ]]; then
    RESOLVED_STEAMID="$PLAYER"
    RESOLVED_NAME="(SteamID provided directly)"
    return 0
  fi

  # Name search — requires RCON/online players
  echo "==> Searching online players for \"${PLAYER}\"..."
  local raw_matches
  raw_matches=$(search_players "$PLAYER") || {
    echo "ERROR: RCON playerlist call failed. Is the server running?"
    exit 1
  }

  if [[ -z "$raw_matches" ]]; then
    echo "ERROR: No online players match \"${PLAYER}\"."
    echo "  • If the player is offline, provide their SteamID directly: PLAYER=76561198XXXXXXXXX make ${MODE}"
    exit 1
  fi

  local match_count
  match_count=$(echo "$raw_matches" | wc -l | tr -d ' ')

  if [[ "$match_count" -eq 1 ]]; then
    # Single match — confirm with user
    local steamid username
    steamid=$(echo "$raw_matches" | cut -f1)
    username=$(echo "$raw_matches" | cut -f2)
    printf "Found: %s  (%s)\nProceed? [y/N] " "$username" "$steamid"
    read -r confirm
    if [[ ! "$confirm" =~ ^[Yy]$ ]]; then
      echo "Aborted."
      exit 0
    fi
    RESOLVED_STEAMID="$steamid"
    RESOLVED_NAME="$username"
  else
    # Multiple matches — let user pick
    echo "Multiple matches found:"
    local i=1
    while IFS=$'\t' read -r steamid username; do
      printf "  %d) %s  (%s)\n" "$i" "$username" "$steamid"
      i=$((i + 1))
    done <<< "$raw_matches"
    printf "Enter number (or q to quit): "
    read -r pick
    if [[ "$pick" == "q" || -z "$pick" ]]; then
      echo "Aborted."
      exit 0
    fi
    if ! [[ "$pick" =~ ^[0-9]+$ ]] || [[ "$pick" -lt 1 || "$pick" -gt "$match_count" ]]; then
      echo "ERROR: Invalid selection."
      exit 1
    fi
    local chosen
    chosen=$(echo "$raw_matches" | sed -n "${pick}p")
    RESOLVED_STEAMID=$(echo "$chosen" | cut -f1)
    RESOLVED_NAME=$(echo "$chosen" | cut -f2)
  fi
}

# ── Pod lookup ────────────────────────────────────────────────────────────────

get_pod() {
  kubectl --context "${KUBE_CONTEXT}" get pods -n "${NAMESPACE}" \
    -l "app.kubernetes.io/instance=${RELEASE}" \
    --no-headers -o custom-columns=NAME:.metadata.name | head -1
}

# ══════════════════════════════════════════════════════════════════════════════
# MODE handlers
# ══════════════════════════════════════════════════════════════════════════════

# ── message ───────────────────────────────────────────────────────────────────

if [[ "$MODE" == "message" ]]; then
  if [[ -z "$MESSAGE" ]]; then
    printf "Admin message: "
    read -r MESSAGE
  fi
  if [[ -z "$MESSAGE" ]]; then
    echo "ERROR: MESSAGE is empty."
    exit 1
  fi
  echo "==> Broadcasting: ${MESSAGE}"
  rcon_cmd "say <color=cyan>[ADMIN]</color> ${MESSAGE}"
  exit 0
fi

# ── save ──────────────────────────────────────────────────────────────────────

if [[ "$MODE" == "save" ]]; then
  echo "==> Saving world..."
  rcon_cmd "server.save"
  echo "==> Done."
  exit 0
fi

# ── force-restart ─────────────────────────────────────────────────────────────

if [[ "$MODE" == "force-restart" ]]; then
  echo "=== Force-restart: ${RELEASE} in ${KUBE_CONTEXT}/${NAMESPACE} ==="
  kubectl --context "${KUBE_CONTEXT}" rollout restart deployment/"${RELEASE}" -n "${NAMESPACE}"
  echo "Pod restart triggered. Monitor with:"
  echo "  kubectl --context ${KUBE_CONTEXT} rollout status deployment/${RELEASE} -n ${NAMESPACE}"
  echo "  make logs KUBE_CONTEXT=${KUBE_CONTEXT}"
  exit 0
fi

# ── ban ───────────────────────────────────────────────────────────────────────

if [[ "$MODE" == "ban" ]]; then
  RESOLVED_STEAMID=""
  RESOLVED_NAME=""
  resolve_player

  if [[ -z "$REASON" ]]; then
    printf "Ban reason (optional): "
    read -r REASON
  fi

  echo "==> Banning ${RESOLVED_NAME} (${RESOLVED_STEAMID})${REASON:+ — reason: ${REASON}}..."
  rcon_cmd "banid ${RESOLVED_STEAMID} \"${RESOLVED_NAME}\" \"${REASON}\""
  rcon_cmd "server.writecfg"
  echo "==> Done. Ban persisted to server config."
  exit 0
fi

# ── admin ─────────────────────────────────────────────────────────────────────

if [[ "$MODE" == "admin" ]]; then
  RESOLVED_STEAMID=""
  RESOLVED_NAME=""
  resolve_player

  echo "==> Granting admin (ownerid) to ${RESOLVED_NAME} (${RESOLVED_STEAMID})..."
  rcon_cmd "ownerid ${RESOLVED_STEAMID} \"${RESOLVED_NAME}\""
  rcon_cmd "server.writecfg"
  echo "==> Done. ${RESOLVED_NAME} is now an admin."
  exit 0
fi

# ── mod ───────────────────────────────────────────────────────────────────────

if [[ "$MODE" == "mod" ]]; then
  RESOLVED_STEAMID=""
  RESOLVED_NAME=""
  resolve_player

  echo "==> Granting moderator to ${RESOLVED_NAME} (${RESOLVED_STEAMID})..."
  rcon_cmd "moderatorid ${RESOLVED_STEAMID} \"${RESOLVED_NAME}\""
  rcon_cmd "server.writecfg"
  echo "==> Done. ${RESOLVED_NAME} is now a moderator."
  exit 0
fi

# ── whitelist ─────────────────────────────────────────────────────────────────

if [[ "$MODE" == "whitelist" ]]; then
  RESOLVED_STEAMID=""
  RESOLVED_NAME=""
  resolve_player

  echo "==> Granting whitelist.allow to ${RESOLVED_NAME} (${RESOLVED_STEAMID})..."
  rcon_cmd "oxide.grant user ${RESOLVED_STEAMID} whitelist.allow"
  echo "==> Done. ${RESOLVED_NAME} can now join the whitelisted server."
  exit 0
fi

# ── reboot / wipe ─────────────────────────────────────────────────────────────

if [[ "$MODE" == "wipe" ]]; then
  LABEL="MAP WIPE"
  COLOR="red"
else
  LABEL="SERVER RESTART"
  COLOR="orange"
fi

echo "=== Rust ${MODE} ==="
echo "Target: ${RCON_HOST}:${RCON_PORT} | Context: ${KUBE_CONTEXT} | NS: ${NAMESPACE}"
echo ""

say "<color=${COLOR}>${LABEL} IN 5 MINUTES</color>"
sleep 180

say "<color=${COLOR}>${LABEL} IN 2 MINUTES</color>"
sleep 60

say "<color=red>${LABEL} IN 1 MINUTE — please find shelter</color>"
sleep 30

say "<color=red>${LABEL} IN 30 SECONDS</color>"
sleep 20

say "<color=red>${LABEL} IN 10 SECONDS</color>"
sleep 5

say "<color=red>${LABEL} IN 5 SECONDS</color>"
sleep 5

if [[ "$MODE" == "wipe" ]]; then
  POD=$(get_pod)
  if [[ -z "$POD" ]]; then
    echo "[wipe] ERROR: could not find running pod for release ${RELEASE}"
    exit 1
  fi

  SAVE_DIR="/steamcmd/rust/server/${SERVER_IDENTITY}"
  echo "[wipe] Deleting save files from ${POD}:${SAVE_DIR}/ ..."

  # Delete .sav and .sav.N files — keep .map (terrain cache, expensive to regenerate)
  kubectl --context "${KUBE_CONTEXT}" exec -n "${NAMESPACE}" "${POD}" -- \
    bash -c "rm -fv ${SAVE_DIR}/proceduralmap.*.sav ${SAVE_DIR}/proceduralmap.*.sav.[0-9]*"

  echo "[wipe] Save files deleted."
else
  echo "[reboot] Saving world..."
  rcon_cmd "server.save" || true
fi

echo "[${MODE}] Restarting pod..."
kubectl --context "${KUBE_CONTEXT}" rollout restart deployment/"${RELEASE}" -n "${NAMESPACE}"

echo "[${MODE}] Done. Monitor with:"
echo "  kubectl --context ${KUBE_CONTEXT} rollout status deployment/${RELEASE} -n ${NAMESPACE}"
echo "  make logs KUBE_CONTEXT=${KUBE_CONTEXT}"
