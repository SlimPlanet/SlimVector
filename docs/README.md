# SlimVector documentation

This documentation describes the repository as implemented: .NET 10, JSON and MessagePack data-plane APIs, MemoryPack internal and durable high-volume formats, single-node RF1 operation, shared-nothing RF3 scale-out, and optional geographic disaster recovery.

## Start here

| Goal | Read |
| --- | --- |
| Install SlimVector and run the first hybrid query | [User guide](user-guide.md) |
| Ingest PDF, DOCX, PPTX, Markdown, or text without generating vectors yourself | [Studio](studio.md) and [document ingestion](document-ingestion.md) |
| Integrate over HTTP or with .NET | [HTTP API](api.md) and the [.NET client walkthrough](user-guide.md#use-the-net-client) |
| Configure a durable local deployment | [Single-node operation](single-node.md) and [configuration](configuration.md) |
| Add servers and redistribute storage | [Shared-nothing cluster](cluster.md) |
| Operate, monitor, back up, or recover a deployment | [Observability](observability.md), [backup and restore](backup-restore.md), and [troubleshooting](troubleshooting.md) |

## User and application references

- [User guide](user-guide.md): installation, CRUD, all search modes, filters, pagination, .NET client, deployment choices, and production checklist.
- [HTTP API](api.md): media types, routes, request semantics, stable errors, and administrator endpoints.
- [SlimVector Studio](studio.md): browser-based local ingestion, search, collection management, telemetry, and backups.
- [Document ingestion](document-ingestion.md): extraction, chunking, multilingual local embeddings, dependency injection, and supported formats.
- [Indexes and search](indexes.md): Flat, HNSW, IVF-Flat, IVF-PQ, DiskANN, BM25, metadata indexes, and hybrid ranking.

## Operations and deployment

- [Configuration](configuration.md): typed sections, defaults, environment overrides, validation, placement, admission, and secrets.
- [Single-node operation](single-node.md): local and Docker startup, durability, lazy loading, and backup expectations.
- [Shared-nothing cluster](cluster.md): three catalog voters, RF3 data groups, node join/drain/remove, capacity, rebalance, and consistency.
- [Geographic replication](geo-replication.md): asynchronous or synchronous DR transport, RPO/RTO, promotion, and recovery.
- [Backup and restore](backup-restore.md): incremental deduplication, filesystem/S3 providers, encryption, verification, and restore runbooks.
- [Observability](observability.md): health, Prometheus metrics, structured logs, alerts, and saturation diagnosis.
- [Security](security.md): trust boundaries, TLS, authentication responsibilities, keys, sensitive data, and hardening checklist.
- [Troubleshooting](troubleshooting.md): startup, HTTP, quorum, placement, index, storage, replication, and Native AOT issues.

## Design and development

- [Architecture](architecture.md): request paths, consensus topology, authoritative state, indexes, DR boundary, and backups.
- [Storage](storage.md): on-disk layout, MemoryPack segments, recovery, compaction, and v1-to-v2 migration.
- [Testing and benchmarks](testing.md): quality gates, Native AOT smoke, statistical E2E runner, capacity planner, and artifacts.
- Architecture decisions: [ADR 0001](adr/0001-memorypack-native-aot-warnings.md), [ADR 0002](adr/0002-multi-raft-groups.md), [ADR 0003](adr/0003-immutable-storage-and-derived-indexes.md), [ADR 0004](adr/0004-separate-geographic-replication.md), [ADR 0005](adr/0005-virtual-shards-placement-epochs.md), and [ADR 0006](adr/0006-shared-nothing-data-group-placement.md).

## Defaults worth knowing

- Public API prefix: `/api/v1`; OpenAPI: `/openapi/v1.json`.
- Health and metrics: `/health/live`, `/health/ready`, and `/metrics`.
- JSON is the public default; `application/vnd.msgpack` is optional on collection/document endpoints.
- Single-node is the default execution mode and persists below `Storage:Path`.
- Distributed data defaults to 1,024 immutable virtual shards per collection and RF3 placement.
- Administrator routes are absent until explicitly enabled and protected with a key of at least 32 characters.

Configuration examples are version-controlled in [`src/SlimVector.Api/appsettings.json`](../src/SlimVector.Api/appsettings.json), [`config/appsettings.Cluster.example.json`](../config/appsettings.Cluster.example.json), and the three Compose files at the repository root.
