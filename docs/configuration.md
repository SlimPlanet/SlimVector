# Configuration

Every section binds to a dedicated C# options type and is validated with `ValidateOnStart`. Environment variables replace `:` with `__`; arrays use numeric suffixes, for example `AutoIndex__AllowedIndexes__0=Hnsw`.

Complete examples are [single-node appsettings](../src/SlimVector.Api/appsettings.json), [cluster appsettings](../config/appsettings.Cluster.example.json), and [evolving Compose cluster](../compose.cluster.yml).

| Section | Controls |
| --- | --- |
| `Storage`, `Collections` | durable root/fsync/compaction; lazy-open idle timeout, sweep, open limit |
| `VectorIndex` | hybrid candidate multiplier, search limit, legacy Auto threshold |
| `AutoIndex` | allowed kinds, assessment/migration intervals, hysteresis, recall/gain floors, validation sample, size/memory/churn thresholds |
| `Hnsw` | `M`, `EfConstruction`, `EfSearch` defaults for new collections |
| `Ivf` | list/probe counts and deterministic training iterations |
| `PQ` | subvectors, centroids, iterations, exact-rerank multiplier |
| `DiskAnn` | artifact path, degree/search/beam, delta merge, page/cache sizes, retained generations |
| `TextIndex`, `MetadataIndex` | BM25 limits/scoring and filter indexing/depth |
| `Raft`, `ClusterMembership` | bootstrap or joining mode, group count, election/snapshot/transport, warm-up/lag/timeouts/minimum voters |
| `Rebalancing` | controller enablement, manual approval, move concurrency, reconciliation/cooldown intervals and minimum improvement |
| `AdaptiveBatching`, `Backpressure` | bounded command/byte/window targets, global/client/collection/group queues and concurrency |
| `RateLimit` | global/client/collection/read/write/admin token buckets, reserved read/write fractions, adaptive recovery |
| `Backup`, `GeoReplication` | provider/schedule/retention/encryption/S3; separate signed DR outbox/receiver |
| `Api`, `Observability` | prefix/body/batch/timeout/admin auth; metrics/logging/slow-query threshold |

The HNSW/IVF/PQ/DiskANN sections populate a new collection's configuration when `vectorIndex` is omitted. An explicitly supplied collection configuration is stored in the catalog and remains authoritative across restart.

## Bootstrap versus join mode

Initial cluster voters set `Raft:JoinExistingCluster=false` and provide at least three unique `Members` plus matching `MemberApiEndpoints`, including the local endpoint. A new server sets `JoinExistingCluster=true` with both arrays empty. It starts non-voting and is installed into each group only through the authenticated membership API and Raft consensus.

Critical startup failures include invalid/duplicate topology, missing local or API mappings, exhausted port offsets, bad election/heartbeat ratios, empty Auto allowed sets, non-divisible fixed IVF-PQ dimensions, invalid DiskANN page/cache/retention bounds, inconsistent queue/token reserves, unsafe timeouts, weak administrator/geo secrets, invalid backup encryption keys, and incomplete S3 credentials.

## Placement controller

`Rebalancing:ManualApproval` defaults to `true`: planning is read-only and a catalog leader starts no move until an administrator approves the returned plan. `MaximumConcurrentMoves` bounds simultaneous source/target pairs. With manual approval disabled, the hosted controller plans and advances moves every `ReconcileInterval`; setting `Enabled=false` disables background progression while keeping explicit administrator actions available.

## Rate and congestion policy

Normal limits are token buckets at four scopes: global, client/API key, collection, and operation. The global pool is divided into shared, read-reserved, and write-reserved capacity; administrative requests use their separately bounded bucket and are not blocked by data-plane congestion. Adaptive refill drops immediately under queue, memory, Raft lag/quorum, or error pressure and recovers gradually over `RecoveryWindow`.

A contractual limit and congestion refusal are distinct 429 responses. Both include `Retry-After`; `X-SlimVector-RateLimit-Kind` is `contractual` or `congestion`, and the scope/reason is returned separately.

## Secrets

Do not commit `Api:AdminApiKey`, `GeoReplication:SharedSecret`, backup encryption keys, or S3 credentials. Inject them with an orchestrator secret store. Administrator and geo secrets must be independent. Generate a backup AES-256 key with `openssl rand -base64 32` and retain old keys for the lifetime of backups encrypted with them.
