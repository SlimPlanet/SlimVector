#!/usr/bin/env bash

set -euo pipefail

cluster_name="${SLIMVECTOR_KIND_CLUSTER:-slimvector-geo}"
export KIND_EXPERIMENTAL_PROVIDER=podman

if ! command -v kind >/dev/null 2>&1; then
    echo "Commande requise introuvable : kind" >&2
    exit 1
fi

echo "Suppression du cluster '${cluster_name}' et de toutes ses données locales..."
kind delete cluster --name "${cluster_name}"
