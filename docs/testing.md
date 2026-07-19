# Testing and benchmarks

Run deterministic quality gates from the repository root:

```bash
dotnet format SlimVector.slnx --verify-no-changes
dotnet build SlimVector.slnx -c Release
dotnet test SlimVector.slnx -c Release
```

The test projects cover domain validation; SIMD Flat, HNSW, IVF-Flat, IVF-PQ, scalar quantization and SSD DiskANN persistence/recall/mutation/rollback; Auto selection, online migration, failed-candidate isolation and restart-safe rollback; BM25, metadata and hybrid fusion; immutable storage/crash reconciliation/compaction; CRUD, eviction, batching, backpressure and reserved/fair token buckets; backup/S3/restore; actual TCP Raft election/failover/snapshot/partition/multi-group plus fourth-member warm-up, restart and removal; geographic outage/restart/divergence; and HTTP/client/admin/429/redirect behavior.

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

Run reproducible measured end-to-end profiles with:

```bash
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Smoke
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Standard --baseline artifacts/benchmarks/<run>/benchmark-results.json
# Large is intentionally for a dedicated machine.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Large
```

The runner measures all vector kinds and quantizations against exact recall, migration build, a real SlimVector HTTP process with vector/text/hybrid traffic plus concurrent reads/writes, controlled backpressure and rate-limit saturation, and a real four-node DotNext add/catch-up path. It samples CPU and working set, records idle/average/peak memory, managed-memory release after index disposal, GC/LOH/pause data, disk size/write rate, error rate, latency percentiles, throughput, recall, build/persist/cold-load timing, and the exact workload/index/admission configuration. Every versioned run writes `benchmark-summary.md`, HTML charts, CSV, JSON, and environment/version metadata under `artifacts/benchmarks`. Missing profiles are not synthesized; only an executed profile produces a report. A supplied baseline adds p95 regression percentages.
