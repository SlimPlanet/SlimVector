# Testing and benchmarks

> [Documentation index](README.md) · [User guide](user-guide.md) · [Observability](observability.md)

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

The deterministic capacity planner benchmark exercises RF3 at 3, 6 and 9 homogeneous nodes and fails if useful capacity is not linear or assigned-byte skew reaches 15%:

```bash
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --capacity-scale-out --node-capacity-gib 1024 --output artifacts/capacity
```

The Compose topology is validated in CI, but the real multi-container scale-out/failure drill requires a running Docker daemon. Start the three catalog voters, start joiner nodes, register them through `/admin/cluster/nodes/join`, approve the suggested plan, then verify topology and per-node `data-groups` directories before and after shard redistribution.

Run reproducible measured end-to-end profiles with:

```bash
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Smoke
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Standard --baseline artifacts/benchmarks/<run>/benchmark-results.json
# Large is intentionally for a dedicated machine.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Large

# Manual open-loop saturation sweep; latency starts at the intended dispatch time.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Saturation --storage-mode durable

# Short custom saturation probe.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Saturation --saturation-warmup-seconds 2 --saturation-stage-seconds 10 --saturation-rates 100,200,400
# Restrict a diagnostic run and override the statistical protocol when needed.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Smoke --indexes Flat-Float32,Hnsw --repetitions 3 --warmups 1 --operation-count 512
# Compare buffered and durable HTTP persistence, or select only one mode.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Smoke --storage-mode both
# Compare JSON and MessagePack data-plane transport under the same server/index/storage workload.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Smoke --wire-format both
# Consume local fvecs/ivecs files; the runner never downloads datasets.
dotnet run --project benchmarks/SlimVector.Benchmarks -c Release -- --e2e --profile Smoke --dataset fvecs --vectors-path corpus.fvecs --queries-path queries.fvecs --truth-path truth.ivecs
```

The schema-v5 runner is a coordinator: it randomizes the scenario order and starts every warm-up or measured fork in an isolated child process. Smoke defaults to one warm-up plus five measured forks; Standard uses the same protocol and Large uses one plus three. Index cold-load runs in another fresh process after separate `SerializeSnapshot` and `DurableSnapshotWrite` phases. OS page-cache state is recorded as uncontrolled; the benchmark never requires privileged cache manipulation.

The default clustered synthetic workload uses independent perturbed queries, Zipf-distributed hot clusters, varied text, and typed metadata. HNSW, IVF and DiskANN run search-parameter sweeps, and recommendations are emitted only at recall thresholds 0.90, 0.95 and 0.99. HTTP CRUD uses the selected configuration for every index family in buffered and durable modes. `--wire-format json` is the backward-compatible default; `messagepack` selects only binary transport and `both` creates paired server scenarios while keeping each fork isolated. Queue saturation, adaptive congestion and contractual rate limiting remain one separate JSON control scenario, so selecting both formats does not duplicate expected-rejection probes.

Every operation reports its latency unit and batch size, fork-level wall/throughput distributions, deterministic bootstrap confidence interval, qualified p50/p95/p99, CPU core-equivalents, normalized CPU, RSS/private-memory samples, managed allocations and GC, logical storage bytes and durable flushes. p50 requires two samples, p95 twenty and p99 one hundred; unavailable metrics are serialized as `null` and rendered as `n/a`. Directory-size changes are named artifact deltas and are not described as physical I/O.

Every versioned run writes `benchmark-summary.md`, a sortable/filterable HTML report with recall/latency and CPU/RAM charts, scenario-level `benchmark-results.csv`, phase-level `benchmark-operations.csv`, `benchmark-resource-samples.csv`, and nested JSON under `artifacts/benchmarks`. MessagePack scenarios have a `-MessagePack` suffix; unchanged JSON scenario names preserve comparison with older schema-v5 JSON runs. Baselines must use schema v5 and have the same dataset, scenario, runtime, GC, OS/CPU and repetition fingerprint. A regression is flagged only when confidence intervals do not overlap and both relative and absolute thresholds are exceeded. Add `--fail-on-regression` to turn significant regressions or incompatible baselines into a non-zero exit code. Use `--server-indexes`, `--recall-thresholds`, `--storage-mode`, and `--wire-format` to narrow the matrix without changing the recorded fingerprint.

## Interpret an E2E report

- Compare only runs whose baseline fingerprint is compatible. A faster result on another CPU, dataset, dimension, index matrix, durability mode, runtime, or GC configuration is not an isolated product comparison.
- For approximate indexes, choose the fastest tuning point that satisfies the required recall threshold. A low-latency point below the threshold is explicitly non-eligible.
- Prefer the median and bootstrap confidence interval across forks. p95 requires at least 20 operation samples and p99 at least 100; `n/a` is evidence that the sample is insufficient, not zero latency.
- Separate operation latency from phase duration and throughput. Mutation latency is labeled as a batch-of-16 latency where applicable, while throughput remains documents/s.
- Treat `averageCpuCoreEquivalent` as used cores and `normalizedCpuUtilization` as the machine-normalized ratio. Sampled peaks are unavailable for phases too short to collect two observations.
- Storage byte counters are logical instrumented reads/writes. `artifactSizeDeltaBytes` is directory growth and must not be compared with physical device IOPS or write amplification.
- Expected queue/congestion/contractual rejections belong to their own counters. Only unexpected failures contribute to the main error rate.

Cross-product comparisons require the same client location, dataset/truth set, recall target, concurrency model, durability and replication policy, warm state, network path, and cost envelope. Published vendor numbers that do not expose those controls are context, not a defensible head-to-head baseline.
