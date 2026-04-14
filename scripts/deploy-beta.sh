#!/bin/bash
# Deploy to dal2-beta cluster via Helm
set -euo pipefail

CONTEXT="dal2-beta"
NAMESPACE="penguin-rust"
RELEASE="rust-server"
HELM_CHART="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/k8s/helm/rust-server"
VALUES_FILE="${HELM_CHART}/values-beta.yaml"
TAG=""
DRY_RUN=false
ROLLBACK=false

usage() {
    echo "Usage: $0 [--tag beta-<epoch>] [--dry-run] [--rollback]"
    exit 1
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag)      TAG="$2"; shift ;;
        --dry-run)  DRY_RUN=true ;;
        --rollback) ROLLBACK=true ;;
        --help)     usage ;;
        *) echo "Unknown flag: $1"; usage ;;
    esac
    shift
done

if [ "${ROLLBACK}" = "true" ]; then
    echo "==> Rolling back ${RELEASE} in ${NAMESPACE}"
    helm rollback "${RELEASE}" 1 --kube-context "${CONTEXT}" --namespace "${NAMESPACE}"
    kubectl --context "${CONTEXT}" rollout status deployment/rust-server -n "${NAMESPACE}" --timeout=120s
    exit 0
fi

# If no tag specified, find the latest CI-built image tag from GHCR
if [ -z "${TAG}" ]; then
    echo "==> No --tag specified; using 'latest' (consider pinning to a specific epoch tag)"
    TAG="latest"
fi

DRY_RUN_FLAG=""
[ "${DRY_RUN}" = "true" ] && DRY_RUN_FLAG="--dry-run"

echo "==> Deploying ${RELEASE} to ${CONTEXT}/${NAMESPACE} (image tag: ${TAG})"

kubectl --context "${CONTEXT}" create namespace "${NAMESPACE}" --dry-run=client -o yaml \
    | kubectl --context "${CONTEXT}" apply -f -

helm upgrade --install "${RELEASE}" "${HELM_CHART}" \
    --kube-context "${CONTEXT}" \
    --namespace "${NAMESPACE}" \
    --values "${VALUES_FILE}" \
    --set image.tag="${TAG}" \
    --set namespace="${NAMESPACE}" \
    --wait --timeout 15m \
    ${DRY_RUN_FLAG}

if [ "${DRY_RUN}" = "false" ]; then
    echo "==> Pod status"
    kubectl --context "${CONTEXT}" get pods -n "${NAMESPACE}"

    echo "==> Service (LoadBalancer IP may take a moment to provision)"
    kubectl --context "${CONTEXT}" get svc -n "${NAMESPACE}"
fi
