# Testing and benchmarks

Run deterministic quality gates from the repository root:

```bash
dotnet format SlimVector.slnx --verify-no-changes
dotnet build SlimVector.slnx -c Release
dotnet test SlimVector.slnx -c Release
```

The test projects cover domain validation; SIMD distances; Flat/HNSW, BM25, metadata, rank fusion, and persisted index snapshots; immutable storage/crash reconciliation/compaction; application CRUD, lazy eviction, adaptive batching/backpressure, backup/S3/restore; actual multi-process TCP Raft elections, failover, catch-up, snapshot install, partitions, and multiple groups; geographic outage/restart/divergence; and HTTP/client/admin/redirect behavior.

`SlimVector.DocIngestor.Tests` generates valid PDF, DOCX, and PPTX files in memory and verifies format routing, page/slide/heading provenance, bounded overlapping chunks, deterministic identifiers, and pipeline behavior. `SlimVector.Studio.Tests` starts the complete web host with an isolated database and deterministic test embedding generator; it exercises the static UI, automatic default collection, ingestion, all four search modes, manual mutations, runtime telemetry, and backup verification without downloading a model.

For a real local-model check, run the Studio, click **Prepare model**, ingest a document, and issue a vector or hybrid query. This uses the architecture-specific ONNX artifact and therefore complements the deterministic CI tests.

## Native AOT smoke

```bash
chmod +x scripts/native-aot-smoke.sh
scripts/native-aot-smoke.sh
```

The script selects the host RID, publishes self-contained Native AOT with full trimming, starts the native executable against a temporary durable directory, waits for readiness, executes collection/document/hybrid-search calls, verifies metrics, and cleans up the process/data. `SLIMVECTOR_SMOKE_PORT` can select a different local port.

## Benchmarks

List or run BenchmarkDotNet cases:

```bash
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --list flat
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --filter '*HybridIndex*'
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --filter '*AdaptiveBatching*'
```

The suite includes allocation diagnostics and parameterized cases for exact/HNSW/text/metadata/hybrid queries, Raft MemoryPack serialization at multiple batch sizes, concurrent adaptive-scheduler throughput, and filesystem cold load at multiple collection sizes. Benchmark results are machine-specific and are not committed as product guarantees.
