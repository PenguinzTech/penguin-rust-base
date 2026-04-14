#!/bin/bash
# Deploy to local MicroK8s (alpha) via Kustomize
set -euo pipefail

CONTEXT="local-alpha"
NAMESPACE="penguin-rust"
IMAGE="localhost:32000/penguin-rust-base"
TAG="latest"
DRY_RUN=false
SKIP_BUILD=false

usage() {
    echo "Usage: $0 [--skip-build] [--dry-run] [--tag TAG]"
    exit 1
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --skip-build) SKIP_BUILD=true ;;
        --dry-run)    DRY_RUN=true ;;
        --tag)        TAG="$2"; shift ;;
        --help)       usage ;;
        *) echo "Unknown flag: $1"; usage ;;
    esac
    shift
done

if ! microk8s status --format short 2>/dev/null | grep -q "microk8s is running"; then
    echo "ERROR: MicroK8s is not running. Start it with: microk8s start" >&2
    exit 1
fi

if ! microk8s status --format short 2>/dev/null | grep -q "registry: enabled"; then
    echo "ERROR: MicroK8s registry addon not enabled. Run: microk8s enable registry" >&2
    exit 1
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ "${SKIP_BUILD}" = "false" ]; then
    echo "==> Building image ${IMAGE}:${TAG}"
    docker build -t "${IMAGE}:${TAG}" "${REPO_ROOT}/docker/"
    echo "==> Pushing to MicroK8s registry"
    docker push "${IMAGE}:${TAG}"
fi

echo "==> Creating namespace ${NAMESPACE}"
kubectl --context "${CONTEXT}" create namespace "${NAMESPACE}" --dry-run=client -o yaml \
    | kubectl --context "${CONTEXT}" apply -f -

if [ "${DRY_RUN}" = "true" ]; then
    echo "==> Dry run — manifests that would be applied:"
    kubectl kustomize "${REPO_ROOT}/k8s/kustomize/overlays/alpha"
    exit 0
fi

echo "==> Applying Kustomize overlay (alpha)"
kubectl apply --context "${CONTEXT}" -n "${NAMESPACE}" \
    -k "${REPO_ROOT}/k8s/kustomize/overlays/alpha"

echo "==> Waiting for rollout"
kubectl --context "${CONTEXT}" rollout status deployment/rust-server -n "${NAMESPACE}" --timeout=900s

echo "==> Pod status"
kubectl --context "${CONTEXT}" get pods -n "${NAMESPACE}"

echo ""
echo "Server ports:"
echo "  Game: localhost:30015 (UDP + TCP)"
echo "  RCON: localhost:30016 (TCP)"
