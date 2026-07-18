# HTTP API

The default prefix is `/api/v1`; `Api:RoutePrefix` can change it. JSON uses camel case and string enums. OpenAPI is available at `/openapi/v1.json`.

## Collections

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/collections` | Create a collection |
| `POST` | `/collections/get-or-create` | Return a compatible collection or create it |
| `GET` | `/collections` | List collections |
| `GET` | `/collections/{name}` | Get one collection |
| `PATCH` | `/collections/{name}` | Rename or change vector-index settings |
| `DELETE` | `/collections/{name}` | Delete a collection |

Creation requires `name` and `dimension`; `metric` defaults to `cosine`, and vector index `kind` defaults to `auto`. Metrics are `cosine`, `dotProduct`, and `euclidean`; index kinds are `auto`, `flat`, and `hnsw`.

## Documents

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/collections/{name}/documents/add` | Insert new ids |
| `POST` | `/collections/{name}/documents/upsert` | Insert or replace |
| `PATCH` | `/collections/{name}/documents` | Patch text, vector, and/or metadata |
| `GET` | `/collections/{name}/documents` | Page documents; repeat `ids=` to select ids |
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

With `atomic=true` (the default), one invalid item rejects the entire request and nothing is persisted. With `atomic=false`, valid items commit and the response reports stable `errorCode`/`errorMessage` values for failed items. Admission saturation returns `429 queue_saturated`; a payload over the configured adaptive-batch byte limit returns `400 write_too_large`.

Metadata values support null, string, boolean, integral and floating-point numbers, RFC-compatible date/time values, GUIDs, and simple arrays. JSON numbers without a fractional part are stored as integral values.

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

Errors are RFC Problem Details with `code` and `traceId` extensions. Common stable codes include `collection_not_found`, `document_not_found`, `dimension_mismatch`, `invalid_filter`, `text_too_large`, `queue_saturated`, `request_too_large`, `not_leader`, `quorum_unavailable`, and `read_only_secondary`.

A cluster follower returns `307 Temporary Redirect` for a leader-only write when the leader's API address is known. Clients must preserve method and body when following it; the provided .NET client uses `HttpClient`, which supports 307 redirects by default.

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
