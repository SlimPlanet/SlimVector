# SlimVector Studio

> [Documentation index](README.md) · [User guide](user-guide.md) · [Document ingestion](document-ingestion.md)

SlimVector Studio is the batteries-included local web application for exercising the database and the document-ingestion library. It hosts `AddSlimVector` in the same ASP.NET Core process: starting the Studio starts the database, write scheduler, storage engine, consensus coordinator, backup service, ingestion pipeline, and UI together.

## Start

```bash
dotnet run --project src/SlimVector.Studio
```

Open `http://localhost:5187`. On first launch the host creates the durable `documents` collection with cosine distance, automatic Flat/HNSW selection, metadata indexing, and 384 dimensions.

The first vector ingestion or vector/hybrid query downloads the local model. Use the **Prepare model** action on the overview page to do this explicitly. Once the status is green, the Studio can vectorize offline.

## Interface

### Overview

The overview reports collection and chunk counts, local model status and selected architecture variant, total search operations, and quick actions. The global collection selector in the header controls ingestion, search, and the document explorer.

### Ingestion

Drop multiple PDF, DOCX, PPTX, text, or Markdown files and choose:

- target collection;
- recursive, paragraph, or sentence chunking;
- target/maximum/overlap token sizes;
- arbitrary scalar or homogeneous-array JSON metadata;
- replacement by source file name;
- atomic batch behavior;
- preview-only behavior.

The response shows extraction counts and timings, every chunk's locations and token estimate, and a short vector preview. Stored chunks receive reserved metadata keys:

| Key | Meaning |
| --- | --- |
| `source_file` | original upload file name |
| `content_sha256` | full source content hash |
| `document_id` | deterministic file-stem/hash identifier |
| `document_format` | PDF, Word, PowerPoint, or plain text |
| `chunk_sequence` / `chunk_tokens` | ordered chunk position and size |
| `locations` | contributing pages, slides, or sections |
| `heading` / `title` | structural context when present |
| `ingested_at` | UTC ingestion time |

When replacement is enabled, new chunks are committed before stale chunks with the same `source_file` are removed. Re-uploading identical content is idempotent through upsert identifiers.

### Query lab

The query lab exposes every `SearchMode`:

- **Vector** vectorizes the query locally and searches the collection's active vector index.
- **BM25** uses the persistent text index only.
- **Hybrid** vectorizes locally and fuses independent vector and text ranks with configurable weights.
- **Metadata** performs a filter-only query and does not require text.

Top K, leader/linearizable/stale read consistency, returned text/vector/metadata/score fields, and scalar metadata filters are configurable. Results display fused score, vector and text ranks, source metadata, elapsed database time, and optional full vectors.

### Documents and collections

The document explorer pages through stored records, optionally loads full vectors, shows metadata/version/update time, and performs atomic multi-delete. The manual mutation dialog exposes add, upsert, update, and delete with either a supplied JSON vector or local automatic vectorization.

Collection management exposes names, dimensions, cosine/dot-product/Euclidean metrics, Flat/HNSW/Auto selection, and HNSW `M`, `efConstruction`, and `efSearch`. Dimensions and distance metrics are immutable; create a new collection to change them.

### Exploitation and backups

The exploitation page shows readiness, execution mode, managed memory, loaded collections, search/index counters, adaptive write batches and queue depth, plus each network Raft group when running in cluster mode. In the default direct single-node mode it correctly reports that no Raft transport group is required.

Backup actions create a logical incremental snapshot, list manifests, verify checksums, restore one collection under an optional new name, or restore the full catalog. Full restore requires entering `RESTORE` explicitly.

## Configuration

Studio settings live in `src/SlimVector.Studio/appsettings.json` and can be overridden with normal ASP.NET Core configuration:

```json
{
  "Storage": { "Path": "data/studio", "FlushToDisk": true },
  "Backup": { "Path": "backups/studio" },
  "Studio": {
    "MaximumUploadBytes": 134217728,
    "DefaultCollection": "documents",
    "ModelDirectory": null,
    "AutoDownloadModel": true
  }
}
```

Examples:

```bash
Storage__Path=/srv/slimvector/data \
Backup__Path=/srv/slimvector/backups \
Studio__ModelDirectory=/srv/slimvector/models/minilm \
dotnet run --project src/SlimVector.Studio
```

The request-body and multipart limits are derived from `Studio:MaximumUploadBytes`. The model cache can be mounted read-only after it has been pre-populated; set `Studio:AutoDownloadModel=false` in that deployment.

All ordinary SlimVector sections remain available, including vector/text/metadata index settings, adaptive batching, backpressure, Raft, geo replication, and backup providers. See [configuration](configuration.md).

## Privacy and network behavior

Document bytes, extracted text, chunks, queries, and embeddings stay in the Studio process and SlimVector storage. The only outbound request made by the default configuration is the one-time download of the pinned tokenizer and ONNX model from Hugging Face. There is no telemetry exporter or remote inference provider in the Studio.

Studio is intended for local exploration and trusted operator workflows. Its `/studio/api` surface is not a multi-tenant authentication boundary; place a remotely exposed instance behind TLS and access control, keep upload limits enabled, and follow [security](security.md).
