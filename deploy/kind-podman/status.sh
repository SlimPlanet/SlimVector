#!/usr/bin/env bash

set -euo pipefail

cluster_name="${SLIMVECTOR_KIND_CLUSTER:-slimvector-geo}"
kube_context="kind-${cluster_name}"

kubectl --context "${kube_context}" get nodes \
    --label-columns slimvector.dev/member-index
kubectl --context "${kube_context}" get pods \
    --all-namespaces \
    --selector app.kubernetes.io/name=slimvector \
    --output wide

echo
echo "Santé eu-west :"
curl --fail --silent --show-error http://127.0.0.1:8090/health/ready
echo
echo "Santé eu-central :"
curl --fail --silent --show-error http://127.0.0.1:8091/health/ready
echo
