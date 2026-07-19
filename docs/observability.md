# Observability

## Health

- `/health/live` confirms the process can answer HTTP.
- `/health/ready` returns 200 only when the consensus coordinator is ready; otherwise 503.
- `/metrics` returns Prometheus text when `Observability:MetricsEnabled=true`; otherwise 404.

Liveness must not be used to route writes. Readiness is the load-balancer signal.

## Metrics

Important series include:

- collections and memory: `slimvector_open_collections`, `slimvector_managed_memory_bytes`;
- search: request/failure/slow totals, mode totals, cumulative duration;
- indexes: load/failure totals, duration/documents and per-kind loads, migration attempts/failures/duration and last sampled recall; each open collection also exposes active kind/state, document count, estimated resident bytes, and persisted snapshot bytes;
- admission/batching: queue depth, request/completion/rejection totals, batches/items, per-shard target size, window, payload, and replication latency;
- Raft: leader flag, term, committed/applied indexes, applied commands, member count and membership-change state; per-member info includes endpoint/transport/leader/remote labels plus match index and observable replication lag;
- geo: pending/sent/failure/received/duplicate/divergence totals and lag seconds;
- backup/restore: successful/failed totals and deduplicated blobs.

Alert on readiness loss, any divergence, sustained geo pending growth, repeated backup failures, no successful backup within the expected interval, write rejections, queue depth near a configured capacity, applied index lag, and managed-memory growth alongside open collections.

Rates should be calculated by the monitoring system from counters. Cumulative duration divided by request count gives a coarse mean; use structured slow-query events for outliers.

## Structured logs

SlimVector logs identifiers and operational dimensions, never document text, vectors, or metadata. A query above `Observability:SlowQueryThreshold` records collection name, search mode, limit, and elapsed milliseconds. Unhandled API errors include only the trace id in production; Problem Details exposes the same trace id for correlation.

Configure JSON console formatting in the host/orchestrator when `StructuredConsoleLogging` is desired. Do not enable ASP.NET request-body logging in production because bodies contain sensitive user data.

## Saturation diagnosis

1. Compare global and per-shard queue depth with configured capacities.
2. Compare contractual and congestion rejection counters, adaptive-rate ratio, and the pressure reason in structured 429 logs.
3. Inspect target batch size/window and last replication latency. A large target with growing latency points at consensus/storage throughput; a small target with high queue depth can indicate payload or memory pressure.
4. Compare Raft commit/applied indexes and leader stability.
5. Check managed memory and open collection count; shorten idle timeout only if cold-load cost is acceptable.

Metrics contain node-local values. Scrape every node and retain `group`/`local` labels for Raft series.
