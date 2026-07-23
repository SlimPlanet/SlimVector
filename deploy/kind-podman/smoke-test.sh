#!/usr/bin/env bash

set -euo pipefail

primary_endpoint="${SLIMVECTOR_PRIMARY_ENDPOINT:-http://127.0.0.1:8090}"
secondary_endpoint="${SLIMVECTOR_SECONDARY_ENDPOINT:-http://127.0.0.1:8091}"
collection_name="kind-geo-smoke"

curl --fail-with-body --silent --show-error --location \
    --request POST \
    --header 'Content-Type: application/json' \
    --data "{\"name\":\"${collection_name}\",\"dimension\":3,\"metric\":\"cosine\"}" \
    "${primary_endpoint}/api/v1/collections/get-or-create" >/dev/null

curl --fail-with-body --silent --show-error --location \
    --request POST \
    --header 'Content-Type: application/json' \
    --data '{"atomic":true,"documents":[{"id":"geo-check","text":"réplication locale Kind Podman","vector":[1,0,0],"metadata":{"source":"kind-podman"}}]}' \
    "${primary_endpoint}/api/v1/collections/${collection_name}/documents/upsert" >/dev/null

attempt=1
while [[ "${attempt}" -le 60 ]]; do
    response="$(curl --fail-with-body --silent --show-error --location \
        "${secondary_endpoint}/api/v1/collections/${collection_name}/documents/count" 2>/dev/null || true)"
    if [[ "${response}" == *'"count":1'* ]]; then
        echo "Géoréplication validée : le document est présent dans eu-central."
        exit 0
    fi

    sleep 1
    attempt=$((attempt + 1))
done

echo "Le document n'a pas atteint eu-central dans le délai imparti." >&2
exit 1
