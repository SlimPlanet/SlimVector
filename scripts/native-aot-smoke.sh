#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) runtime_id="osx-arm64" ;;
  Darwin-x86_64) runtime_id="osx-x64" ;;
  Linux-x86_64) runtime_id="linux-x64" ;;
  Linux-aarch64|Linux-arm64) runtime_id="linux-arm64" ;;
  *) echo "Unsupported Native AOT smoke-test platform." >&2; exit 2 ;;
esac

publish_path="$repo_root/artifacts/aot-smoke-$runtime_id"
smoke_root="$(mktemp -d "${TMPDIR:-/tmp}/slimvector-aot-smoke.XXXXXX")"
smoke_port="${SLIMVECTOR_SMOKE_PORT:-18080}"
process_id=""

cleanup() {
  if [[ -n "$process_id" ]] && kill -0 "$process_id" 2>/dev/null; then
    kill "$process_id"
    wait "$process_id" || true
  fi
  rm -rf "$smoke_root"
}
trap cleanup EXIT

dotnet publish "$repo_root/src/SlimVector.Api/SlimVector.Api.csproj" \
  -c Release \
  -r "$runtime_id" \
  --self-contained true \
  -o "$publish_path"

ASPNETCORE_URLS="http://127.0.0.1:$smoke_port" \
Storage__Path="$smoke_root/data" \
Storage__FlushToDisk=true \
"$publish_path/SlimVector.Api" >"$smoke_root/server.log" 2>&1 &
process_id="$!"

for _ in {1..100}; do
  if curl -fsS "http://127.0.0.1:$smoke_port/health/ready" >"$smoke_root/ready.json" 2>/dev/null; then
    break
  fi
  if ! kill -0 "$process_id" 2>/dev/null; then
    sed -n '1,240p' "$smoke_root/server.log" >&2
    exit 1
  fi
  sleep 0.1
done
curl -fsS "http://127.0.0.1:$smoke_port/health/ready" | rg -q 'ready'

curl -fsS -X POST "http://127.0.0.1:$smoke_port/api/v1/collections" \
  -H 'Content-Type: application/json' \
  -d '{"name":"smoke","dimension":2,"metric":"cosine"}' >"$smoke_root/collection.json"
curl -fsS -X POST "http://127.0.0.1:$smoke_port/api/v1/collections/smoke/documents/add" \
  -H 'Content-Type: application/json' \
  -d '{"documents":[{"id":"native","text":"native aot vector database","vector":[1,0],"metadata":{"runtime":"aot"}}]}' \
  >"$smoke_root/add.json"
curl -fsS -X POST "http://127.0.0.1:$smoke_port/api/v1/collections/smoke/documents/query" \
  -H 'Content-Type: application/json' \
  -d '{"mode":"hybrid","text":"native vector","vector":[1,0],"limit":1}' \
  | rg -q '"id":"native"'
curl -fsS "http://127.0.0.1:$smoke_port/metrics" | rg -q 'slimvector_search_requests_total 1'

echo "Native AOT smoke test passed for $runtime_id."
