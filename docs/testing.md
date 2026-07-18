# Testing and benchmarks

Run deterministic quality gates from the repository root:

```bash
dotnet format SlimVector.slnx --verify-no-changes
dotnet build SlimVector.slnx -c Release
dotnet test SlimVector.slnx -c Release
```

The test projects cover domain validation; SIMD distances; Flat/HNSW, BM25, metadata, rank fusion, and persisted index snapshots; immutable storage/crash reconciliation/compaction; application CRUD, lazy eviction, adaptive batching/backpressure, backup/S3/restore; actual multi-process TCP Raft elections, failover, catch-up, snapshot install, partitions, and multiple groups; geographic outage/restart/divergence; and HTTP/client/admin/redirect behavior.

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
