# SlimVector

SlimVector is a .NET 10 hybrid vector database with durable single-node operation, independent and dynamically evolving DotNext Raft groups, adaptive admission/batching, and separate geographic disaster-recovery replication. It combines exact SIMD, HNSW, IVF-Flat, IVF-PQ, scalar quantization and SSD DiskANN with BM25, typed metadata filters, and weighted reciprocal-rank fusion. Auto mode trains and validates index generations online with restart-safe rollback.

## Quick start

Requirements: .NET SDK 10.0.300 or newer in the 10.0 feature band.

```bash
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/SlimVector.Api
```

Create a collection and add two documents:

```bash
curl -X POST http://localhost:8080/api/v1/collections \
  -H 'Content-Type: application/json' \
  -d '{"name":"articles","dimension":3,"metric":"cosine","vectorIndex":{"kind":"hnsw","hnswM":16,"hnswEfConstruction":200,"hnswEfSearch":64}}'

curl -X POST http://localhost:8080/api/v1/collections/articles/documents/add \
  -H 'Content-Type: application/json' \
  -d '{"atomic":true,"documents":[{"id":"dotnet","text":"A distributed vector database in .NET","vector":[1,0,0],"metadata":{"year":2026,"tags":["dotnet","vector"]}},{"id":"other","text":"An unrelated document","vector":[0,1,0],"metadata":{"year":2024}}]}'
```

Run a hybrid query. Hybrid scores use weighted rank fusion; vector distance and BM25 scores are never added directly.

```bash
curl -X POST http://localhost:8080/api/v1/collections/articles/documents/query \
  -H 'Content-Type: application/json' \
  -d '{"mode":"hybrid","text":"vector database","vector":[1,0,0],"limit":5,"filter":{"operator":"greaterThanOrEqual","field":"year","value":2025},"include":["text","metadata","scores"]}'
```

OpenAPI is at `/openapi/v1.json`; liveness, readiness, and Prometheus text metrics are at `/health/live`, `/health/ready`, and `/metrics`.

## Document ingestion Studio

Run the all-in-one local Studio when you want to ingest and explore documents without creating vectors yourself:

```bash
dotnet run --project src/SlimVector.Studio
```

Open `http://localhost:5187`. The Studio starts an embedded single-node SlimVector database, creates a 384-dimension `documents` collection on first launch, and offers:

- drag-and-drop PDF, DOCX, PPTX, text, and Markdown extraction;
- recursive, paragraph, or sentence chunking with configurable target, maximum, and overlap;
- fully local multilingual ONNX embeddings with automatic ARM64/AVX2 model selection;
- vector, BM25, hybrid, and metadata-only search with consistency and returned-field controls;
- collection CRUD and HNSW settings, document inspection and manual batch mutations;
- Raft/write/search telemetry plus backup, verification, and restore workflows.

The first vector operation downloads the pinned Apache-2.0 Hugging Face model into the per-user application-data directory. It then works offline. Documents are never sent to a remote inference API. See [document ingestion](docs/document-ingestion.md) and the [Studio guide](docs/studio.md).

## Docker

Published multi-architecture images are available from GitHub Container Registry:

```bash
docker pull ghcr.io/slimplanet/slimvector:latest
docker run --rm -p 8080:8080 -v slimvector-data:/data ghcr.io/slimplanet/slimvector:latest
```

Every stable GitHub Release links to its immutable image digest and includes a checksummed archive of the released `Dockerfile`, `.dockerignore`, and Compose definitions.

The CI/CD release channel is selected from the last commit message, following the SlimFaas convention:

- `alpha` publishes `<next-version>-alpha.<run>` and updates the `alpha` image tag;
- `beta` publishes `<next-version>-beta.<run>` and updates the `beta` image tag;
- `release` on `main` publishes the stable semantic version, updates its major/minor and `latest` tags, and creates the GitHub Release;
- without one of these keywords, `main` publishes `<next-version>-dev.<run>` and updates `dev`; pull requests are built and tested without being pushed.

```bash
docker compose -f compose.single-node.yml up --build
docker compose -f compose.cluster.yml up --build
docker compose -f compose.cluster.yml --profile joiner up --build
docker compose -f compose.geo.yml up --build
```

The cluster publishes nodes on ports 8081–8083. The simulated geographic primary and read-only secondary are published on 8090 and 8091.

## Typed .NET client

```csharp
using SlimVector.Client;

HttpClient http = new() { BaseAddress = new Uri("http://localhost:8080") };
SlimVectorClient client = new(http);
await client.CreateCollectionAsync(new CreateCollectionRequest { Name = "articles", Dimension = 3 });
await client.AddDocumentsAsync("articles",
[
    new SlimVectorDocument { Id = "one", Text = "vector database", Vector = [1, 0, 0] },
]);
SlimVectorQueryResult result = await client.QueryAsync("articles", new SlimVectorQuery
{
    Mode = SlimVector.Domain.SearchMode.Hybrid,
    Text = "vector database",
    Vector = [1, 0, 0],
    Limit = 5,
});
```

JSON is the default public wire format. For vector-heavy client traffic, construct the same typed client with `new SlimVectorClient(http, SlimVectorWireFormat.MessagePack)`; collection/document endpoints and Problem Details then use `application/vnd.msgpack` without changing DTOs.

## Quality gates

```bash
dotnet format SlimVector.slnx --verify-no-changes
dotnet build SlimVector.slnx -c Release
dotnet test SlimVector.slnx -c Release
dotnet publish src/SlimVector.Api/SlimVector.Api.csproj -c Release -r linux-x64 --self-contained true
```

The API project publishes with Native AOT and `TrimMode=full`. The narrowly scoped generated-serializer aggregate-warning policy is explained in [ADR 0001](docs/adr/0001-memorypack-native-aot-warnings.md).

## Documentation

Start with the [user guide](docs/user-guide.md) for an end-to-end path from local installation to production operations. The [documentation index](docs/README.md) organizes all user, operator, architecture, and contributor references.

- [HTTP API](docs/api.md), [.NET client](docs/user-guide.md#use-the-net-client), and [configuration](docs/configuration.md)
- [Studio](docs/studio.md) and [document extraction, chunking, and local embeddings](docs/document-ingestion.md)
- [Single-node](docs/single-node.md), [shared-nothing cluster](docs/cluster.md), and [geographic replication](docs/geo-replication.md)
- [Indexes](docs/indexes.md), [storage](docs/storage.md), [backup and restore](docs/backup-restore.md)
- [Security](docs/security.md), [observability](docs/observability.md), and [troubleshooting](docs/troubleshooting.md)
- [Architecture and ADRs](docs/architecture.md), [testing, Native AOT smoke, and benchmarks](docs/testing.md)

## Projects

- `SlimVector.Domain`: immutable domain model and validation
- `SlimVector.Storage`: immutable segments, manifests, checksums, tombstones, compaction
- `SlimVector.Indexing`: Flat SIMD/quantization, HNSW, IVF-Flat/PQ, DiskANN, BM25, metadata, rank fusion
- `SlimVector.Protocol`: generated cross-project MessagePack contracts and formatters
- `SlimVector.Raft`: deterministic commands, catalog/data groups, snapshots, safe dynamic membership
- `SlimVector.Replication`: durable inter-zone outbox and signed receiver
- `SlimVector.Application`: lazy collection lifecycle, batching, backups, use cases
- `SlimVector.Api`: source-generated Minimal API, OpenAPI, health, metrics
- `SlimVector.Client`: source-generated typed HTTP client
- `SlimVector.DocIngestor`: PDF/DOCX/PPTX extraction, structured chunking, and local ONNX embeddings
- `SlimVector.Studio`: all-in-one web interface that starts SlimVector automatically
