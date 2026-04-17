#!/usr/bin/env bash
# scripts/admin.sh — Rust server admin operations
#
# MODE=reboot       (default) warn players → save → restart pod
# MODE=wipe                   warn players → delete .sav files (no save) → restart pod
# MODE=force-restart          immediate pod restart; no RCON needed
# MODE=message                broadcast MESSAGE to all players via RCON
# MODE=save                   force a world save via RCON
#
# Usage:
#   RCON_PASSWORD=<pw> make reboot
#   RCON_PASSWORD=<pw> make wipe
#   make force-restart
#   RCON_PASSWORD=<pw> MESSAGE="text" make message
#   RCON_PASSWORD=<pw> make save
#   MODE=reboot RCON_PASSWORD=<pw> RCON_HOST=<ip> ./scripts/admin.sh

set -euo pipefail

RCON_HOST="${RCON_HOST:-}"
RCON_PORT="${RCON_PORT:-28016}"
RCON_PASSWORD="${RCON_PASSWORD:-}"
KUBE_CONTEXT="${KUBE_CONTEXT:-dal2-beta}"
NAMESPACE="${NAMESPACE:-penguin-rust}"
RELEASE="${RELEASE:-rust-server}"
SERVER_IDENTITY="${SERVER_IDENTITY:-rust_server}"
MESSAGE="${MESSAGE:-}"
MODE="${MODE:-reboot}"   # reboot | wipe | force-restart | message | save

# ── Validation ────────────────────────────────────────────────────────────────

if [[ "$MODE" != "reboot" && "$MODE" != "wipe" && "$MODE" != "force-restart" && "$MODE" != "message" && "$MODE" != "save" ]]; then
  echo "ERROR: MODE must be 'reboot', 'wipe', 'force-restart', 'message', or 'save' (got: '$MODE')"
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

# ── RCON helper ───────────────────────────────────────────────────────────────

rcon_cmd() {
  local cmd="$1"
  python3 - <<PYEOF
import asyncio, json, sys
try:
    import websockets
except ImportError:
    sys.exit("websockets package required: pip3 install websockets")

async def send():
    uri = "ws://${RCON_HOST}:${RCON_PORT}/${RCON_PASSWORD}"
    async with websockets.connect(uri, open_timeout=5) as ws:
        payload = json.dumps({"Identifier": 1, "Message": "$cmd", "Name": "WebRcon"})
        await ws.send(payload)
        resp = await asyncio.wait_for(ws.recv(), timeout=5)
        data = json.loads(resp)
        if data.get("Message"):
            print(data["Message"])

asyncio.run(send())
PYEOF
}

say() {
  local msg="$1"
  echo "[${MODE}] Sending: $msg"
  rcon_cmd "say $msg" 2>/dev/null || true
}

# ── Pod lookup helper ─────────────────────────────────────────────────────────

get_pod() {
  kubectl --context "${KUBE_CONTEXT}" get pods -n "${NAMESPACE}" \
    -l "app.kubernetes.io/instance=${RELEASE}" \
    --no-headers -o custom-columns=NAME:.metadata.name | head -1
}

# ── Message ───────────────────────────────────────────────────────────────────

if [[ "$MODE" == "message" ]]; then
  if [[ -z "$MESSAGE" ]]; then
    printf "Admin message text: "
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

# ── Save ──────────────────────────────────────────────────────────────────────

if [[ "$MODE" == "save" ]]; then
  echo "==> Saving world..."
  rcon_cmd "server.save"
  echo "==> Done."
  exit 0
fi

# ── Force-restart (no RCON) ───────────────────────────────────────────────────

if [[ "$MODE" == "force-restart" ]]; then
  echo "=== Force-restart: ${RELEASE} in ${KUBE_CONTEXT}/${NAMESPACE} ==="
  kubectl --context "${KUBE_CONTEXT}" rollout restart deployment/"${RELEASE}" -n "${NAMESPACE}"
  echo "Pod restart triggered. Monitor with:"
  echo "  kubectl --context ${KUBE_CONTEXT} rollout status deployment/${RELEASE} -n ${NAMESPACE}"
  echo "  make logs KUBE_CONTEXT=${KUBE_CONTEXT}"
  exit 0
fi

# ── Reboot / wipe ─────────────────────────────────────────────────────────────

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
