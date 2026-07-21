# SlimVector user guide

This guide takes an application developer or operator from a local database to a durable production deployment. It uses JSON for readable examples; the same collection and document operations are available as MessagePack and through the typed .NET client.

## Choose how to use SlimVector

| Need | Recommended surface |
| --- | --- |
| Explore local documents without writing embedding code | [SlimVector Studio](studio.md) |
| Build an application against an HTTP database | SlimVector API with JSON or MessagePack |
| Build a .NET application | `SlimVector.Client` |
| Run on one durable host | Single-node mode with scheduled backups |
| Survive a server failure or add disk by adding nodes | Shared-nothing cluster with RF3 data groups |
| Maintain a disaster-recovery copy in another region | Geographic replication between separate deployments |

Studio and the standalone API are different hosts. Studio embeds a single-node database and an ingestion UI; the API is the deployment surface for applications and clusters.

## Requirements

- .NET SDK 10.0.300 or a later patch in the 10.0 feature band, or Docker with Compose.
- A local filesystem supporting atomic rename for `Storage:Path`.
- Client-generated vectors whose dimension exactly matches their collection. SlimVector does not call a remote embedding API.

For the included multilingual document pipeline, the first embedding operation needs network access to download a pinned 384-dimensional ONNX model. The cache works offline afterward.

## Start a local database

From the repository root:

```bash
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/SlimVector.Api -c Release
```

Or start the Native AOT container with a named durable volume:

```bash
docker compose -f compose.single-node.yml up --build -d
```

Confirm that the process is ready:

```bash
curl -fsS http://localhost:8080/health/ready
curl -fsS http://localhost:8080/openapi/v1.json > /tmp/slimvector-openapi.json
```

`/health/live` answers when the HTTP process is alive. Route traffic only when `/health/ready` returns 200.

## Create a collection

A collection fixes the vector dimension and distance metric. These cannot be changed later; create another collection when the embedding model or metric changes.

```bash
curl -fsS -X POST http://localhost:8080/api/v1/collections \
  -H 'Content-Type: application/json' \
  -d '{
    "name": "articles",
    "dimension": 3,
    "metric": "cosine",
    "vectorIndex": { "kind": "auto" }
  }'
```

`cosine` is the default metric and `auto` is the default index kind. Use `get-or-create` when several application instances may initialize the same compatible collection:

```bash
curl -fsS -X POST http://localhost:8080/api/v1/collections/get-or-create \
  -H 'Content-Type: application/json' \
  -d '{"name":"articles","dimension":3,"metric":"cosine"}'
```

An existing collection with incompatible settings is rejected instead of being silently changed.

## Add and change documents

Documents group the id, text, vector, and typed metadata naturally. `add` rejects an existing id; `upsert` creates or replaces it.

```bash
curl -fsS -X POST http://localhost:8080/api/v1/collections/articles/documents/add \
  -H 'Content-Type: application/json' \
  -d '{
    "atomic": true,
    "documents": [
      {
        "id": "dotnet",
        "text": "SlimVector is a distributed vector database for .NET.",
        "vector": [1.0, 0.0, 0.0],
        "metadata": {
          "published": true,
          "year": 2026,
          "tags": ["dotnet", "database"],
          "publishedAt": {"$date": "2026-07-21T10:00:00Z"},
          "sourceId": {"$guid": "44e7c508-38f4-42cf-a9d8-f0cdbc3903db"}
        }
      },
      {
        "id": "search",
        "text": "Hybrid retrieval combines vector similarity and BM25 ranks.",
        "vector": [0.8, 0.2, 0.0],
        "metadata": {"published": true, "year": 2025, "tags": ["search"]}
      }
    ]
  }'
```

Metadata accepts null, strings, booleans, integers, floating-point numbers, tagged dates/UUIDs, and homogeneous simple arrays. An update replaces each supplied field; supplying `metadata` replaces the complete metadata map for that document.

```bash
curl -fsS -X PATCH http://localhost:8080/api/v1/collections/articles/documents \
  -H 'Content-Type: application/json' \
  -d '{"documents":[{"id":"search","metadata":{"published":true,"year":2026}}]}'
```

`atomic` defaults to `true`. In a distributed collection, an atomic batch must route to one physical data group; a cross-group atomic request returns `cross_shard_atomic_unsupported`. Set `atomic:false` to partition the batch and inspect every item in the result. Retry only failed items and keep stable document ids so upsert remains idempotent.

## Search

SlimVector supports four modes.

### Vector search

```bash
curl -fsS -X POST http://localhost:8080/api/v1/collections/articles/documents/query \
  -H 'Content-Type: application/json' \
  -d '{"mode":"vector","vector":[1,0,0],"limit":5,"include":["text","metadata","scores"]}'
```

### Text search

```bash
curl -fsS -X POST http://localhost:8080/api/v1/collections/articles/documents/query \
  -H 'Content-Type: application/json' \
  -d '{"mode":"text","text":"distributed database","limit":5}'
```

### Hybrid search

Hybrid search ranks vector and BM25 results independently and combines their ranks. The weights do not add incompatible raw scores.

```bash
curl -fsS -X POST http://localhost:8080/api/v1/collections/articles/documents/query \
  -H 'Content-Type: application/json' \
  -d '{
    "mode":"hybrid",
    "text":"vector database",
    "vector":[1,0,0],
    "vectorWeight":0.6,
    "textWeight":0.4,
    "limit":5,
    "filter":{"operator":"greaterThanOrEqual","field":"year","value":2025}
  }'
```

### Metadata-only search

```bash
curl -fsS -X POST http://localhost:8080/api/v1/collections/articles/documents/query \
  -H 'Content-Type: application/json' \
  -d '{
    "mode":"metadata",
    "limit":100,
    "filter":{"operator":"in","field":"tags","values":["dotnet","search"]},
    "include":["text","metadata"]
  }'
```

Filters support `equal`, `notEqual`, comparisons, `in`, `exists`, `and`, `or`, and `not`. Arrays are indexed by their scalar members. See [HTTP API](api.md#query) for compound syntax.

### Choose consistency

| Value | Behavior | Typical use |
| --- | --- | --- |
| `leader` | Reads current leader state after its leader barrier | Default application reads |
| `linearizable` | Uses a quorum-backed read barrier | Read-after-write workflows requiring the strongest ordering |
| `stale` | May use a healthy follower without a quorum barrier | Lower-latency reads that tolerate replication lag |

Single-node mode preserves the same API but all three read the same local committed state.

## List, paginate, count, and delete

```bash
curl -fsS 'http://localhost:8080/api/v1/collections/articles/documents?limit=100'
curl -fsS http://localhost:8080/api/v1/collections/articles/documents/count
curl -fsS -X POST http://localhost:8080/api/v1/collections/articles/documents/delete \
  -H 'Content-Type: application/json' \
  -d '{"ids":["search"],"atomic":true}'
```

For the next page, pass the response's opaque token as `continuationToken`. Do not decode or persist it as a permanent cursor. A placement epoch change intentionally invalidates the token; restart pagination after that error. `offset` exists for compatibility but is capped at 10,000 by default and is unsuitable for deep distributed pagination.

## Choose an index

Start with `auto` unless you have measured evidence for a fixed choice.

| Choice | Use when |
| --- | --- |
| `flat` | Exact recall or a small collection is more important than scan cost |
| `hnsw` | A medium, read-heavy collection fits in memory |
| `ivfFlat` | A large collection needs trained partitioning while retaining full vectors |
| `ivfPq` | Memory pressure justifies product quantization and exact reranking |
| `diskAnn` | The vector set is larger than the intended RAM budget and local SSD is available |
| `auto` | SlimVector should evaluate size, memory, churn, recall, and measured gain before switching generations |

Approximate-index settings trade latency for recall. Validate them against Flat with representative vectors and queries; the benchmark runner produces recall/latency curves at qualified thresholds. See [indexes](indexes.md) and [testing and benchmarks](testing.md#benchmarks).

## Use the .NET client

Reference `SlimVector.Client`, provide an `HttpClient` with the server base address, and reuse the client rather than creating it per request.

```csharp
using System.Text.Json;
using SlimVector.Client;
using SlimVector.Domain;

HttpClient http = new() { BaseAddress = new Uri("http://localhost:8080") };
SlimVectorClient client = new(http);

await client.GetOrCreateCollectionAsync(new CreateCollectionRequest
{
    Name = "articles",
    Dimension = 3,
    Metric = DistanceMetric.Cosine,
});

await client.UpsertDocumentsAsync("articles",
[
    new SlimVectorDocument
    {
        Id = "client-example",
        Text = "Written by the typed client",
        Vector = [1, 0, 0],
        Metadata = new()
        {
            ["year"] = JsonSerializer.SerializeToElement(2026),
        },
    },
]);

SlimVectorQueryResult result = await client.QueryAsync("articles", new SlimVectorQuery
{
    Mode = SearchMode.Hybrid,
    Text = "typed client",
    Vector = [1, 0, 0],
    Limit = 10,
});
```

Select MessagePack once for vector-heavy traffic without changing DTOs:

```csharp
SlimVectorClient client = new(http, SlimVectorWireFormat.MessagePack);
```

JSON remains the interoperability default. MessagePack uses `application/vnd.msgpack`, camel-case map keys, string enums/dates, native float arrays, generated contracts, and the untrusted-data security profile. Administrator client operations remain JSON. The current typed client targets the default `/api/v1` prefix.

## Handle errors and load shedding

Errors use Problem Details with stable `code` and `traceId` fields. Log the code, HTTP status, and trace id; do not parse the human-readable message.

- Retry 429 responses only after `Retry-After`. `X-SlimVector-RateLimit-Kind=contractual` means a configured quota; `congestion` means runtime pressure.
- Retry transient 503 quorum/election failures with bounded exponential backoff and jitter.
- Preserve the HTTP method when following a 307 leader redirect.
- Fix 400 validation, dimension, filter, or payload errors before retrying.
- Treat storage corruption and geo divergence as operator incidents, not ordinary retry conditions.

## Make the local deployment durable

Set a durable absolute path, keep fsync enabled, and enable administrator endpoints plus encrypted backups through secret-backed environment variables:

```bash
Storage__Path=/var/lib/slimvector \
Storage__FlushToDisk=true \
Api__AdminEndpointsEnabled=true \
Api__AdminApiKey="$SLIMVECTOR_ADMIN_KEY" \
Backup__Enabled=true \
Backup__Path=/var/backups/slimvector \
Backup__Encrypt=true \
Backup__EncryptionKey="$SLIMVECTOR_BACKUP_KEY" \
ASPNETCORE_URLS=http://0.0.0.0:8080 \
dotnet run --project src/SlimVector.Api -c Release
```

The live data and backup paths must be separate durable volumes. Generate the backup key with `openssl rand -base64 32`, retain it while any dependent backup exists, and perform restore drills. Backups are consistent per collection, not a cross-collection transaction.

## Move to a cluster

Use cluster mode when one host failure must not interrupt the service or when capacity must grow with added local disks. The initial three catalog voters also host RF3 data groups. Later API/data nodes maintain a persistent catalog cache and host only their assigned groups.

```bash
export SLIMVECTOR_ADMIN_KEY='replace-with-a-secret-at-least-32-characters'
docker compose -f compose.cluster.yml up --build -d
```

Every API node accepts requests. Adding a node registers its capacity but does not move data automatically: inspect and explicitly approve the capacity-aware rebalance plan. With homogeneous nodes and RF3, useful capacity is approximately `(total disk × 0.85) / 3`; three additional equal nodes roughly double it. Follow the exact [join, rebalance, drain, and remove runbook](cluster.md).

## Operate SlimVector

At minimum:

1. Route traffic with `/health/ready`, not liveness.
2. Scrape `/metrics` on every node and retain node/group labels.
3. Alert on readiness loss, unavailable or under-replicated groups, sustained 429s, queue saturation, Raft lag, memory growth, geo divergence, and backup failures.
4. Verify backups automatically and restore one into an isolated environment regularly.
5. Upgrade or restart one Raft voter at a time and wait for replication lag to return to zero.
6. Drain a node and complete its approved movement plan before deleting it.
7. Benchmark the intended vectors, filters, index parameters, durability mode, and wire format on representative hardware.

## Production security checklist

- Put public data endpoints behind TLS, authentication, authorization, request limits, and tenant isolation at a trusted gateway; SlimVector does not provide built-in end-user identity or row-level authorization.
- Do not expose `/internal/*`, Raft ports, storage volumes, or administrator routes to untrusted networks.
- Enable administrator endpoints only when required and inject a unique 32+ character key from a secret manager. In cluster mode this key also signs internal data/query RPC payloads.
- Use a distinct `GeoReplication:SharedSecret` and a distinct 32-byte backup encryption key.
- Never enable request-body logging: documents, metadata, and vectors may be sensitive.
- Run one data volume per node on local durable storage; never mount a live volume on two nodes.

See [security](security.md) for the complete trust model.

## Known boundaries

- Collection dimension and distance metric are immutable.
- Transactions do not span physical data groups.
- Geographic replication is single-writer disaster recovery, not automatic multi-primary replication.
- Restore supports backup boundaries, not arbitrary point-in-time recovery.
- Live distributed storage v1 to v2 migration is not rolling; use verified backup/restore into a new cluster.
- The benchmark suite reports machine-specific evidence, not universal product guarantees.

For details, use the [documentation index](README.md), the generated OpenAPI document, and [troubleshooting](troubleshooting.md).
