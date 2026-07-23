#!/usr/bin/env bash

set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_directory}/../.." && pwd)"
cluster_name="${SLIMVECTOR_KIND_CLUSTER:-slimvector-geo}"
kube_context="kind-${cluster_name}"
image_name="${SLIMVECTOR_IMAGE:-localhost/slimvector:kube-local}"
build_image="${SLIMVECTOR_BUILD_IMAGE:-true}"
rollout_timeout="${SLIMVECTOR_ROLLOUT_TIMEOUT:-5m}"
image_archive_directory=""

export KIND_EXPERIMENTAL_PROVIDER=podman

require_command() {
    if ! command -v "$1" >/dev/null 2>&1; then
        echo "Commande requise introuvable : $1" >&2
        exit 1
    fi
}

kube() {
    kubectl --context "${kube_context}" "$@"
}

cleanup_image_archive() {
    if [[ -n "${image_archive_directory}" ]]; then
        if [[ -f "${image_archive_directory}/slimvector.oci.tar" ]]; then
            unlink "${image_archive_directory}/slimvector.oci.tar"
        fi
        if [[ -d "${image_archive_directory}" ]]; then
            rmdir "${image_archive_directory}"
        fi
        image_archive_directory=""
    fi
}

trap cleanup_image_archive EXIT

secret_value() {
    namespace="$1"
    key="$2"
    encoded="$(kube --namespace "${namespace}" get secret slimvector-secrets \
        --output "jsonpath={.data.${key}}" 2>/dev/null)"
    printf '%s' "${encoded}" | openssl base64 -d -A
}

apply_secrets() {
    source_namespace=""
    if kube --namespace slimvector-eu-west get secret slimvector-secrets >/dev/null 2>&1; then
        source_namespace="slimvector-eu-west"
    elif kube --namespace slimvector-eu-central get secret slimvector-secrets >/dev/null 2>&1; then
        source_namespace="slimvector-eu-central"
    fi

    if [[ -n "${source_namespace}" ]]; then
        geo_secret="$(secret_value "${source_namespace}" geo-shared-secret)"
        admin_key="$(secret_value "${source_namespace}" admin-api-key)"
    else
        geo_secret="$(openssl rand -hex 32)"
        admin_key="$(openssl rand -hex 32)"
    fi

    for namespace in slimvector-eu-west slimvector-eu-central; do
        kube --namespace "${namespace}" create secret generic slimvector-secrets \
            --from-literal="geo-shared-secret=${geo_secret}" \
            --from-literal="admin-api-key=${admin_key}" \
            --dry-run=client \
            --output yaml |
            kube apply --filename -
    done
}

apply_member_config() {
    namespace="$1"
    raft_port="$2"
    api_port="$3"
    public_port_zero="$4"
    public_port_one="$5"
    public_port_two="$6"

    kube --namespace "${namespace}" create configmap slimvector-members \
        "--from-literal=Raft__Members__0=http://${worker_ip_zero}:${raft_port}" \
        "--from-literal=Raft__Members__1=http://${worker_ip_one}:${raft_port}" \
        "--from-literal=Raft__Members__2=http://${worker_ip_two}:${raft_port}" \
        "--from-literal=Raft__MemberApiEndpoints__0=http://127.0.0.1:${public_port_zero}" \
        "--from-literal=Raft__MemberApiEndpoints__1=http://127.0.0.1:${public_port_one}" \
        "--from-literal=Raft__MemberApiEndpoints__2=http://127.0.0.1:${public_port_two}" \
        "--from-literal=Raft__MemberNodeIds__0=${worker_ip_zero}" \
        "--from-literal=Raft__MemberNodeIds__1=${worker_ip_one}" \
        "--from-literal=Raft__MemberNodeIds__2=${worker_ip_two}" \
        "--from-literal=Raft__MemberInternalEndpoints__0=http://${worker_ip_zero}:${api_port}" \
        "--from-literal=Raft__MemberInternalEndpoints__1=http://${worker_ip_one}:${api_port}" \
        "--from-literal=Raft__MemberInternalEndpoints__2=http://${worker_ip_two}:${api_port}" \
        "--from-literal=Raft__MemberZones__0=${worker_ip_zero}" \
        "--from-literal=Raft__MemberZones__1=${worker_ip_one}" \
        "--from-literal=Raft__MemberZones__2=${worker_ip_two}" \
        "--from-literal=Raft__MemberCapacityBytes__0=5368709120" \
        "--from-literal=Raft__MemberCapacityBytes__1=5368709120" \
        "--from-literal=Raft__MemberCapacityBytes__2=5368709120" \
        "--from-literal=GeoReplication__SecondaryEndpoint=http://${worker_ip_zero}:8180" \
        --dry-run=client \
        --output yaml |
        kube apply --filename -
}

require_command kind
require_command kubectl
require_command openssl
require_command podman

if ! podman info >/dev/null 2>&1; then
    echo "Podman n'est pas démarré. Lancez d'abord : podman machine start" >&2
    exit 1
fi

if ! kind get clusters 2>/dev/null | grep -Fx "${cluster_name}" >/dev/null; then
    echo "Création du cluster Kind '${cluster_name}' avec Podman..."
    kind create cluster \
        --name "${cluster_name}" \
        --config "${script_directory}/kind-config.yaml"
else
    echo "Réutilisation du cluster Kind '${cluster_name}'."
fi

worker_names="$(kind get nodes --name "${cluster_name}" | grep -- '-worker' | sort)"
worker_count="$(printf '%s\n' "${worker_names}" | sed '/^$/d' | wc -l | tr -d ' ')"
if [[ "${worker_count}" != "3" ]]; then
    echo "Trois workers Kind sont requis, ${worker_count} détecté(s)." >&2
    exit 1
fi

worker_zero="$(printf '%s\n' "${worker_names}" | sed -n '1p')"
worker_one="$(printf '%s\n' "${worker_names}" | sed -n '2p')"
worker_two="$(printf '%s\n' "${worker_names}" | sed -n '3p')"

kube label node "${worker_zero}" slimvector.dev/worker=true slimvector.dev/member-index=0 --overwrite
kube label node "${worker_one}" slimvector.dev/worker=true slimvector.dev/member-index=1 --overwrite
kube label node "${worker_two}" slimvector.dev/worker=true slimvector.dev/member-index=2 --overwrite

worker_ip_zero="$(kube get node "${worker_zero}" --output 'jsonpath={.status.addresses[?(@.type=="InternalIP")].address}')"
worker_ip_one="$(kube get node "${worker_one}" --output 'jsonpath={.status.addresses[?(@.type=="InternalIP")].address}')"
worker_ip_two="$(kube get node "${worker_two}" --output 'jsonpath={.status.addresses[?(@.type=="InternalIP")].address}')"

for worker in ${worker_names}; do
    podman exec "${worker}" mkdir -p /var/lib/slimvector/eu-west /var/lib/slimvector/eu-central
    podman exec "${worker}" chown -R 1654:1654 /var/lib/slimvector
done

if [[ "${build_image}" == "true" ]]; then
    echo "Construction de l'image ${image_name}..."
    podman build \
        --file "${repository_root}/Dockerfile" \
        --tag "${image_name}" \
        "${repository_root}"
elif ! podman image exists "${image_name}"; then
    echo "Téléchargement de l'image ${image_name}..."
    podman pull "${image_name}"
fi

echo "Chargement de l'image dans les quatre nœuds Kind..."
image_archive_directory="$(mktemp -d "${TMPDIR:-/tmp}/slimvector-kind.XXXXXX")"
podman save \
    --format oci-archive \
    --output "${image_archive_directory}/slimvector.oci.tar" \
    "${image_name}"
kind load image-archive \
    "${image_archive_directory}/slimvector.oci.tar" \
    --name "${cluster_name}"
cleanup_image_archive

kube apply --filename "${script_directory}/manifests/platform.yaml"
apply_secrets
apply_member_config slimvector-eu-west 3262 8080 8090 8092 8093
apply_member_config slimvector-eu-central 3362 8180 8091 8192 8193

kube apply --filename "${script_directory}/manifests/primary.yaml"
kube apply --filename "${script_directory}/manifests/secondary.yaml"
kube --namespace slimvector-eu-west set image daemonset/slimvector "slimvector=${image_name}"
kube --namespace slimvector-eu-central set image daemonset/slimvector "slimvector=${image_name}"

# Recharge aussi une image reconstruite sous le même tag.
kube --namespace slimvector-eu-west rollout restart daemonset/slimvector
kube --namespace slimvector-eu-central rollout restart daemonset/slimvector
kube --namespace slimvector-eu-west rollout status daemonset/slimvector --timeout "${rollout_timeout}"
kube --namespace slimvector-eu-central rollout status daemonset/slimvector --timeout "${rollout_timeout}"

echo
echo "SlimVector est prêt :"
echo "  primaire eu-west    : http://127.0.0.1:8090"
echo "  secondaire eu-central : http://127.0.0.1:8091"
echo "  nœuds eu-west       : 8090, 8092, 8093"
echo "  nœuds eu-central    : 8091, 8192, 8193"
echo
echo "Validation de la géoréplication : ${script_directory}/smoke-test.sh"
