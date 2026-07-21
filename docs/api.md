# HTTP API

> [Documentation index](README.md) · [User guide](user-guide.md) · Generated contract: `/openapi/v1.json`

The default prefix is `/api/v1`; `Api:RoutePrefix` can change it. JSON remains the default and uses camel case and string enums. Collection and document endpoints also accept and return cross-language MessagePack with `Content-Type: application/vnd.msgpack` and `Accept: application/vnd.msgpack`. OpenAPI advertises both media types at `/openapi/v1.json`; administration endpoints remain JSON control-plane APIs.

MessagePack bodies use maps with the same camel-case field names as JSON. Enums and `DateTimeOffset` values remain human-portable strings, vectors use native float arrays, and arbitrary metadata maps MessagePack nil/bool/integer/float/string/array/map values to the existing JSON data model. The decoder uses the untrusted-data security profile and generated contracts only: typeless payloads and runtime contract discovery are not enabled.

The typed .NET client selects the format once while keeping JSON as its compatible default. It currently targets the default `/api/v1` prefix:

```csharp
SlimVectorClient json = new(http);
SlimVectorClient binary = new(http, SlimVectorWireFormat.MessagePack);
```

Request and response formats are negotiated independently. For example, a JSON request with `Accept: application/vnd.msgpack` receives a MessagePack response. Error responses use the selected representation too; the MessagePack form carries the same stable Problem Details fields and error `code`.

## Collections

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/collections` | Create a collection |
| `POST` | `/collections/get-or-create` | Return a compatible collection or create it |
| `GET` | `/collections` | List collections |
| `GET` | `/collections/{name}` | Get one collection |
| `PATCH` | `/collections/{name}` | Rename or change vector-index settings |
| `DELETE` | `/collections/{name}` | Delete a collection |

Creation requires `name` and `dimension`; `metric` defaults to `cosine`, and vector index `kind` defaults to `auto`. Metrics are `cosine`, `dotProduct`, and `euclidean`; index kinds are `auto`, `flat`, `hnsw`, `ivfFlat`, `ivfPq`, and `diskAnn`. Scalar `quantization` is `float32`, `float16`, or `int8`. Requests can also set HNSW, IVF, PQ, rerank, and DiskANN degree/search/beam/delta/page/cache/retention fields; invalid combinations fail before catalog replication.

## Documents

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/collections/{name}/documents/add` | Insert new ids |
| `POST` | `/collections/{name}/documents/upsert` | Insert or replace |
| `PATCH` | `/collections/{name}/documents` | Patch text, vector, and/or metadata |
| `GET` | `/collections/{name}/documents` | Page documents; repeat `ids=` to select ids; returns `continuationToken` |
| `POST` | `/collections/{name}/documents/delete` | Delete ids |
| `GET` | `/collections/{name}/documents/count` | Count documents |
| `POST` | `/collections/{name}/documents/query` | Search |

Add and upsert accept a natural grouped envelope:

```json
{
  "atomic": false,
  "documents": [
    {
      "id": "doc-1",
      "text": "hybrid search",
      "vector": [0.25, 0.5, 0.75],
      "metadata": { "published": true, "year": 2026, "tags": ["search", "dotnet"] }
    }
  ]
}
```

With `atomic=true` (the default), one invalid item rejects the entire request and nothing is persisted. With `atomic=false`, valid items commit and the response reports stable `errorCode`/`errorMessage` values for failed items. Admission control returns 429 with `Retry-After`, `X-SlimVector-RateLimit-Kind` (`contractual` or `congestion`), and `X-SlimVector-RateLimit-Scope`. Queue saturation remains `queue_saturated`; a payload over the configured adaptive-batch byte limit returns `400 write_too_large`.

Metadata values support null, string, boolean, integral and floating-point numbers, dates, GUIDs, and homogeneous simple arrays. JSON dates and GUIDs use tagged objects so ordinary strings never change type implicitly: `{"$date":"2026-07-21T10:00:00Z"}` and `{"$guid":"44e7c508-38f4-42cf-a9d8-f0cdbc3903db"}`. JSON numbers without a fractional part are stored as integral values. MessagePack integer and floating-point tokens preserve the same distinction.

`PATCH` changes only the supplied document fields, but a supplied `metadata` object replaces that document's complete metadata map; it is not merged key by key. In distributed mode, `atomic=true` also requires every document in a mutation request to resolve to the same physical data group. A cross-group atomic request fails with `cross_shard_atomic_unsupported`; use `atomic=false` to partition it and inspect the per-document results.

Distributed pagination should pass the opaque `continuationToken` returned by the previous page. It is bound to the collection and placement epoch, so a topology cutover invalidates it rather than returning duplicates or omissions; restart from the first page after that error. `offset` remains available for compatibility but is capped by `Api:MaximumDocumentOffset` (10,000 by default).

## Query

`mode` is `vector`, `text`, `hybrid`, or `metadata`. Required inputs follow the mode. `limit` defaults to 10. `include` accepts `text`, `vector`, `metadata`, and `scores`; omitted include uses text, metadata, and scores. `consistency` is `leader`, `linearizable`, or `stale`.

```json
{
  "mode": "hybrid",
  "text": "distributed vector database",
  "vector": [1, 0, 0],
  "limit": 20,
  "vectorWeight": 0.6,
  "textWeight": 0.4,
  "consistency": "linearizable",
  "filter": {
    "operator": "and",
    "operands": [
      { "operator": "greaterThanOrEqual", "field": "year", "value": 2025 },
      { "operator": "in", "field": "tags", "values": ["dotnet", "vector"] },
      { "operator": "not", "operands": [{ "operator": "equal", "field": "draft", "value": true }] }
    ]
  }
}
```

Filter operators are `equal`, `notEqual`, `greaterThan`, `greaterThanOrEqual`, `lessThan`, `lessThanOrEqual`, `in`, `exists`, `and`, `or`, and `not`. The configured depth limit is enforced iteratively before evaluation.

## Errors and redirects

Errors are RFC Problem Details with `code` and `traceId` extensions. Common stable codes include `collection_not_found`, `document_not_found`, `dimension_mismatch`, `invalid_filter`, `text_too_large`, `queue_saturated`, `request_too_large`, `not_leader`, `quorum_unavailable`, `membership_conflict`, `membership_member_not_found`, and `read_only_secondary`.

Clients should branch on `code` and HTTP status, not the explanatory `detail`. For 429 responses, honor `Retry-After` and inspect `X-SlimVector-RateLimit-Kind`/`X-SlimVector-RateLimit-Scope`. Follow 307 redirects without changing the method, and apply bounded backoff with jitter during elections or quorum loss.

Any API node accepts data operations. It resolves document → virtual shard → data group and forwards writes to that group's leader over the authenticated HTTP/2/MemoryPack internal API. A stale placement epoch is rejected and rerouted. Legacy expert membership operations can still return `307 Temporary Redirect` when they must execute on a particular Raft leader.

## Backup administration

Backup endpoints are absent unless `Api:AdminEndpointsEnabled=true` and a key of at least 32 characters is configured. Send it in `X-SlimVector-Admin-Key`.

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/admin/backups` | Create and verify a backup |
| `GET` | `/admin/backups` | List manifests |
| `POST` | `/admin/backups/{id}/verify` | Verify manifest and all blobs |
| `POST` | `/admin/backups/{id}/restore` | Full destructive restore |
| `POST` | `/admin/backups/{id}/restore-collection` | Restore a collection |

The collection restore body is `{"collectionName":"source","restoredName":"copy","overwrite":false}`. Treat the administrator key as a secret and place these routes behind TLS and network access control.

## Index and cluster administration

These routes use the same administrator key:

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/admin/collections/{name}/index` | Active/previous generation, kind and migration state/reason |
| `POST` | `/admin/collections/{name}/index/rollback` | Atomically swap to the retained generation |
| `GET` | `/admin/cluster/membership` | Groups, members, leader/transport/synchronization and active change |
| `POST` | `/admin/cluster/membership/add` | Warm up and consensus-add one group endpoint |
| `POST` | `/admin/cluster/membership/promote` | Verify safe catch-up/promotion completed |
| `POST` | `/admin/cluster/membership/demote` | Stage a serialized safe removal |
| `POST` | `/admin/cluster/membership/remove` | Consensus-remove after safety checks |
| `POST` | `/admin/cluster/membership/transfer-leadership` | Resign the local group leader before maintenance |
| `GET` | `/admin/cluster/nodes/topology` | Nodes, zones, capacities, data-group replica sets, epochs and durable movements |
| `POST` | `/admin/cluster/nodes/join` | Register an API/data node without moving data |
| `POST` | `/admin/cluster/nodes/{nodeId}/drain` | Stop new placement on a node and mark it for evacuation |
| `DELETE` | `/admin/cluster/nodes/{nodeId}` | Remove an already evacuated node |
| `GET` | `/admin/cluster/nodes/rebalance/plan` | Estimate replica sets, reasons, bytes and utilization before/after |
| `POST` | `/admin/cluster/nodes/rebalance/approve` | Persist and start an approved replica movement plan |
| `GET` | `/admin/cluster/rebalance/plan?drainDataGroupId=data-0` | Build a read-only placement plan |
| `POST` | `/admin/cluster/rebalance/approve` | Approve a plan by `planId` |
| `GET` | `/admin/cluster/rebalance/status` | List durable in-flight shard moves |
| `POST` | `/admin/cluster/rebalance/advance` | Advance each move by one resumable phase |
| `POST` | `/admin/cluster/rebalance/pause` | Pause automatic move progression |
| `POST` | `/admin/cluster/rebalance/resume` | Resume automatic move progression |

Membership bodies use `{"groupId":"data-0","endpoint":"10.0.0.14:3263"}`; leadership transfer needs only `groupId`. Perform topology changes for the catalog and every data group.

The rebalance approval body is `{"planId":"..."}`. Only the catalog leader may plan, approve, or advance. A move is persisted as `copying`, `catchingUp`, `switching`, and `draining`; status includes routing epoch and snapshot/replay high-water marks. Plans expire with the controller process, but approved moves and their checkpoints survive restart.

Public collection/document routes do not implement end-user authentication or row-level authorization. Administrator endpoints use one shared operational key. See [security](security.md) before exposing either surface outside a trusted development network.
